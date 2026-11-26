using ToolRunner.Steps;

namespace ToolRunner;

// What a run will do, worked out before anything changes: the tools that run and the mod each one
// makes, the mods switched off for the run and why, the ones a step replaces for good, the steps that start the game. Read off
// the profile as it is - nothing is written.
public sealed class RunPreview
{
	public sealed record StepLine(string Key, string Name, string? Output, bool OutputIsNew);

	public List<StepLine> Steps { get; } = [];
	// the steps that are not run because the setup uses their old output, each as it stands now (OldOutput)
	public List<OldOutput.Look> UsesOld { get; } = [];
	// the profile the run is for, as the old outputs' lines name it
	string ProfileName { get; init; } = "";
	public List<(string Mod, string Why)> OffForTheRun { get; } = [];
	public List<string> ReplacedForGood { get; } = [];
	public List<string> StartsTheGame { get; } = [];
	public bool ClosesMo2 { get; private init; }
	// the ticked save steps a new game leaves out
	public List<string> LeftOutForNewGame { get; } = [];
	// the setup's own commands, as written: a setup file can come from anywhere, and what it runs on this PC is shown before it runs
	public List<string> CommandsBefore { get; } = [];
	public List<string> CommandsAfter { get; } = [];
	// the room the run takes on each disk (null: it could not be worked out - the check says why)
	public DiskForecast? Forecast { get; private set; }

	// a_last: each step's last runs (LastRuns.Build) when the caller has them - an old output that is used is then dated by the
	// run that made it, with what changed in the profile since
	public static RunPreview Build(Setup a_setup, Mo2Instance a_mo2, IReadOnlyList<Setup.StepEntry> a_todo, IReadOnlyList<LastRuns.Section>? a_last = null)
	{
		var control = new Mo2Control(a_mo2, a_setup.Profile, RunLog.Null);
		var profile = new Profile(a_mo2, a_setup.Profile, () => control.Running);
		var st = profile.States();
		var p = new RunPreview { ClosesMo2 = control.Running, ProfileName = a_setup.Profile };
		// only when a step past the shader caches runs do the outputs go off, and with them the old ones come back at their places
		if (a_todo.Any(s => s.Key is not "shaders" and not "playsave")) {
			p.UsesOld.AddRange(OldOutput.LookAt(a_setup, a_mo2, st, a_todo, a_last));
		}
		p.LeftOutForNewGame.AddRange(a_setup.Steps.Where(s => s.On && a_setup.LeftOutForNewGame(s.Key)).Select(s => StepCatalog.Name(s.Key)));
		p.CommandsBefore.AddRange(a_setup.BeforeRun);
		p.CommandsAfter.AddRange(a_setup.AfterRun);
		foreach (var s in a_todo) {
			var ss = new StepSettings(s.Settings);
			p.Steps.Add(new StepLine(s.Key, StepCatalog.Name(s.Key), ss.OutputMod, ss.OutputMod is { } om && !st.ContainsKey(om)));
			if (s.Key is "shaders" or "cleansave" or "playsave" || (s.Key == "grass" && ss.Str("by") == "game")) {
				p.StartsTheGame.Add(StepCatalog.Name(s.Key));
			}
		}
		// the same list the run switches off (Runner.OutputsOff), only when a step past the shader caches runs
		if (a_todo.Any(s => s.Key is not "shaders" and not "playsave")) {
			var off = Runner.OutputsToTurnOff(a_setup, st, a_todo);
			p.OffForTheRun.AddRange(off);
			foreach (var (m, why) in profile.Dependents(off.Select(o => o.Mod))) {
				p.OffForTheRun.Add((m, why));
			}
		}
		p.ReplacedForGood.AddRange(a_todo.SelectMany(s => new StepSettings(s.Settings).List("replaces"))
			.Where(m => st.TryGetValue(m, out var f) && f == '+').Distinct(StringComparer.OrdinalIgnoreCase));
		try {
			p.Forecast = DiskForecast.Build(a_setup, a_mo2, a_todo);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
			// a disk that will not answer leaves the preview without its forecast, never without the rest
		}
		return p;
	}

	// in plain words, for the window's box and the command line; a_usual gives a step's usual time when there is one
	public List<string> Lines(Func<string, TimeSpan?> a_usual)
	{
		var l = new List<string> { "The tools that run, in this order:" };
		var total = TimeSpan.Zero;
		var untimed = 0;
		for (var i = 0; i < Steps.Count; ++i) {
			var s = Steps[i];
			var t = a_usual(s.Key);
			total += t ?? TimeSpan.Zero;
			untimed += t == null ? 1 : 0;
			var made = s.Output == null ? "" : s.OutputIsNew ? $" - makes \"{s.Output}\" (a new mod)" : $" - remakes \"{s.Output}\"";
			l.Add($"  {i + 1}. {s.Name}{made}{(t is { } u ? $" (usually {Minutes(u)})" : "")}");
		}
		if (total > TimeSpan.Zero) {
			l.Add($"About {Minutes(total)} in all{(untimed > 0 ? $", plus {untimed} step(s) never timed yet" : "")}.");
		}
		if (UsesOld.Count > 0) {
			l.Add("");
			l.Add("NOT built again - the old output is used, as this setup says (the Steps list: Use old output):");
			foreach (var u in UsesOld) {
				l.Add($"  - {u.Name}: \"{u.Mod}\"");
				l.AddRange(OldOutput.Warning(u, ProfileName).Select(w => "      " + w));
			}
		}
		if (Forecast?.Lines() is { Count: > 0 } room) {
			l.Add("");
			l.AddRange(room);
		}
		if (OffForTheRun.Count > 0) {
			l.Add("");
			l.Add("Switched OFF while the run goes, back on when it ends (a remade output as soon as its tool is done):");
			l.AddRange(OffForTheRun.Select(o => $"  - {o.Mod} ({o.Why})"));
		}
		if (ReplacedForGood.Count > 0) {
			l.Add("");
			l.Add("Kept off after the run (replaced by what it makes):");
			l.AddRange(ReplacedForGood.Select(m => "  - " + m));
		}
		if (LeftOutForNewGame.Count > 0) {
			l.Add("");
			l.Add("Left out - this setup is set to a new game, so there is no save to keep clean: " + string.Join(", ", LeftOutForNewGame) + ".");
		}
		if (StartsTheGame.Count > 0) {
			l.Add("");
			l.Add("The game starts for: " + string.Join(", ", StartsTheGame) + ".");
		}
		if (ClosesMo2) {
			l.Add("");
			l.Add("Mod Organizer is open: the run closes it, and opens it again for each tool.");
		}
		if (CommandsBefore.Count + CommandsAfter.Count > 0) {
			l.Add("");
			l.Add("This setup runs its own commands on this PC (Settings > Run):");
			l.AddRange(CommandsBefore.Select(c => "  - before the first step: " + c));
			l.AddRange(CommandsAfter.Select(c => "  - after the run: " + c));
		}
		l.Add("");
		l.Add("Nothing has been changed yet.");
		return l;
	}

	static string Minutes(TimeSpan a_t) => a_t.TotalMinutes < 1 ? "under a minute" : a_t.TotalMinutes < 60 ? $"{a_t.TotalMinutes:0} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";
}
