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

	// (a file changed for the save to play, which runs after the run's record says finished, is on record as "to put back"
	// before it is changed: StepKit.KeepFile)
	public static void Prepare(RunContext a_ctx)
	{
		QuietGame.Apply(a_ctx);
		FasterArchives.Apply(a_ctx);
		Crdw.Apply(a_ctx);
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
			if (StepKit.PutFilesBack(a_ctx, a_file).Count > 0) {
				throw new StepFailedException($"{Path.GetFileName(a_file)} could not be changed for this game start, nor put back as it was ({e.Message}) - close what holds it, then run again from this step");
			}
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
	public const string LeftAtStop = "Faster Archives' window is left open (a repack is never cut off) - choose Cancel in it, or let it finish; Mod Organizer closes once that window has";
	const string What = "Faster Archives closed the game to ask what to do with the archives it can repack (this is no crash)";
	const string HowToPass = "set this setup to pass its offer over during a run (Settings > Game steps > Faster Archives' repack offer)";

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

	// Its program, started since a_sinceUtc, each with its start time (a number Windows gives to another process later is not
	// it). a_owned: only what the run can prove its own - on its record as started by the game this run started. One that
	// another game of the player's started in those seconds is not that (a process is only ever ended by a PID this run
	// started, or one of its children)
	static Dictionary<int, DateTime> ToolSince(StartedSet a_started, DateTime a_sinceUtc, bool a_owned)
	{
		var found = new Dictionary<int, DateTime>();
		var running = Procs.Pids(ToolImage);
		if (running.Count == 0) {
			return found;
		}
		if (a_owned) {
			// with the start time on the run's own record, never one read again by number: the two stay one identity up to the
			// moment the program is ended (End)
			a_started.Sweep();
			var owned = a_started.OwnedAt();
			// One that started in the very tick of that look's clock is not taken by it (StartedSet) but by a look a tick
			// later: asked once more, so a program started an instant ago is not missed
			if (running.Any(p => !owned.ContainsKey(p) && Procs.StartTime(p) is { } began && began >= Procs.StartClock().AddMilliseconds(-50))) {
				Thread.Sleep(20);
				a_started.Sweep();
				owned = a_started.OwnedAt();
			}
			foreach (var p in running) {
				if (owned.TryGetValue(p, out var at) && at >= a_sinceUtc.AddSeconds(-2)) {
					found[p] = at;
				}
			}
			return found;
		}
		foreach (var p in running) {
			if (Procs.StartTime(p) is { } t && t >= a_sinceUtc.AddSeconds(-2)) {
				found[p] = t;
			}
		}
		return found;
	}

	// how long its window is looked at, at the offer limit, before it is taken for one that only waits
	public static TimeSpan OfferLook { get; set; } = TimeSpan.FromSeconds(3);

	// Did Faster Archives close the game of this start for its offer? Its own program runs since that start (a_started: what
	// this run started), or its log of that start says it handed off. a_since: when the game was started (the PC's own time)
	public static bool Offered(IReadOnlyList<string> a_logFolders, DateTime a_since, StartedSet a_started)
	{
		if (ToolSince(a_started, a_since.ToUniversalTime(), true).Count > 0) {
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
		// What is ENDED here (a window that only sits there, when nobody chose within the whole wait) is only a program the
		// run can prove its own. What is WAITED for may be less sure: when the game closed for the offer and no such program
		// is on the run's record (its program was not started by the game directly), its program of that name started since
		// this game start is waited for all the same - and never ended
		var tools = ToolSince(a_ctx.Control.Started, a_since.ToUniversalTime(), true);

		if (tools.Count == 0) {
			tools = ToolSince(a_ctx.Control.Started, a_since.ToUniversalTime(), false);
		}
		// returns whether every one of them was the run's to end. Each is ended as the process on record - its number AND its
		// start time, through one handle - never by its number alone. Whose they are is asked NOW, not when the wait began:
		// a program that had started only an instant before the wait is put on the run's record a look later (StartedSet
		// takes no process younger than its reading of the clock), and one never on record is never ended.
		bool End(List<int> a_pids)
		{
			var mine = ToolSince(a_ctx.Control.Started, a_since.ToUniversalTime(), true);
			foreach (var p in a_pids) {
				if (mine.TryGetValue(p, out var at)) {
					Procs.KillTree(p, at);
				}
			}
			return a_pids.All(mine.ContainsKey);
		}
		const string stillOpen = "its window is still open (the run could not tell that its own game opened it, so it is left alone): choose Cancel there, so that Mod Organizer can close";
		var cpu = tools.Keys.ToDictionary(p => p, p => Procs.CpuOrNull(p) ?? TimeSpan.Zero);
		List<int> Live() => tools.Where(t => Procs.StartTime(t.Key) == t.Value).Select(t => t.Key).ToList();
		var sampled = Stopwatch.StartNew();
		// Did its program work since the last look? Repacking takes a core or more; a window that waits for a choice next to
		// nothing - a tenth of one core over the stretch between two looks tells them apart
		bool Worked(List<int> a_live)
		{
			var seconds = Math.Max(0.2, sampled.Elapsed.TotalSeconds);
			var worked = false;
			foreach (var p in a_live) {
				var before = cpu.GetValueOrDefault(p);
				var now = Procs.CpuOrNull(p) ?? before;
				worked |= (now - before).TotalSeconds / seconds > 0.1;
				cpu[p] = now;
			}
			sampled.Restart();
			return worked;
		}
		// Does its window only sit there? Not having worked since the last look, AND not in one more short slice right now: a
		// choice made in the last moment of a long look would otherwise read as "idle" by the look's average. A window is
		// only ever closed on this answer.
		bool SitsIdle(List<int> a_live)
		{
			if (Worked(a_live)) {
				return false;
			}
			Thread.Sleep(300);
			return !Worked(a_live);
		}
		// A Stop never closes its window. Whether it only waits, or the player chose "Repack now" a moment ago, cannot be told
		// for certain from outside: a look at its processor use misses a repack that begins as the look ends, or between the
		// look and the close - and a repack cut off leaves archives half written, which is worse than any wait. So it stays,
		// said in words; Mod Organizer's close waits for it (Mo2Control.Repackers). Who pressed Stop is at the PC.
		void AtStop()
		{
			if (Live().Count > 0) {
				a_ctx.Log.Warn("Stop: " + LeftAtStop);
				a_ctx.Log.Simple("Note: " + LeftAtStop);
			}
		}
		var repacking = false;
		if (a_time > most) {
			// Past the limit the game is not started again. Its window of this offer, opened seconds ago, is LEFT OPEN: a
			// player who has answered it this often is at the PC, and whether they are choosing "Repack now" this very moment
			// cannot be told for certain - a repack is never cut off. One that works by now is waited out like any other; one
			// that sits there stays, the step stops, and Mod Organizer's close waits for it (Mo2Control.Repackers)
			if (tools.Count > 0) {
				try {
					a_ctx.Pause(OfferLook);
				} catch (OperationCanceledException) {
					AtStop();
					throw;
				}
			}
			var open = Live();
			if (!Worked(open)) {
				throw new StepFailedException($"{What} - {a_time} times in this step. " +
					(open.Count > 0 ? "Its window is left open: choose \"Repack now\" or \"Don't offer these again\" there (Mod Organizer closes once that window has)"
						: "Choose \"Repack now\" or \"Don't offer these again\" in its window when it next asks") + $", or {HowToPass}; then run again from this step");
			}
			repacking = true;
			a_ctx.Log.Warn($"{What} - {a_time} times in this step, and this time it is repacking: the run waits for it to finish, then starts the game again");
		} else {
			a_ctx.Log.Warn(What + ". Choose in its window; the run starts the game again when that window has closed.");
		}
		a_ctx.Log.Simple(repacking ? "Note: Faster Archives is repacking the game's archives - the run goes on when it has finished" : "Note: " + What + " - it waits for your choice in its own window");
		if (tools.Count == 0) {
			a_ctx.Log.Detail("its window has closed already - starting the game again");
			return;
		}
		var limit = TimeSpan.FromMinutes(a_s.Num("offer_wait_min", 30));
		// the band that says the run waits for the player, with no Done to press: the run goes on by itself
		a_ctx.Activity(a_step, "Faster Archives asks for your choice in its own window");
		a_ctx.Control.WaitingForPlayer?.Invoke(Asks);
		var quiet = Stopwatch.StartNew();
		try {
			while (Live() is { Count: > 0 } live) {
				// it works (repacking): nobody is being waited for
				if (Worked(live)) {
					quiet.Restart();
				}
				// nobody chose: its window is closed, as its Cancel would, so that Mod Organizer can close and the profile be put
				// back in a run nobody watches - but only one that sits idle to the last moment
				if (quiet.Elapsed > limit && !SitsIdle(live)) {
					quiet.Restart();
				}
				if (quiet.Elapsed > limit) {
					var closed = End(live);
					throw new StepFailedException($"{What}, and nobody chose for {limit.TotalMinutes:0} minutes - " +
						(closed ? "its window was closed unanswered (it asks again at the next game start)" : stillOpen) + $". Choose there, or {HowToPass}; then run again from this step");
				}
				a_ctx.Pause(TimeSpan.FromSeconds(5));
			}
		} catch (OperationCanceledException) {
			AtStop();
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
// is answered Yes at every game start of a run (KnownBoxes). With its question off and Mode LOAD it loads its old list
// blind; then the run has it make a new list (its Mode set to CACHE) at the run's own game starts that come after the run
// changed the game's files, and puts the mod's own setting back after each step (his answer of 2026-11-15: "mod makes a new
// list when files are changed during the run"). The setup can ask for a new list at every game start instead, or for the
// mod to be left alone and the player only told.
public static partial class Crdw
{
	public const string IniRel = @"SKSE\Plugins\CRDW.ini";
	public const string SettingName = "directoryCache";
	// The key the first builds of 1.0 wrote. Every save of the settings wrote its "leave" whether the player chose it or not,
	// so that value says nothing; only its "refresh", which had to be picked, still speaks
	const string OldSettingName = "crdwCache";
	const string Name = "Cached Recursive Directory Walk";

	// when the run has it make a new list, for a mod that loads its old one blind
	public enum Renew
	{
		// at the run's own game starts after the run changed the game's files
		Changed,
		// at every game start of the run
		Always,
		// never: the player is only told
		Never,
	}

	public static Renew When(Setup a_setup)
	{
		var set = (Setup.Str(a_setup.Root, SettingName) ?? "").Trim();
		if (set.Equals("always", StringComparison.OrdinalIgnoreCase)) {
			return Renew.Always;
		}
		if (set.Equals("never", StringComparison.OrdinalIgnoreCase)) {
			return Renew.Never;
		}
		return set == "" && (Setup.Str(a_setup.Root, OldSettingName) ?? "").Trim().Equals("refresh", StringComparison.OrdinalIgnoreCase) ? Renew.Always : Renew.Changed;
	}

	// The same as the word the setting holds for it: what the settings window shows, whatever the setup itself says - under
	// the old key, with an empty value, or in a word it does not know. Shown any other way, the window's list fell back to
	// its first choice and the next save wrote that as if the player had picked it.
	public static string Word(Setup a_setup) => When(a_setup) switch { Renew.Always => "always", Renew.Never => "never", _ => "changed" };

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

	// it loads its old list without asking
	public static bool LoadsBlind(State a_s) => !a_s.PopUp && a_s.Mode == "LOAD";

	public static Issue? Check(Setup a_setup, Profile a_profile)
	{
		if (Read(a_profile) is not { } s || !LoadsBlind(s)) {
			return null;
		}
		return new Issue("", false, Name + " loads its cache without asking (its question is switched off and its Mode is LOAD): the game then sees the files as they were when that cache was made, not what this run builds. " +
			When(a_setup) switch {
				Renew.Always => "This setup has it make a new cache at every game start of the run and puts its setting back after each step.",
				Renew.Never => "This setup leaves it alone: make its cache again after the run (its Mode set to CACHE for one game start), or let the run do it (Settings > Game steps > " + Name + ").",
				_ => "The run has it make a new cache at its own game starts after it changed the game's files, and puts its setting back after each step.",
			});
	}

	// Before a game start of the step in hand: a new list at this step's game starts, when the setup asks for one at every
	// start, or (unless it says never) when the run changed the game's files since the game last made its list in this run.
	// The mod's own file goes back when the step ends (GameStart.Change)
	public static void Apply(RunContext a_ctx)
	{
		var when = When(a_ctx.Setup);
		if (when == Renew.Never || (when == Renew.Changed && a_ctx.Journal.FilesChanged == 0) || Read(a_ctx.Profile) is not { Ini: { } ini } s || !LoadsBlind(s)) {
			return;
		}
		if (GameStart.Change(a_ctx, ini, "General", "Mode", "s:CACHE", Name + (when == Renew.Always ? " makes a new cache at this step's game starts (this setup asks for one at every start)" :
			    " makes a new cache at this step's game starts (the run changed the game's files since its cache was made)") + " - its setting goes back after the step")) {
			a_ctx.FreshList = true;
		}
	}

	// A step ended. a_done: it finished. a_writes: it is a step that changes the files the game lists (every step but the
	// ones that only start the game and save). A step that finished after game starts with a new list leaves the list
	// current - as of those starts: what the step itself wrote afterwards counts as changed again, and so do the mods it
	// had switched for those starts and has put back since (the list was made with them switched).
	public static void StepEnded(RunContext a_ctx, bool a_done, bool a_writes)
	{
		if (a_ctx.FreshList && a_done) {
			a_ctx.Journal.FilesChanged = 0;
		}
		a_ctx.FreshList = false;
		if (a_writes || a_ctx.SwitchedBack) {
			++a_ctx.Journal.FilesChanged;
		}
		a_ctx.SwitchedBack = false;
	}

	// all but the steps that only start the game and save, compile shaders (a shader cache is no file the mod lists), or
	// sort the load order
	public static bool Writes(string a_step) => a_step is not ("cleansave" or "playsave" or "shaders" or Steps.LootStep.StepKey);

	// What the player still has to do for it once the run is over, or null. a_changed: the run built something or switched
	// mods off and on again. a_current: the game made its list anew in this run after the last thing that may have changed
	// the game's files (the put-back's switching counts, and so does each command of the setup after the run).
	// a_lastWasGame: the run's last step that finished started the game with everything the run built switched on (the save
	// to play). a_commandOnly: the list was current until the setup's own commands after the run ran - whether those
	// changed a game file nobody can tell from here, so the player is told what to do if they do.
	public static string? AfterRun(State? a_state, bool a_changed, bool a_current, bool a_lastWasGame, bool a_commandOnly = false)
	{
		if (a_state is not { } s || !(a_changed || a_commandOnly)) {
			return null;
		}
		if (LoadsBlind(s)) {
			if (a_current) {
				return null;
			}
			return a_commandOnly
				? Name + ": this setup's command after the run ran after the game last made its cache, and the mod loads that cache without asking - if that command adds, moves or removes game files, set its Mode to CACHE for one game start before you play"
				: Name + ": its cache is older than what this run changed, and it loads it without asking - set its Mode to CACHE for one game start before you play";
		}
		// with its question on, the mod itself asks at the next game start: after a command of the player's own, the answer is theirs
		return a_changed && s.PopUp && !a_lastWasGame ? Name + ": answer Yes to its question at your next game start - this run changed the game's files after the game last ran" : null;
	}
}
