using System.Diagnostics;
using System.Text.RegularExpressions;
using ToolRunner.Steps;

namespace ToolRunner;

// What is made ready before each game start of a run, in one place: the small quiet window, and the two mods below.
public static class GameStart
{
	// The copy of a file the game reads: Mod Organizer's overwrite outranks every mod, then the ticked mods by their place (the
	// first in the profile's mod order wins). The game's own Data folder is never looked at here: it is never written.
	public static string? Winning(Profile a_profile, string a_rel)
	{
		var ow = Path.Combine(a_profile.Mo2.OverwriteDir, a_rel);
		if (File.Exists(ow)) {
			return ow;
		}
		var states = a_profile.States();
		return a_profile.ModOrder().Where(m => states.TryGetValue(m, out var f) && f == '+').Select(m => Path.Combine(a_profile.Mo2.ModDir(m), a_rel)).FirstOrDefault(File.Exists);
	}

	public static void Prepare(RunContext a_ctx)
	{
		QuietGame.Apply(a_ctx);
		var kept = a_ctx.Journal.Kept.Count;
		FasterArchives.Apply(a_ctx);
		Crdw.Apply(a_ctx);
		// the save to play runs after the run's record says finished: a crash there must still leave something to put back
		if (a_ctx.Journal.Kept.Count > kept && a_ctx.Journal.InGameAfter) {
			a_ctx.Journal.NeedsRestore = true;
			a_ctx.Journal.Save();
		}
	}

	// one setting of a mod's own file changed for this step's game starts; the file goes back when the step ends (StepKit.KeepFile)
	public static bool Change(RunContext a_ctx, string a_file, string a_section, string a_key, string a_value, string a_why)
	{
		StepKit.KeepFile(a_ctx, a_file);
		try {
			var text = Files.DecodeText(Files.ReadShared(a_file), out var how);
			Files.WriteAtomic(a_file, Files.EncodeText(QuietGame.Set(text, a_section, a_key, a_value), how));
			a_ctx.Log.Detail(a_why);
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// the write may have landed before it failed: the kept copy goes back now, and stays on record until it has
			StepKit.PutFilesBack(a_ctx, a_file);
			a_ctx.Log.Warn($"{Path.GetFileName(a_file)} could not be changed for this game start ({e.Message})");
			return false;
		}
	}
}

// Faster Archives (Nexus 197523) repacks the game's archives. When it finds some it can repack, it closes the game by itself
// right at its start and shows its choice in a program of its own (its log: "handed off to ...FasterArchivesTool.exe ...;
// closing the game until it is done"); under Mod Organizer the player starts the game again afterwards. A run once took that
// close for a crash. So: it is never a crash; the choice is the player's, made in that window, and the run waits for it and
// then starts the game again - or, when the setup says "skip", the offer is switched off for the run's own game starts
// (the mod's own bOfferRepack=0, put back after each step), and it asks again when the player next starts the game.
public static partial class FasterArchives
{
	public const string ToolImage = "FasterArchivesTool.exe";
	public const string IniRel = @"SKSE\Plugins\FasterArchives.ini";
	public const string SettingName = "fasterArchives";

	public const string Asks = "Faster Archives asks what to do with the archives it can repack - choose in its own window (Cancel there puts it off); the run goes on by itself when that window has closed";
	const string What = "Faster Archives closed the game to ask what to do with the archives it can repack (this is no crash)";
	const string HowToPass = "set this setup to pass its offer over during a run (Settings > Faster Archives' offer)";

	public static bool IsTool(string a_image) => Path.GetFileNameWithoutExtension(a_image).Equals("FasterArchivesTool", StringComparison.OrdinalIgnoreCase);

	public static bool Skip(Setup a_setup) => (Setup.Str(a_setup.Root, SettingName) ?? "ask").Trim().Equals("skip", StringComparison.OrdinalIgnoreCase);

	// the mod's winning settings file in this profile; null = the mod is not ticked there
	public static string? Ini(Profile a_profile) => GameStart.Winning(a_profile, @"SKSE\Plugins\FasterArchives.dll") == null ? null : GameStart.Winning(a_profile, IniRel);

	public static bool HandedOff(string a_log) =>
		a_log.Contains("handed off to", StringComparison.OrdinalIgnoreCase) && a_log.Contains("closing the game", StringComparison.OrdinalIgnoreCase);

	[GeneratedRegex(@"^\s*bOfferRepack\s*=\s*0\s*(;.*)?$", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex OfferOff();

	[GeneratedRegex(@"\]\s+0 archives to repack\b", RegexOptions.CultureInvariant)]
	private static partial Regex NothingLeft();

	public static bool OfferOn(string a_ini) => !OfferOff().IsMatch(a_ini.Replace("\r", "", StringComparison.Ordinal));

	// its program, started since a_sinceUtc, each with its start time (a number Windows gives to another process later is not it)
	static Dictionary<int, DateTime> ToolSince(DateTime a_sinceUtc)
	{
		var found = new Dictionary<int, DateTime>();
		foreach (var p in Procs.Pids(ToolImage)) {
			if (Procs.StartTime(p) is { } t && t >= a_sinceUtc.AddSeconds(-2)) {
				found[p] = t;
			}
		}
		return found;
	}

	// Did Faster Archives close the game of this start for its offer? Its own program runs since that start, or its log of that
	// start says it handed off. a_since: when the game was started (the PC's own time)
	public static bool Offered(IReadOnlyList<string> a_logFolders, DateTime a_since)
	{
		if (ToolSince(a_since.ToUniversalTime()).Count > 0) {
			return true;
		}
		foreach (var dir in a_logFolders) {
			var log = Path.Combine(dir, "FasterArchives.log");
			try {
				if (File.Exists(log) && File.GetLastWriteTime(log) >= a_since.AddSeconds(-5) && HandedOff(Files.ReadSharedTailText(log, 64 * 1024))) {
					return true;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
		return false;
	}

	// The game closed for the offer (the a_time-th time in this step). While its window is open the run waits and says so; the
	// time it spends repacking never counts against "nobody chose". Returns when the window has closed: the step then starts
	// the game again.
	public static void Wait(RunContext a_ctx, string a_step, StepSettings a_s, int a_time, DateTime a_since)
	{
		var most = a_s.Int("max_offers", 3);
		var tools = ToolSince(a_since.ToUniversalTime());
		if (a_time > most) {
			// its window of this offer opened seconds ago and only waits for a choice: closed, as its Cancel would, so that Mod
			// Organizer can close for the put-back (it cannot under a program the game started)
			foreach (var p in tools.Keys) {
				Procs.KillTree(p);
			}
			throw new StepFailedException($"{What} - {a_time} times in this step. Choose \"Repack now\" or \"Don't offer these again\" in its window when it next asks, or {HowToPass}; then run again from this step");
		}
		a_ctx.Log.Warn(What + ". Choose in its window; the run starts the game again when that window has closed.");
		a_ctx.Log.Simple("Note: " + What + " - it waits for your choice in its own window");
		if (tools.Count == 0) {
			a_ctx.Log.Detail("its window has closed already - starting the game again");
			return;
		}
		var limit = TimeSpan.FromMinutes(a_s.Num("offer_wait_min", 30));
		// the band that says the run waits for the player, with no Done to press: the run goes on by itself
		a_ctx.Activity(a_step, "Faster Archives asks for your choice in its own window");
		a_ctx.Control.WaitingForPlayer?.Invoke(Asks);
		var quiet = Stopwatch.StartNew();
		var cpu = tools.Keys.ToDictionary(p => p, p => Procs.CpuOrNull(p) ?? TimeSpan.Zero);
		List<int> Live() => tools.Where(t => Procs.StartTime(t.Key) == t.Value).Select(t => t.Key).ToList();
		try {
			while (Live() is { Count: > 0 } live) {
				foreach (var p in live) {
					var now = Procs.CpuOrNull(p) ?? cpu[p];
					// it works (repacking): nobody is being waited for
					if ((now - cpu[p]).TotalSeconds > 0.5) {
						quiet.Restart();
					}
					cpu[p] = now;
				}
				if (quiet.Elapsed > limit) {
					live.ForEach(Procs.KillTree);
					throw new StepFailedException($"{What}, and nobody chose for {limit.TotalMinutes:0} minutes - its window was closed unanswered (it asks again at the next game start). Choose there, or {HowToPass}; then run again from this step");
				}
				a_ctx.Pause(TimeSpan.FromSeconds(5));
			}
		} catch (OperationCanceledException) {
			// a Stop: a window that only waits for a choice is closed with it (Mod Organizer cannot close under a program of the
			// game's); one that is repacking is never cut off - half-written archives are worse than a wait
			if (quiet.Elapsed > TimeSpan.FromSeconds(10)) {
				Live().ForEach(Procs.KillTree);
			} else {
				a_ctx.Log.Warn("Faster Archives is repacking - it is left to finish; close Mod Organizer when it is done");
			}
			throw;
		} finally {
			a_ctx.Control.WaitingForPlayer?.Invoke("");
		}
		a_ctx.Activity(a_step, "starting the game again");
		a_ctx.Log.Detail("Faster Archives' window has closed - starting the game again");
		const string repacked = "Faster Archives - Repacked";
		if (Directory.Exists(a_ctx.Mo2.ModDir(repacked)) && !a_ctx.Profile.IsOn(repacked)) {
			a_ctx.Note($"Faster Archives put its repacked archives in the mod \"{repacked}\" - it is not ticked yet: tick it in Mod Organizer after the run (its page says where it goes)");
		}
	}

	// "skip": no offer at this step's game starts
	public static void Apply(RunContext a_ctx)
	{
		if (!Skip(a_ctx.Setup) || Ini(a_ctx.Profile) is not { } ini) {
			return;
		}
		try {
			if (!OfferOn(Files.ReadSharedText(ini))) {
				return;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return;
		}
		GameStart.Change(a_ctx, ini, "Repack", "bOfferRepack", "0", "Faster Archives' offer is switched off for this step's game starts (this setup passes it over during a run) - it goes back after the step");
	}

	// the pre-check's note for a profile that ticks it
	public static Issue? Check(Setup a_setup, Profile a_profile)
	{
		if (Ini(a_profile) is not { } ini) {
			return null;
		}
		try {
			if (!OfferOn(Files.ReadSharedText(ini))) {
				return null;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
		// its own log of the last game start says whether anything is left to offer: with nothing left the note would only be noise
		foreach (var dir in InGame.LogFolders(a_setup, a_profile.Mo2)) {
			try {
				var log = Path.Combine(dir, "FasterArchives.log");
				if (File.Exists(log) && NothingLeft().IsMatch(Files.ReadSharedTailText(log, 64 * 1024))) {
					return null;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
		return Skip(a_setup) ? null : new Issue("", false, "Faster Archives is ticked: when it finds archives to repack it closes the game at its start and waits for your choice in its own window - " +
			"the run then waits for you. For a run nobody watches, let it repack once before the run, or " + HowToPass);
	}
}

// Cached Recursive Directory Walk (CRDW, Nexus 186434) keeps a list of the game's loose files so the game need not walk its
// folders at each start. At a start it asks "Do you want to generate CRDW cache files?" (Yes = walk and make the list again,
// No = load the old list); with its question switched off it does what its Mode says. A run changes the game's files, and an
// old list hides them from the game (its page: tools that write into Data mean the cache must be made again). So its question
// is answered Yes at every game start of a run (KnownBoxes). With its question off and Mode LOAD the run only says so - or,
// when the setup says "refresh", sets its Mode to CACHE for the run's own game starts and puts the file back after each step.
public static partial class Crdw
{
	public const string IniRel = @"SKSE\Plugins\CRDW.ini";
	public const string SettingName = "crdwCache";
	const string Name = "Cached Recursive Directory Walk";

	public const string Answered = Name + " asked whether to make its cache again - answered Yes: a run changes the game's files, and its old cache would hide the new ones from the game";

	public sealed record State(string? Ini, bool PopUp, string Mode);

	[GeneratedRegex(@"^\s*ShowPopUp\s*=\s*b\s*:\s*(true|false)\b", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex PopUpLine();

	[GeneratedRegex(@"^\s*Mode\s*=\s*s\s*:\s*(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
	private static partial Regex ModeLine();

	// its own defaults when a line is missing: the question shown, and LOAD behind it
	public static (bool PopUp, string Mode) Parse(string a_ini)
	{
		var pop = PopUpLine().Match(a_ini);
		var mode = ModeLine().Match(a_ini);
		return (!pop.Success || pop.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase), mode.Success ? mode.Groups[1].Value.ToUpperInvariant() : "LOAD");
	}

	// null = the mod is not ticked in this profile
	public static State? Read(Profile a_profile)
	{
		if (GameStart.Winning(a_profile, @"SKSE\Plugins\CRDW.dll") == null) {
			return null;
		}
		var ini = GameStart.Winning(a_profile, IniRel);
		try {
			var (pop, mode) = ini == null ? (true, "LOAD") : Parse(Files.ReadSharedText(ini));
			return new State(ini, pop, mode);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return new State(ini, true, "LOAD");
		}
	}

	public static bool Refresh(Setup a_setup) => (Setup.Str(a_setup.Root, SettingName) ?? "leave").Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase);

	// it loads its old list without asking
	public static bool LoadsBlind(State a_s) => !a_s.PopUp && a_s.Mode == "LOAD";

	public static Issue? Check(Setup a_setup, Profile a_profile)
	{
		if (Read(a_profile) is not { } s || !LoadsBlind(s)) {
			return null;
		}
		return new Issue("", false, Name + " loads its cache without asking (its question is switched off and its Mode is LOAD): the game then sees the files as they were when that cache was made, not what this run builds. " +
			(Refresh(a_setup) ? "This setup has it make a new cache at each game start of the run and puts its setting back after." :
				"Make its cache again after the run (its Mode set to CACHE for one game start), or let the run do it (Settings > " + Name + ")."));
	}

	// "refresh": a new list at this step's game starts
	public static void Apply(RunContext a_ctx)
	{
		if (!Refresh(a_ctx.Setup) || Read(a_ctx.Profile) is not { Ini: { } ini } s || !LoadsBlind(s)) {
			return;
		}
		GameStart.Change(a_ctx, ini, "General", "Mode", "s:CACHE", Name + " makes a new cache at this step's game starts (this setup asks for it) - its setting goes back after the step");
	}

	// What the player still has to do for it once the run is over, or null. a_lastWasGame: the run's last step that finished
	// started the game with everything the run built switched on (the save to play).
	public static string? AfterRun(Setup a_setup, State? a_state, bool a_built, bool a_lastWasGame)
	{
		if (a_state is not { } s || !a_built) {
			return null;
		}
		if (LoadsBlind(s)) {
			return a_lastWasGame && Refresh(a_setup) ? null :
				Name + ": its cache is older than what this run built, and it loads it without asking - set its Mode to CACHE for one game start before you play";
		}
		return s.PopUp && !a_lastWasGame ? Name + ": answer Yes to its question at your next game start - this run changed the game's files after the game last ran" : null;
	}
}
