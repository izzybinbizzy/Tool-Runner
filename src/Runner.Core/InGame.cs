using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// The game driven for the clean save, the save to play and the shader caches. With DevBench the runner does it all; without
// it the player is asked for each in-game action and the runner checks the result on disk (the save file, the game gone).
public static partial class InGame
{
	public const string GameImage = "SkyrimSE.exe";
	public const string SaveOff = Brand.Name + " - DynDOLOD off";
	public const string SaveClean = Brand.Name + " - clean save";
	public const string SaveReady = Brand.Name + " - ready to play";

	public static readonly string[] ShaderLogNames = ["CommunityShaders.log", "BottledShaders.log", "OpenShaders.log"];

	// is any Skyrim running (the player's or ours)? - only for "close it first"
	public static bool AnyGame() => Procs.Running(GameImage);

	// the game THIS run started (its PID is in Control.Started), never the player's own
	public static Process? Game(RunContext a_ctx)
	{
		Process? mine = null;
		foreach (var p in Procs.ByImage(GameImage)) {
			if (mine == null && a_ctx.Control.Started.SameProcess(p.Id)) {
				mine = p;
			} else {
				p.Dispose();
			}
		}
		return mine;
	}

	// no Skyrim may run before the run starts one (a game it just closed gets 30 s to go); the player's own is never closed
	public static void NoGameRunning(RunContext a_ctx)
	{
		var sw = Stopwatch.StartNew();
		// (a process list Windows would not give is no answer: "no Skyrim runs" was not learned, and the look is made again)
		bool unknown;
		while (AnyGame() | (unknown = Procs.LookFailed)) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > TimeSpan.FromSeconds(30)) {
				throw new StepFailedException(unknown ? "Windows would not list the running programs, so it is not known whether Skyrim is running - start the run again in a moment" :
					"Skyrim is already running - close it first");
			}
			a_ctx.Pause(TimeSpan.FromSeconds(1));
		}
	}

	// the PID of a Skyrim that did not run when this was asked (the one a launch is about to start)
	public static Func<int> FreshGamePid()
	{
		var old = Procs.Pids(GameImage).ToHashSet();
		return () => {
			var ps = Procs.ByImage(GameImage);
			var id = ps.Select(p => p.Id).FirstOrDefault(i => !old.Contains(i));
			ps.ForEach(p => p.Dispose());
			return id;
		};
	}

	// the SKSE log folder: a setup may name it (a log redirector), else My Games\<game>\SKSE
	public static string SkseLogs(Setup a_setup, Mo2Instance a_mo2)
	{
		var set = Setup.Str(a_setup.Root, "skseLogs");
		if (!string.IsNullOrWhiteSpace(set)) {
			return set;
		}
		var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var gog = Path.Combine(docs, "My Games", "Skyrim Special Edition GOG", "SKSE");
		// the GOG copy is known by what it ships (QuietGame.IsGog), never by the word "GOG" in a folder's name: a GOG game in
		// "D:\Games\Skyrim" read as Steam's, a Steam game under "D:\GOG and Steam" as GOG's
		return QuietGame.IsGog(a_mo2.GameDir) && Directory.Exists(gog) ? gog :
			Path.Combine(docs, "My Games", "Skyrim Special Edition", "SKSE");
	}

	// Where the SKSE plugins' logs really are. The game is told My Games\<game>\SKSE, but a Mod Organizer file-mapper plugin (an
	// "SKSE log redirector") can show it another folder there: the files are then in that other folder and the one in Documents
	// stays empty (his run of 2026-11-10: the shader log and the crash logs were never found). So, when the setup names no
	// folder: the usual one, then every existing folder such a plugin names in its own file.
	public static List<string> LogFolders(Setup a_setup, Mo2Instance a_mo2)
	{
		var first = SkseLogs(a_setup, a_mo2);
		var folders = new List<string> { first };
		if (!string.IsNullOrWhiteSpace(Setup.Str(a_setup.Root, "skseLogs"))) {
			return folders;
		}
		foreach (var d in MappedLogFolders(Path.Combine(Path.GetDirectoryName(a_mo2.Exe) ?? "", "plugins"))) {
			if (!folders.Any(f => Files.SamePath(f, d))) {
				folders.Add(d);
			}
		}
		return folders;
	}

	// a quoted absolute Windows path in a script (the quote it opened with closes it: a folder name may hold the other one)
	[GeneratedRegex("""[rR]?(["'])([A-Za-z]:[\\/](?:(?!\1)[^\r\n]){1,400})\1""")]
	private static partial Regex PathLiteral();

	// the plugin files as last read (their count, sizes and times), so a look that repeats every few seconds reads none again
	static readonly Lock s_mappedGate = new();
	static (string Dir, string Stamp, List<string> Folders)? s_mapped;

	// The folders a Mod Organizer plugin that maps SKSE's log folder names as text in its file (read, never run). Only a folder
	// that holds a log now counts, and never a drive's root: a plugin's file names other folders too, and every folder taken
	// here is searched for crash logs.
	public static List<string> MappedLogFolders(string a_pluginsDir)
	{
		var found = new List<string>();
		try {
			if (!Directory.Exists(a_pluginsDir)) {
				return found;
			}
			var files = Directory.EnumerateFiles(a_pluginsDir, "*.py").Concat(Directory.EnumerateDirectories(a_pluginsDir).SelectMany(d => Directory.EnumerateFiles(d, "*.py")))
				.Select(f => new FileInfo(f)).Where(f => f.Length < 512 * 1024).Take(400).ToList();
			var stamp = string.Join("|", files.Select(f => $"{f.FullName}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
			lock (s_mappedGate) {
				if (s_mapped is { } last && last.Dir == a_pluginsDir && last.Stamp == stamp) {
					return last.Folders.Where(HoldsALog).ToList();
				}
			}
			foreach (var f in files) {
				var text = Files.ReadSharedText(f.FullName);
				if (!text.Contains("IPluginFileMapper", StringComparison.Ordinal) || !text.Contains("SKSE", StringComparison.Ordinal)) {
					continue;
				}
				foreach (Match m in PathLiteral().Matches(text)) {
					var dir = m.Groups[2].Value.Replace(@"\\", @"\", StringComparison.Ordinal).Replace('/', '\\').TrimEnd('\\');
					// kept whether or not it stands yet: the game may make the folder later, and the plugin's file (what this
					// list is remembered by) does not change when it does - HoldsALog judges each time it is asked
					if (dir.Length > 3 && !found.Contains(dir, StringComparer.OrdinalIgnoreCase)) {
						found.Add(dir);
					}
				}
			}
			lock (s_mappedGate) {
				s_mapped = (a_pluginsDir, stamp, found);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
		return found.Where(HoldsALog).ToList();
	}

	static bool HoldsALog(string a_dir)
	{
		try {
			return Directory.EnumerateFiles(a_dir, "*.log").Any();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	// his checklist 470: the game closed under a step - the crash report written since that start is named (Crash Logger writes
	// SKSE\crash-<time>.log, Trainwreck SKSE\Crashlogs\crash-<time>.log, .NET Script Framework NetScriptFramework\Crash\Crash_<time>.txt
	// and Tullius CTD Logger SKSE\Plugins\...\SkyrimDiag_Crash_<time>_SkyrimDiagReport.txt, the last two in Mod Organizer's
	// overwrite), so the failure points at the cause instead of only saying the game went
	public static StepFailedException GameClosed(RunContext a_ctx, string a_while) =>
		// a game the run's watch ended under a box nobody could answer is named by that box, never as a close of its own
		new(a_ctx.BlockedBy ?? $"the game closed {a_while}" + CrashNote(LogFolders(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, a_ctx.GameLaunched ?? a_ctx.Journal.Started) + LastBox(a_ctx));

	// what the game itself said in a box just before it went (a plugin that failed to load, a version it refuses)
	public static string LastBox(RunContext a_ctx) =>
		a_ctx.Boxes?.GameSaidWithin(TimeSpan.FromMinutes(2)) is { } said ? " - the last box it showed: " + said : "";

	public static string CrashNote(string a_skseLogs, string a_overwrite, DateTime a_since) => CrashNote([a_skseLogs], a_overwrite, a_since);

	public static string CrashNote(IReadOnlyList<string> a_logFolders, string a_overwrite, DateTime a_since) =>
		CrashReports(a_logFolders, a_overwrite, a_since) is { Count: > 0 } logs ? $" - it crashed; its crash report: {string.Join(" and ", logs)}" :
			" - no crash report was written (a crash logger such as Crash Logger SSE names the cause of the next one)";

	public static string? CrashLog(string a_skseLogs, string a_overwrite, DateTime a_since) => CrashReports([a_skseLogs], a_overwrite, a_since).FirstOrDefault();

	// A crash logger's own report file, by its name (a dump or a helper's log beside it is not the report a player reads). A
	// report carries its time in its name ("crash-2026-11-07-23-07-14.log"); the logger's own plugin log ("CrashLogger.log",
	// written at every start) is none - a run once named it as the crash report of a game that had only closed.
	public static bool IsCrashReport(string a_fileName) =>
		Regex.IsMatch(a_fileName, @"^(crash[-_ ]?\d.*\.log|Crash_.*\.txt|SkyrimDiag_Crash_.*_SkyrimDiagReport\.txt)$", RegexOptions.IgnoreCase);

	// every crash report written since a_since, newest first - one per crash logger that wrote one
	public static List<string> CrashReports(IReadOnlyList<string> a_logFolders, string a_overwrite, DateTime a_since)
	{
		var found = new List<FileInfo>();
		void Look(string a_dir, string a_pattern, SearchOption a_how)
		{
			try {
				if (Directory.Exists(a_dir)) {
					found.AddRange(new DirectoryInfo(a_dir).EnumerateFiles(a_pattern, a_how).Where(f => f.LastWriteTime >= a_since && IsCrashReport(f.Name)));
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
		foreach (var logs in a_logFolders) {
			Look(logs, "crash*.log", SearchOption.AllDirectories);
			Look(logs, "SkyrimDiag_Crash_*_SkyrimDiagReport.txt", SearchOption.AllDirectories);
		}
		Look(Path.Combine(a_overwrite, "NetScriptFramework", "Crash"), "Crash*.txt", SearchOption.TopDirectoryOnly);
		Look(Path.Combine(a_overwrite, "SKSE", "Plugins"), "SkyrimDiag_Crash_*_SkyrimDiagReport.txt", SearchOption.AllDirectories);
		return found.OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).Distinct(StringComparer.OrdinalIgnoreCase)
			.GroupBy(f => Path.GetFileName(f).StartsWith("SkyrimDiag", StringComparison.OrdinalIgnoreCase) ? "t" : Path.GetExtension(f).ToLowerInvariant()).Select(g => g.First()).ToList();
	}

	// the log a shader mod writes now: the newest of the known names (Bottled Shaders renamed its log 2026-10-26)
	public static string? NewestShaderLog(string a_folder, string? a_preferred = null) => NewestShaderLog([a_folder], a_preferred);

	public static string? NewestShaderLog(IReadOnlyList<string> a_folders, string? a_preferred = null)
	{
		var names = a_preferred == null ? ShaderLogNames : [a_preferred, .. ShaderLogNames.Where(n => !n.Equals(a_preferred, StringComparison.OrdinalIgnoreCase))];
		return a_folders.SelectMany(d => names.Select(n => Path.Combine(d, n))).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
	}

	[GeneratedRegex(@"\[(?:\d{4}-\d\d-\d\d[ T])?\d\d:\d\d:\d\d(?:\.\d+)?\].*\[ShaderTiming\].*\bremaining=(\d+)")]
	private static partial Regex Remaining();

	// a compile runs: a "remaining=N" line (N > 0) in the last 5 minutes, or Open Shaders' "started" with no "completed" after it,
	// or a shader landed in overwrite\ShaderCache in the last 2 minutes (the signal that survives a log's rename or re-stamp)
	public static bool Compiling(string a_logFolder, string a_overwrite, DateTime a_since) => Compiling([a_logFolder], a_overwrite, a_since);

	public static bool Compiling(IReadOnlyList<string> a_logFolders, string a_overwrite, DateTime a_since)
	{
		if (CacheBusy(a_overwrite)) {
			return true;
		}
		var log = NewestShaderLog(a_logFolders);
		if (log == null || File.GetLastWriteTimeUtc(log) < a_since.ToUniversalTime() || DateTime.UtcNow - File.GetLastWriteTimeUtc(log) > TimeSpan.FromMinutes(5)) {
			return false;
		}
		string[] tail;
		try {
			tail = Files.ReadSharedTailText(log, LogTailBytes).Split('\n').TakeLast(300).ToArray();
		} catch (IOException) {
			return false;
		}
		for (int i = tail.Length - 1; i >= 0; --i) {
			var m = Remaining().Match(tail[i]);
			if (m.Success) {
				// Bottled Shaders writes this line only for a SLOW shader, so its last count can stay above 0 for good: by the
				// line's own date it counts 5 minutes, however many other lines the log gains after it
				return int.Parse(m.Groups[1].Value, InvariantCulture) > 0 && !(LineTime(tail[i]) is { } at && DateTime.Now - at > TimeSpan.FromMinutes(5));
			}
			if (tail[i].Contains("Shader compilation completed", StringComparison.Ordinal)) {
				return false;
			}
			if (tail[i].Contains("Shader compilation started", StringComparison.Ordinal)) {
				return true;
			}
		}
		return false;
	}

	// when a log line was written, by its own stamp "[yyyy-MM-dd HH:mm:ss" (null: it carries no date)
	static DateTime? LineTime(string a_line)
	{
		var line = a_line.TrimStart();
		return line.Length >= 20 && DateTime.TryParseExact(line[1..20], "yyyy-MM-dd HH:mm:ss", InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at : null;
	}

	// the newest "remaining=N" a shader log holds (null: it has no ShaderTiming lines - Open Shaders, an older Community Shaders)
	public static int? LastRemaining(string a_log)
	{
		try {
			return RemainingIn(Files.ReadSharedText(a_log));
		} catch (IOException) {
			return null;
		}
	}

	// the same, of a log's text - the whole of it, however many lines follow the count. Bottled Shaders writes the line only for
	// a SLOW shader, so a last count above 0 says nothing about now; only 0 is a verdict (his run of 2026-11-10: its last line
	// read remaining=549 five minutes before the compile's real end, and nothing after it said done)
	public static int? RemainingIn(string a_logText)
	{
		var lines = a_logText.Split('\n');
		for (int i = lines.Length - 1; i >= 0; --i) {
			var m = Remaining().Match(lines[i]);
			if (m.Success) {
				return int.Parse(m.Groups[1].Value, InvariantCulture);
			}
		}
		return null;
	}

	// Bottled Shaders queues its disk-cache trim ("[DDC] N entries ...") only once its start-up compile pass is over, so that line
	// with no "remaining=N" (N > 0) after it means a warm start needs no full compile watch (a warm start sat 300 s at the main menu)
	// a line stamped with its date before a_since is an earlier session's, never this start's
	public static bool BootPassOver(IReadOnlyList<string> a_lines, DateTime? a_since = null)
	{
		for (int i = a_lines.Count - 1; i >= 0; --i) {
			var m = Remaining().Match(a_lines[i]);
			if (m.Success && int.Parse(m.Groups[1].Value, InvariantCulture) > 0) {
				return false;
			}
			if (a_lines[i].Contains("[DDC]", StringComparison.Ordinal)) {
				var line = a_lines[i].TrimStart();
				return a_since is not { } since || line.Length < 20 || !DateTime.TryParseExact(line[1..20], "yyyy-MM-dd HH:mm:ss", InvariantCulture,
					System.Globalization.DateTimeStyles.None, out var at) || at >= since.AddSeconds(-1);
			}
		}
		return false;
	}

	// (its own name: called "BootPassOver" too, the call below picked this method again - a DateTime fits it better than the
	// parser's DateTime? - and handed it the log's lines as folders, so the parser never ran)
	public static bool BootPassOverIn(IReadOnlyList<string> a_logFolders, DateTime a_since)
	{
		var log = NewestShaderLog(a_logFolders);
		if (log == null || File.GetLastWriteTimeUtc(log) < a_since.ToUniversalTime()) {
			return false;
		}
		try {
			return BootPassOver(Files.ReadSharedTailText(log, LogTailBytes).Split('\n').TakeLast(300).ToList(), a_since);
		} catch (IOException) {
			return false;
		}
	}

	// how much of a shader log's end holds its last 300 lines, with room to spare (a line is a few hundred letters at most)
	const int LogTailBytes = 512 * 1024;

	public static bool CacheBusy(string a_overwrite, int a_quietSeconds = 120)
	{
		var d = Path.Combine(a_overwrite, "ShaderCache");
		return Directory.Exists(d) && DateTime.UtcNow - Files.NewestWrite(d) < TimeSpan.FromSeconds(a_quietSeconds);
	}

	public static string SavesDir(Profile a_profile)
	{
		var ini = Path.Combine(a_profile.Dir, "settings.ini");
		var local = File.Exists(ini) && Files.ReadSharedText(ini).Split('\n').Any(l => l.Trim().Equals("LocalSaves=true", StringComparison.OrdinalIgnoreCase));
		if (local) {
			return Path.Combine(a_profile.Dir, "saves");
		}
		var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
		var gog = QuietGame.IsGog(a_profile.Mo2.GameDir);
		var myGames = Path.Combine(docs, "My Games", gog ? "Skyrim Special Edition GOG" : "Skyrim Special Edition");
		// the game's INIs: the profile's own when it keeps them (LocalSettings), else My Games'; SkyrimCustom.ini wins
		var localInis = File.Exists(ini) && Files.ReadSharedText(ini).Split('\n').Any(l => l.Trim().Equals("LocalSettings=true", StringComparison.OrdinalIgnoreCase));
		var iniDir = localInis ? a_profile.Dir : myGames;
		var savePath = new[] { "SkyrimCustom.ini", "Skyrim.ini" }.Select(n => Path.Combine(iniDir, n)).Where(File.Exists)
			.Select(f => Files.ReadSharedText(f).Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("sLocalSavePath=", StringComparison.OrdinalIgnoreCase)))
			.FirstOrDefault(l => l != null)?["sLocalSavePath=".Length..].Trim();
		return Path.Combine(myGames, string.IsNullOrEmpty(savePath) ? "Saves" : savePath.TrimEnd('\\'));
	}

	// the player's own newest save - never one the runner (or the old MO2 Updater) made itself (his addition 2, 2026-10-27), nor
	// one written while a run of ours went on (an autosave or quicksave the runner's game made in QASmoke - the fourth review, M3)
	// a_character: only that character's saves (the setup's "character", his checklist 457); empty = anyone's newest
	public static string? NewestPlayerSave(Profile a_profile, IReadOnlyList<(DateTime From, DateTime To)>? a_ourRuns = null, string? a_character = null)
	{
		var d = SavesDir(a_profile);
		if (!Directory.Exists(d)) {
			return null;
		}
		var f = new DirectoryInfo(d).EnumerateFiles("*.ess")
			.Where(x => !Brand.Made(x.Name) && !x.Name.StartsWith("MO2 Updater - ", StringComparison.OrdinalIgnoreCase))
			.Where(x => !(a_ourRuns ?? []).Any(r => x.LastWriteTimeUtc >= r.From && x.LastWriteTimeUtc <= r.To))
			.OrderByDescending(x => x.LastWriteTimeUtc)
			.FirstOrDefault(x => string.IsNullOrWhiteSpace(a_character) || string.Equals(SaveHeader.PlayerName(x.FullName), a_character.Trim(), StringComparison.OrdinalIgnoreCase));
		return f == null ? null : Path.GetFileNameWithoutExtension(f.Name);
	}

	// the character the setup picked for the in-game steps (null = anyone's newest save)
	public static string? Character(RunContext a_ctx) => Setup.Str(a_ctx.Setup.Root, "character") is { Length: > 0 } c && c.Trim() != "" ? c.Trim() : null;

	// the character a save belongs to, when it is another than a_who (null = the same, no character picked, or not readable)
	public static string? OtherCharacter(Profile a_profile, string a_save, string? a_who)
	{
		if (string.IsNullOrWhiteSpace(a_who)) {
			return null;
		}
		var name = SaveHeader.PlayerName(Path.Combine(SavesDir(a_profile), a_save + ".ess"));
		return name != null && !name.Equals(a_who.Trim(), StringComparison.OrdinalIgnoreCase) ? name : null;
	}

	// a save the setup names (or the player made) that belongs to another character than the one picked: never loaded, said why
	static void SameCharacter(RunContext a_ctx, string a_save, string a_what)
	{
		if (Character(a_ctx) is { } who && OtherCharacter(a_ctx.Profile, a_save, who) is { } other) {
			throw new StepFailedException($"{a_what} \"{a_save}\" is {other}'s, not {who}'s (the character this setup picked) - " +
				"name one of that character's saves, or change the character in Settings");
		}
	}

	// when an earlier run's clean save was made: only one of the picked character (null = none, or another character's)
	public static DateTime? OldCleanSave(Profile a_profile, string? a_who) =>
		OtherCharacter(a_profile, SaveClean, a_who) == null ? SaveTime(a_profile, SaveClean) : null;

	// what the guided clean save asks the player to load
	static string LoadWhat(RunContext a_ctx, StepSettings a_s) =>
		a_s.Str("save") is { Length: > 0 } named ? $"load the save \"{named}\"" :
		Character(a_ctx) is { } who ? $"load the newest save of {who}" : "load your newest save";

	// the steps that start the runner's own game (the clean save, the save to play, the shader caches, the game-run grass cache)
	static readonly HashSet<string> GameSteps = ["cleansave", "playsave", "shaders", "grass"];

	// when the runner's own game ran on this profile, as UTC time spans (a step still going: up to now) - only those, never a
	// whole run: a save the player makes while a tool step runs is still theirs (CodeRabbit on PR #4)
	public static List<(DateTime From, DateTime To)> OurRuns(RunContext a_ctx)
	{
		try {
			// only THIS run's open step runs until now; another run's open step (it died) ends when its record was last written
			return Journal.All(Path.GetDirectoryName(a_ctx.Journal.Folder)!).Where(j => j.Profile.Equals(a_ctx.Profile.Name, StringComparison.OrdinalIgnoreCase))
				.SelectMany(j => GameTimes(j.Steps, j.Id == a_ctx.Journal.Id ? DateTime.Now : j.Ended ?? File.GetLastWriteTime(j.File))).ToList();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return [];
		}
	}

	// a_openEnd: where a step with no end stops (now for the run going; its record's last write for a run that died)
	public static IEnumerable<(DateTime From, DateTime To)> GameTimes(IEnumerable<StepRecord> a_steps, DateTime a_openEnd) =>
		a_steps.Where(s => GameSteps.Contains(s.Key) && s.Started != null)
			.Select(s => (s.Started!.Value.ToUniversalTime(), (s.Ended ?? (a_openEnd < s.Started.Value ? s.Started.Value : a_openEnd)).ToUniversalTime()));

	// The place the player stood at before the clean save is on record in the run that MADE that save. A run that starts from an
	// earlier run's clean save - "run again from the failed step" is a new run with a record of its own - takes the marker from
	// that run: without it the save to play was made in the test cell, and the marker stayed in the save for good. The run is
	// known by its clean-save step ending when the save was written.
	public static string? MarkerOfCleanSave(RunContext a_ctx)
	{
		if (SaveTime(a_ctx.Profile, SaveClean) is not { } saved) {
			return null;
		}
		try {
			return Journal.All(Path.GetDirectoryName(a_ctx.Journal.Folder)!)
				.Where(j => j.Id != a_ctx.Journal.Id && j.ReturnMarker != null && j.Profile.Equals(a_ctx.Profile.Name, StringComparison.OrdinalIgnoreCase))
				// of this Mod Organizer: every run of the data folder is in one list, and another instance can have a profile of the same name
				.Where(j => (string.IsNullOrEmpty(j.Mo2Instance) ? Path.GetDirectoryName(j.Mo2Exe) : j.Mo2Instance) is { } theirs &&
					(string.IsNullOrEmpty(a_ctx.Journal.Mo2Instance) ? Path.GetDirectoryName(a_ctx.Journal.Mo2Exe) : a_ctx.Journal.Mo2Instance) is { } mine && Files.SamePath(theirs, mine))
				.Where(j => j.Step("cleansave")?.Ended is { } ended && Math.Abs((ended.ToUniversalTime() - saved).TotalMinutes) < 5)
				.OrderByDescending(j => j.Started).Select(j => j.ReturnMarker).FirstOrDefault();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	public static DateTime? SaveTime(Profile a_profile, string? a_name)
	{
		var f = a_name == null ? null : Path.Combine(SavesDir(a_profile), a_name + ".ess");
		return f != null && File.Exists(f) ? File.GetLastWriteTimeUtc(f) : null;
	}

	// which save the save to play starts from (the fourth review, H1): this run's clean save; else the one the setup names; else
	// an older run's clean save ONLY when it is newer than the player's own newest save (the player has not played since); else
	// the player's newest - never an old clean save over progress made after it
	public static (string? Save, string Why) PlaySaveFrom(string? a_thisRun, string? a_named, DateTime? a_oldClean, string? a_player, DateTime? a_playerTime)
	{
		if (a_thisRun != null) {
			return (a_thisRun, "the clean save this run made");
		}
		if (!string.IsNullOrEmpty(a_named)) {
			return (a_named, "the save the setup names");
		}
		if (a_oldClean != null && (a_playerTime == null || a_oldClean > a_playerTime)) {
			return (SaveClean, "an earlier run's clean save - no save of yours is newer");
		}
		return a_player != null
			? (a_player, a_oldClean != null ? "your newest save - newer than the earlier run's clean save" : "your newest save (this run made no clean save)")
			: (null, "no save to load");
	}

	// after a run that did not finish: when the newest save on disk is one the runner's game made (QASmoke, DynDOLOD maybe off),
	// the player is told which of their own to load (the fourth review, M5)
	public static string? SaveWarning(RunContext a_ctx)
	{
		try {
			var newest = NewestSave(a_ctx.Profile);
			if (newest == null || Brand.AllNames.Any(b => newest.Equals(b + " - ready to play", StringComparison.OrdinalIgnoreCase))) {
				return null;
			}
			var ours = Brand.Made(newest) ||
			           SaveTime(a_ctx.Profile, newest) is { } t && GameTimes(a_ctx.Journal.Steps, DateTime.Now).Any(r => t >= r.From && t <= r.To);
			if (!ours) {
				return null;
			}
			var mine = NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx), Character(a_ctx));
			return $"your newest save \"{newest}\" was made by this run's game (in QASmoke, DynDOLOD maybe off) - to play, load your own save" +
				(mine != null ? $" \"{mine}\"" : "");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// does the profile tick DynDOLOD's DLL? (the default for switching it off / on in game - a setup that does not say)
	public static bool HasDynDolod(Profile a_profile) =>
		a_profile.States().Where(s => s.Value == '+').Any(s => File.Exists(Path.Combine(a_profile.Mo2.ModDir(s.Key), "SKSE", "Plugins", "DynDOLOD.dll")));

	// the clean save's "dyndolod_off" (the save to play reads the same one - the fourth review, L1 / L2)
	static bool DynOff(RunContext a_ctx) =>
		a_ctx.Setup.StepSettings("cleansave") is { } cs && cs["dyndolod_off"] != null ? new StepSettings(cs).Bool("dyndolod_off", false) : HasDynDolod(a_ctx.Profile);

	// a box that says another will follow (a perk overhaul resetting perks: "A message box will appear when the process is
	// complete") - the wait goes on until that one came, or 10 minutes
	public static bool PromisesBox(string a_body) =>
		a_body.Contains("message box will appear", StringComparison.OrdinalIgnoreCase) || a_body.Contains("message will appear", StringComparison.OrdinalIgnoreCase) ||
		a_body.Contains("another message box", StringComparison.OrdinalIgnoreCase);

	// when the settle ends after a box: a promise holds it up to 10 minutes; the box it promised (or any other) ends that hold,
	// leaving the plain settle time or 10 s more, whichever is later
	public static DateTime SettleUntil(DateTime a_plain, DateTime a_now, string a_body) =>
		PromisesBox(a_body) ? a_now.AddMinutes(10) : a_plain > a_now.AddSeconds(10) ? a_plain : a_now.AddSeconds(10);

	// a save just loaded is given time to settle before anything is done in it (his flow: a full minute); a mod's message box
	// that opens meanwhile (seconds after the load, pausing the game) is answered like a load box
	static void Settle(RunContext a_ctx, string a_step, int a_seconds, string a_why)
	{
		a_ctx.Activity(a_step, a_why);
		var plain = DateTime.UtcNow.AddSeconds(a_seconds);
		var until = plain;
		var nextLook = DateTime.UtcNow;
		while (DateTime.UtcNow < until) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw GameClosed(a_ctx, "while the save settled");
			}
			if (DateTime.UtcNow >= nextLook) {
				nextLook = DateTime.UtcNow.AddSeconds(5);
				try {
					if (AnswerGameBox(a_ctx) is { } body) {
						until = SettleUntil(plain, DateTime.UtcNow, body);
						if (PromisesBox(body)) {
							a_ctx.Log.Detail("that box says another follows - waiting for it (up to 10 minutes)");
						}
					}
				} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
				}
			}
			a_ctx.Pause(TimeSpan.FromSeconds(1));
		}
	}

	// The setup's word on a message box of the game that the run does not know: "close" (the default: it is closed at once
	// and named) or "stop" (it is the player's to answer; nobody there, the step stops naming it).
	public const string UnknownBoxSetting = "unknownGameBox";

	public static bool ClosesUnknown(Setup a_setup) => !"stop".Equals(Setup.Str(a_setup.Root, UnknownBoxSetting)?.Trim(), StringComparison.OrdinalIgnoreCase);

	// the game's own message box as DevBench reads it: its text, its buttons' words, how many there are when their words
	// are not given, and the button the game itself marks as its cancel (-1: none)
	public sealed record GameBox(string Body, IReadOnlyList<string> Labels, int? Count, int Cancel);

	// the game's own tutorial pop-up: no message box, and it stops the game under it all the same
	const string TutorialMenu = "Tutorial Menu";

	// a_send: how this look does something to the game (LookAtGameBox's Send) - it answers false when it may not right now
	static GameBox? ReadGameBox(RunContext a_ctx, int a_timeoutSeconds, Func<Action, bool> a_send)
	{
		var list = a_ctx.DevBench.Call("menu", new { action = "list" }, a_timeoutSeconds);
		if ((list["openMenus"] as JsonArray)?.Any(m => m?.ToString() == TutorialMenu) == true &&
		    a_send(() => a_ctx.DevBench.Call("menu", new { action = "close", name = TutorialMenu }, a_timeoutSeconds))) {
			a_ctx.Log.Detail("the game's tutorial pop-up closed");
		}
		if (list["messageBoxOpen"]?.GetValue<bool>() != true) {
			return null;
		}
		var d = a_ctx.DevBench.Call("menu", new { action = "describe" }, a_timeoutSeconds);
		if (d["messageBoxOpen"]?.GetValue<bool>() == false) {
			return null;
		}
		var body = string.Join(" ", new[] { "bodyText", "text", "message" }.Select(k => d[k]?.ToString() ?? "")).Trim();
		var labels = (d["buttons"] as JsonArray ?? d["buttonTexts"] as JsonArray ?? [])
			.Select(b => b is JsonObject bo ? (bo["text"] ?? bo["label"])?.ToString() ?? "" : b?.ToString() ?? "").ToList();
		return new GameBox(body, labels, d["buttonCount"]?.GetValue<int>(), d["cancelIndex"] is JsonValue c && c.TryGetValue<int>(out var cancel) ? cancel : -1);
	}

	// how often the same box is answered before it is taken for one that does not close
	const int GameBoxTries = 5;

	// One look at the game's own message box. One the run knows is answered by its text and labels (DecideBox). One it does
	// not know is closed all the same, by the button that takes nothing on (DevBench.CloseIndex) - his word of 2026-11-21:
	// "it needs to be externally found no matter what the box could potentially be and immediately closed" - and named in the
	// log and the run's notes; a setup may say such a box is the player's (ClosesUnknown), and then a_left gets it,
	// unanswered - as it gets a box that still stands after its answer was given GameBoxTries times.
	// Returns the text of a box that was answered or closed, else null. The run's watch makes this look every few seconds
	// for as long as the run's game is up, whatever the step in hand does, and a step makes it where it waits on the game:
	// one look at a time. a_fromWatch: what was answered is left for the step in hand to take (AnswerGameBox).
	public static string? LookAtGameBox(RunContext a_ctx, out GameBox? a_left, bool a_fromWatch, int a_timeoutSeconds = 10)
	{
		a_left = null;
		// What the watch's look sends to the game (an answer, a pop-up closed) goes through the watch itself: it begins only
		// while the run still holds the watch (BoxWatch.Do). A look that was still reading when the run let go - DevBench can
		// take seconds to answer - sent its answer into a game the run had already left. A step's own look sends as ever.
		bool Send(Action a_call)
		{
			if (a_fromWatch && a_ctx.Boxes is { } watch) {
				return watch.Do(a_call);
			}
			a_call();
			return true;
		}
		lock (a_ctx.GameBoxGate) {
			if (ReadGameBox(a_ctx, a_timeoutSeconds, Send) is not { } box) {
				a_ctx.GameBoxAnswered = ("", 0);
				return null;
			}
			var tries = a_ctx.GameBoxAnswered.Body == box.Body ? a_ctx.GameBoxAnswered.Times : 0;
			if (tries >= GameBoxTries) {
				a_left = box;
				return null;
			}
			var (answer, idx) = DevBench.DecideBox(box.Body, box.Labels, box.Count);
			if (answer == null) {
				// a game in another language (his checklist 439); read again until the game settings answer, a load may not serve them yet
				if (a_ctx.GameWords is not { HasAskYes: > 0 }) {
					a_ctx.GameWords = GameWords.Read(a_ctx.Mo2.GameDir, s => GameSetting(a_ctx, s));
				}
				(answer, idx) = DevBench.DecideBox(box.Body, box.Labels, box.Count, a_ctx.GameWords);
			}
			var said = box.Body.Length > 160 ? box.Body[..160] + " ..." : box.Body;
			string? note = null;
			if (answer == null) {
				if (!ClosesUnknown(a_ctx.Setup)) {
					a_left = box;
					return null;
				}
				idx = DevBench.CloseIndex(box.Labels, box.Cancel, box.Count, a_ctx.GameWords);
				var with = idx < box.Labels.Count && box.Labels[idx].Trim() != "" ? $"\"{box.Labels[idx].Trim()}\"" : $"its button {idx + 1}";
				answer = $"closed with {with} (the run does not know this box)";
				note = $"the game showed a message this run does not know, and it was closed with {with}: {said} - if that was the wrong answer, switch that question off in the mod that asks it, " +
				       "or have the run leave such messages to you (Settings > Game steps)";
			}
			if (!Send(() => a_ctx.DevBench.Call("menu", new { action = "accept", index = idx }))) {
				// the run has let go of the watch (or the watch is in the middle of something else): nothing was answered
				return null;
			}
			a_ctx.GameBoxAnswered = (box.Body, tries + 1);
			// said once for a box, however often its answer has to be given
			if (tries == 0) {
				a_ctx.Log.Say($"game box {answer}: {said}", note != null ? Level.Warn : Level.Detail);
				a_ctx.BoxAnswered(a_fromWatch ? box.Body : null, note);
			}
			return box.Body;
		}
	}

	// The game's message box for the step in hand: what the run's watch answered since the step last asked, else a look of
	// its own. Returns the text of a box that was answered or closed, or null when none was. A box that is left to the player
	// stops a step that runs without the run's watch (the watch itself gives the player their time, then stops the step).
	static string? AnswerGameBox(RunContext a_ctx)
	{
		if (a_ctx.TakeAnsweredBox() is { } byTheWatch) {
			return byTheWatch;
		}
		var body = LookAtGameBox(a_ctx, out var left, false);
		if (left != null && a_ctx.Boxes == null) {
			throw new StepFailedException("the game shows a box this step does not answer: " + left.Body);
		}
		return body;
	}

	// The run's watch looks into the run's own game for its message boxes (BoxWatch's beat, every few seconds from the
	// run's first step to its last): what the game shows on its own screen is no window of Windows', so nothing outside the
	// game sees it but DevBench. Returns a box that was left unanswered, for the watch to name and to give the player
	// their time with; null when there is none (or no game of the run's, or a DevBench that does not answer right now).
	public static BoxWatch.Open? WatchGameBox(RunContext a_ctx)
	{
		try {
			using var game = Game(a_ctx);
			if (game == null) {
				a_ctx.DevBenchGame = 0;
				return null;
			}
			// DevBench answers for whichever game holds its port: only the run's own game is ever answered for
			if (a_ctx.DevBenchGame != game.Id) {
				var health = a_ctx.DevBench.Call("inspect", new { kind = "health" }, 3);
				if (health["pid"] is JsonValue p && p.TryGetValue<int>(out var pid) && pid != game.Id) {
					return null;
				}
				a_ctx.DevBenchGame = game.Id;
			}
			LookAtGameBox(a_ctx, out var left, true, 4);
			if (left == null) {
				return null;
			}
			// (told apart from a window's handle and from every other text by a number made of its own words)
			var id = unchecked((long)0x4000_0000_0000_0000 | (uint)string.GetHashCode(left.Body + "\n" + string.Join("\n", left.Labels), StringComparison.Ordinal));
			return new BoxWatch.Open(id, new BoxSeen(GameImage, true, "a message of the game", left.Body, left.Labels), _ => { });
		} catch (Exception e) when (e is not OutOfMemoryException) {
			// The game is loading, DevBench is not up yet, or it answered something this look cannot read: the next beat looks
			// again. Nothing leaves this look - what it throws would take the beat's look at Windows' own boxes with it
			return null;
		}
	}

	static string? GameSetting(RunContext a_ctx, string a_name)
	{
		try {
			return a_ctx.DevBench.Papyrus("Game", "GetGameSettingString", a_name)?.ToString();
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException or StepFailedException) {
			// (a game that did not run the call - a load in its way - has no word to give right now)
			return null;
		}
	}

	// a Windows box the game or SKSE's loader shows before DevBench answers (DevBench cannot see it, and the start waits under
	// it): SKSE's "a plugin failed to load" question is answered No - the game goes on without that plugin, which is named - a
	// one-button notice is acknowledged and named; any other box stops the step with its text
	public static (string? Press, string Note) NativeBoxVerdict(string a_title, string a_text, IReadOnlyList<string> a_buttons)
	{
		var all = a_title + " " + a_text;
		var text = a_text.Length > 300 ? a_text[..300] + " ..." : a_text;
		var labels = a_buttons.Select(b => b.Replace("&", "").Trim()).ToList();
		if (all.Contains("plugin", StringComparison.OrdinalIgnoreCase) && (all.Contains("error", StringComparison.OrdinalIgnoreCase) ||
		    all.Contains("fail", StringComparison.OrdinalIgnoreCase)) && labels.FirstOrDefault(l => l.Equals("No", StringComparison.OrdinalIgnoreCase)) is { } no) {
			return (no, "an SKSE plugin did not load - the game goes on without it (check that plugin's requirements): " + text);
		}
		if (labels.Count == 1) {
			// a box whose one button says Cancel is no notice: it shows something going on (a progress window), and pressing
			// it would call that off (Uia.BoxNames tells it from a plain notice's lone OK, which carries Cancel's control id)
			if (labels[0].Equals("Cancel", StringComparison.OrdinalIgnoreCase)) {
				return (null, "the game shows a Windows box this step does not answer: its one button is Cancel, and pressing that would call off what it shows - " + text);
			}
			return (labels[0], "the game showed a notice: " + text);
		}
		return (null, "the game shows a Windows box this step does not answer: " + text);
	}

	// a Windows box as the start wait sees it: its title, text, button labels and a way to press one of them
	public sealed record NativeBox(string Title, string Text, IReadOnlyList<string> Buttons, Action<string> Press);

	// each box answered by its verdict and named in the log; one the step does not answer stops it
	public static void AnswerNativeBoxes(IEnumerable<NativeBox> a_boxes, RunLog a_log)
	{
		foreach (var b in a_boxes) {
			var (press, note) = NativeBoxVerdict(b.Title, b.Text, b.Buttons);
			if (press == null) {
				throw new StepFailedException(note);
			}
			a_log.Warn(note);
			b.Press(press);
		}
	}

	// What the game shows right now, in plain words, for a step that is about to give up: this run's Windows boxes and, with
	// DevBench, the open game menus and message box. A step never blames its own signal (a log, a cache) while something else is
	// on screen - an unknown menu of another mod is named instead (his 2026-11-06 find: a mod's menu read as "no shader log").
	public static string WhatTheGameShows(RunContext a_ctx, DateTime a_launched)
	{
		var parts = new List<string>();
		try {
			foreach (var b in RunBoxes(a_ctx, a_launched)) {
				parts.Add($"a Windows box \"{b.Title}\": {(b.Text.Length > 200 ? b.Text[..200] + " ..." : b.Text)}");
			}
		} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or System.Runtime.InteropServices.COMException) {
		}
		try {
			var list = a_ctx.DevBench.Call("menu", new { action = "list" }, 10);
			var menus = (list["menus"] ?? list["open"] ?? list["openMenus"]) as JsonArray ?? [];
			var names = menus.Select(m => m is JsonObject mo ? (mo["name"] ?? mo["menu"])?.ToString() : m?.ToString()).OfType<string>()
				.Where(n => n is not ("" or "HUD Menu" or "Cursor Menu" or "Main Menu" or "Loading Menu" or "Fader Menu")).ToList();
			if (names.Count > 0) {
				parts.Add("the game's open menus: " + string.Join(", ", names));
			}
			if (IsTrue(list["messageBoxOpen"])) {
				var d = a_ctx.DevBench.Call("menu", new { action = "describe" }, 10);
				parts.Add("a game message: " + string.Join(" ", new[] { "bodyText", "text", "message" }.Select(k => d[k]?.ToString() ?? "")).Trim());
			}
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
		}
		return parts.Count == 0 ? "" : string.Join("; ", parts);
	}

	// the boxes of THIS run's game and of an SKSE loader started since its launch - a game the player starts is never touched
	static List<NativeBox> RunBoxes(RunContext a_ctx, DateTime a_launched)
	{
		var pids = new List<int>();
		using (var g = Game(a_ctx)) {
			if (g != null) {
				pids.Add(g.Id);
			}
		}
		foreach (var p in Procs.ByImage("skse64_loader.exe")) {
			using (p) {
				if (Procs.StartTime(p.Id) is { } t && t >= a_launched.ToUniversalTime().AddSeconds(-2)) {
					pids.Add(p.Id);
				}
			}
		}
		var outList = new List<NativeBox>();
		// only Windows' own dialog windows: UI Automation on the loading game's main window can wait for its timeout each time
		if (pids.Count == 0) {
			return outList;
		}
		foreach (var h in Native.TopWindowsOf(pids).Where(h => Native.ClassName(h) == "#32770")) {
			if (Uia.FromHandle(h) is not { } e) {
				continue;
			}
			var buttons = Uia.RealButtons(e);
			// each button by what it is: its words are Windows' language
			var names = Uia.BoxNames(buttons);
			outList.Add(new NativeBox(Uia.Name(e), Uia.TextOf(e, 800), names,
				label => {
					if (names.IndexOf(label) is >= 0 and var at) {
						Uia.Press(buttons[at]);
					}
				}));
		}
		return outList;
	}

	// after a teleport: the new place loaded (the player there, no loading screen), within 5 minutes
	static void WaitPlace(RunContext a_ctx)
	{
		var sw = Stopwatch.StartNew();
		a_ctx.Pause(TimeSpan.FromSeconds(3));
		while (sw.Elapsed < TimeSpan.FromMinutes(5)) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw GameClosed(a_ctx, "while the new place loaded");
			}
			try {
				if (a_ctx.DevBench.Call("inspect", new { kind = "state" }, 10)["playerLoaded"]?.GetValue<bool>() == true &&
				    a_ctx.DevBench.Call("menu", new { action = "list" }, 10)["loading"]?.GetValue<bool>() != true) {
					return;
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or FormatException) {
			}
			a_ctx.Pause(TimeSpan.FromSeconds(3));
		}
		throw new StepFailedException("the new place did not finish loading within 5 minutes");
	}

	// false only when a list Windows gave shows the run's game gone: a list it would not give is no answer, and the game counts
	// as still up (never "closed" on a failed look)
	static bool GameUp(RunContext a_ctx)
	{
		using var g = Game(a_ctx);
		return g != null || Procs.LookFailed;
	}

	// a step that only helps (the horse check, the marker removal, DynDOLOD back on): any DevBench failure is a warning, a real
	// Stop is not (a DevBench timeout is a TaskCanceledException too, which the run would otherwise read as a Stop)
	static bool Helps(RunContext a_ctx, Exception a_e) => !a_ctx.Cancel.IsCancellationRequested &&
		a_e is StepFailedException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidOperationException or FormatException;

	const string Player = "0x00000014";

	// what the player is doing just before a teleport (his checklist 458)
	public sealed record PlayerState(bool Mounted, bool Beast, bool Menu, bool ControlsOff, bool Busy);

	// the game's werewolf race, or Dawnguard's vampire lord race as the game itself resolves it (a_vampireLord, null without Dawnguard)
	public static bool IsBeastRace(string? a_formKey, string? a_vampireLord) =>
		a_formKey != null && (a_formKey.Equals("0x000CDD84", StringComparison.OrdinalIgnoreCase) || a_formKey.Equals(a_vampireLord, StringComparison.OrdinalIgnoreCase));

	// what holds a teleport, the most serious first (null = nothing). Stop: it does not pass by waiting long enough to be sure, and a
	// save made in it would be a bad one to rebuild from; the rest are waited out and then go on with a word in the log, so a reading
	// that is wrong never fails a run
	public static (string Why, bool Stop)? WhatHoldsTeleport(PlayerState a_s) =>
		a_s.Beast ? ("in werewolf or vampire lord form", true) :
		a_s.Mounted ? ("still on a mount (a dragon in flight cannot be left)", true) :
		a_s.Busy ? ("in a kill move or bleeding out", false) :
		a_s.Menu ? ("in a conversation or a menu", false) :
		a_s.ControlsOff ? ("in a scene, with your controls off", false) : null;

	static PlayerState ReadPlayer(RunContext a_ctx, string? a_vampireLord)
	{
		bool On(string a_function) => a_ctx.DevBench.PapyrusOn(Player, "Actor", a_function)?.GetValue<bool>() == true;
		return new PlayerState(On("IsOnMount"), IsBeastRace(DevBench.FormKey(a_ctx.DevBench.PapyrusOn(Player, "Actor", "GetRace")), a_vampireLord),
			a_ctx.DevBench.Papyrus("Utility", "IsInMenuMode")?.GetValue<bool>() == true,
			a_ctx.DevBench.Papyrus("Game", "IsMovementControlsEnabled")?.GetValue<bool>() == false,
			On("IsInKillMove") || On("IsBleedingOut"));
	}

	// how long what stops a step, and what only warns, is waited out; how often it is looked at; how often a mount is asked to be left
	public static TimeSpan TeleportWait { get; set; } = TimeSpan.FromMinutes(3);
	public static TimeSpan TeleportShortWait { get; set; } = TimeSpan.FromMinutes(1);
	public static TimeSpan TeleportPoll { get; set; } = TimeSpan.FromSeconds(3);
	public static TimeSpan DismountEvery { get; set; } = TimeSpan.FromSeconds(30);

	// what holds a teleport waited out: null = go on; else why the step must stop
	public static string? WaitUntilFree(Func<PlayerState> a_read, Action a_dismount, RunLog a_log, CancellationToken a_ct)
	{
		var sw = Stopwatch.StartNew();
		var since = TimeSpan.Zero;
		TimeSpan? asked = null;
		string? said = null;
		while (true) {
			var s = a_read();
			if (s.Mounted && (asked == null || sw.Elapsed - asked >= DismountEvery)) {
				asked = sw.Elapsed;
				a_dismount();
			}
			if (WhatHoldsTeleport(s) is not { } h) {
				return null;
			}
			if (said != h.Why) {
				said = h.Why;
				// each hold has its own limit (a conversation after a long dragon flight still gets its minute); past this point none
				// starts its time again, so holds that keep taking turns end too
				if (sw.Elapsed <= TeleportWait * 2) {
					since = sw.Elapsed;
				}
				a_log.Detail($"you are {h.Why} - waiting before the teleport");
			}
			var limit = h.Stop ? TeleportWait : TeleportShortWait;
			if (sw.Elapsed - since > limit) {
				if (h.Stop) {
					return $"your character is {h.Why}, after {sw.Elapsed.TotalMinutes:0.#} minute(s) of waiting - load a save where you are not, and run this step again";
				}
				a_log.Warn($"your character still reads as {h.Why}, after {sw.Elapsed.TotalMinutes:0.#} minute(s) of waiting - going on with the teleport");
				return null;
			}
			a_ct.WaitHandle.WaitOne(TeleportPoll);
			a_ct.ThrowIfCancellationRequested();
		}
	}

	// before a teleport: off a mount, out of a fight (god mode is on from the load) - his addition 4; a beast form, a dragon, a
	// conversation, a menu or a scene waited out (his checklist 458). Swimming needs nothing: the teleport itself ends it
	static void ReadyToMove(RunContext a_ctx)
	{
		string? stop;
		try {
			a_ctx.DevBench.PapyrusOn(Player, "Actor", "StopCombat");
			var vampireLord = DevBench.FormKey(a_ctx.DevBench.Papyrus("Game", "GetFormFromFile", 0x283A, "Dawnguard.esm"));
			stop = WaitUntilFree(() => ReadPlayer(a_ctx, vampireLord), () => {
				a_ctx.DevBench.PapyrusOn(Player, "Actor", "Dismount");
				a_ctx.Log.Detail("asked to get off the mount first");
			}, a_ctx.Log, a_ctx.Cancel);
			if (stop == null) {
				// a fight that started during the wait
				a_ctx.DevBench.PapyrusOn(Player, "Actor", "StopCombat");
			}
		} catch (Exception e) when (Helps(a_ctx, e)) {
			a_ctx.Log.Warn("could not check your character before the teleport: " + e.Message);
			return;
		}
		if (stop != null) {
			throw new StepFailedException(stop);
		}
	}

	static bool IsTrue(JsonNode? a_n) => a_n is JsonValue v && v.TryGetValue<bool>(out var b) && b;

	// the player's followers in a DevBench actor list: every living teammate loaded near them, never the player (his addition 4,
	// "followers come along" - the game's follow package can leave one behind after a coc / MoveTo)
	public static List<string> Teammates(JsonObject a_refs) =>
		(a_refs["refs"] as JsonArray ?? []).OfType<JsonObject>()
			.Where(r => r["actor"] is JsonObject a && IsTrue(a["playerTeammate"]) && IsTrue(a["alive"]))
			.Select(r => DevBench.FormKey(r["formId"])).OfType<string>().Where(k => k != Player).Distinct().ToList();

	static List<string> FindFollowers(RunContext a_ctx)
	{
		try {
			var found = Teammates(a_ctx.DevBench.Call("inspect", new { kind = "refs", formType = "Actor", radius = 4096, limit = 2000 }, 20));
			if (found.Count > 0) {
				a_ctx.Log.Detail($"{found.Count} follower(s) with you: {string.Join(", ", found)}");
			}
			return found;
		} catch (Exception e) when (Helps(a_ctx, e)) {
			a_ctx.Log.Warn("your followers could not be read before the teleport: " + e.Message);
			return [];
		}
	}

	// after a teleport: each follower moved to the player, a step behind and side by side so no two stand on one spot
	static void BringFollowers(RunContext a_ctx, List<string> a_followers)
	{
		var moved = 0;
		foreach (var (f, i) in a_followers.Select((f, i) => (f, i))) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			try {
				a_ctx.DevBench.PapyrusOn(f, "ObjectReference", "MoveTo", new { form = Player }, (i % 3 - 1) * 70.0, -90.0 - i / 3 * 70.0, 0.0, true);
				++moved;
			} catch (Exception e) when (Helps(a_ctx, e)) {
				a_ctx.Log.Warn($"follower {f} was not moved to you: " + e.Message);
			}
		}
		if (moved > 0) {
			a_ctx.Log.Detail($"{moved} follower(s) brought along");
		}
	}

	// a teleport that takes the followers along: who is with the player read first, then the move, the new place, each follower
	public static void MoveWithFollowers(RunContext a_ctx, Action a_move, Action a_waitPlace)
	{
		var followers = FindFollowers(a_ctx);
		a_ctx.Cancel.ThrowIfCancellationRequested();
		a_move();
		a_waitPlace();
		BringFollowers(a_ctx, followers);
	}

	public static string? NewestSave(Profile a_profile, DateTime? a_after = null)
	{
		var d = SavesDir(a_profile);
		if (!Directory.Exists(d)) {
			return null;
		}
		var f = new DirectoryInfo(d).EnumerateFiles("*.ess").Where(x => a_after == null || x.LastWriteTimeUtc > a_after.Value.ToUniversalTime())
			.OrderByDescending(x => x.LastWriteTimeUtc).FirstOrDefault();
		return f == null ? null : Path.GetFileNameWithoutExtension(f.Name);
	}

	// the offline tests' stand-in for a game start (never set in a run): StartAndLoad's own loop is tested without a game
	public static Action<RunContext, StepSettings>? LaunchStandIn { get; set; }

	static void LaunchGame(RunContext a_ctx, StepSettings a_s)
	{
		if (LaunchStandIn is { } standIn) {
			standIn(a_ctx, a_s);
			return;
		}
		NoGameRunning(a_ctx);
		GameStart.Prepare(a_ctx);
		a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", FreshGamePid(), a_ctx.Cancel);
	}

	// the game up, any compile waited out, `a_save` loaded, frames moving, god mode on (nothing may kill the player while it waits)
	// returns whether the shaders compiled at this start
	public static bool StartAndLoad(RunContext a_ctx, string a_step, StepSettings a_s, string a_save)
	{
		var limit = TimeSpan.FromMinutes(a_s.Int("start_min", 8));
		var offers = 0;
		Stopwatch sw;
		DateTime started;
		while (true) {
			var launched = DateTime.Now;
			a_ctx.GameLaunched = launched;
			LaunchGame(a_ctx, a_s);
			sw = Stopwatch.StartNew();
			started = DateTime.Now;
			var offered = false;
			while (!a_ctx.DevBench.Answers()) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				if (sw.Elapsed > limit) {
					throw new StepFailedException($"the game (or DevBench) did not answer within {limit.TotalMinutes:0} minutes");
				}
				// Faster Archives closes the game itself, a second into its start, to show its offer: no crash - the game is
				// started again once its window has closed
				if (sw.Elapsed > TimeSpan.FromSeconds(5) && !GameUp(a_ctx) && FasterArchives.Offered(LogFolders(a_ctx.Setup, a_ctx.Mo2), launched, a_ctx.Control.Started)) {
					offered = true;
					break;
				}
				if (sw.Elapsed > TimeSpan.FromSeconds(30) && !GameUp(a_ctx)) {
					throw GameClosed(a_ctx, "while it was starting");
				}
				// the run's own watch answers these the whole run; a step run without one looks here
				if (a_ctx.Boxes == null) {
					AnswerNativeBoxes(RunBoxes(a_ctx, launched), a_ctx.Log);
				}
				a_ctx.Pause(TimeSpan.FromSeconds(2));
			}
			if (!offered) {
				break;
			}
			FasterArchives.Wait(a_ctx, a_step, a_s, ++offers, launched);
		}
		a_ctx.Log.Detail($"the game is up after {sw.Elapsed.TotalSeconds:0} s");
		var compiled = WaitCompile(a_ctx, a_step, a_s, started);
		// His rule of 2026-11-21: a run builds its shader cache once, in its first step, and every later game start uses it.
		// Shaders compiled at a start that comes after this run's own shader step are a fault to look into - said in the
		// log and in the run's notes, never only waited out.
		if (compiled && a_ctx.Journal.Step("shaders") is { State: StepState.Done }) {
			const string Again = "the game compiled shaders at this start although this run's shader step had made the cache ready - " +
				"this start needed shaders that step did not build (a mod switched for this step that changes shaders, or a shader mod that does not keep its cache)";
			a_ctx.Log.Warn(Again);
			// once in the run's notes, however many of its game starts it happens at (the log has each)
			if (!a_ctx.Journal.Notes.Contains("shaders: " + Again)) {
				a_ctx.Note("shaders: " + Again);
			}
		}
		a_ctx.Activity(a_step, $"loading \"{a_save}\"");
		a_ctx.Log.Detail($"loading \"{a_save}\"");
		var t = Stopwatch.StartNew();
		while (true) {
			try {
				a_ctx.DevBench.Call("game", new { action = "load", name = a_save });
				break;
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				// a game that went in the meantime is said as gone, with its crash report - never as a load it would not take
				if (!GameUp(a_ctx)) {
					throw GameClosed(a_ctx, $"before it took the load of \"{a_save}\"");
				}
				if (t.Elapsed > TimeSpan.FromMinutes(3)) {
					throw new StepFailedException($"the game never accepted the load of \"{a_save}\": {e.Message}");
				}
				a_ctx.Pause(TimeSpan.FromSeconds(3));
			}
		}
		t.Restart();
		while (t.Elapsed < limit) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw GameClosed(a_ctx, $"while loading \"{a_save}\"");
			}
			try {
				if (AnswerGameBox(a_ctx) != null) {
					a_ctx.Pause(TimeSpan.FromSeconds(3));
					continue;
				}
				if (a_ctx.DevBench.Call("inspect", new { kind = "state" }, 10)["playerLoaded"]?.GetValue<bool>() == true) {
					var f1 = a_ctx.DevBench.Frame();
					a_ctx.Pause(TimeSpan.FromSeconds(2));
					var f2 = a_ctx.DevBench.Frame();
					if (f1 != null && f2 > f1) {
						a_ctx.Log.Detail($"\"{a_save}\" loaded after {t.Elapsed.TotalSeconds:0} s");
						a_ctx.DevBench.Console("tgm");
						return compiled;
					}
				}
			} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
			}
			a_ctx.Pause(TimeSpan.FromSeconds(3));
			// a compile that started under the load is never cut off - the load clock waits while one runs
			if (t.Elapsed > limit - TimeSpan.FromMinutes(1) && Compiling(LogFolders(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, started)) {
				a_ctx.Log.Detail("shaders still compiling under the load - waiting for them");
				// counted as a compile at this start too (the save to play reports a stale cache - the fourth review, L3)
				compiled = true;
				t.Restart();
			}
		}
		throw new StepFailedException($"\"{a_save}\" did not finish loading in {limit.TotalMinutes:0} minutes");
	}

	// a Community Shaders-type plugin by its file name (Community Shaders itself, Open Shaders, Bottled Shaders)
	public static bool IsShaderDll(string a_fileName) =>
		a_fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && (a_fileName.Equals("CommunityShaders.dll", StringComparison.OrdinalIgnoreCase) ||
			a_fileName.Contains("OpenShaders", StringComparison.OrdinalIgnoreCase) || a_fileName.Contains("BottledShaders", StringComparison.OrdinalIgnoreCase));

	// Is there a shader mod in this game at all? One of the known plugins in a ticked mod, in overwrite or in the game's own
	// folder; a cache the setup's own shader step names for a ticked shader mod; or a shader log this very start wrote (the
	// shader mod writes it as the game starts, before the game answers). With none of them nothing compiles, and the watch is
	// not sat out: a profile on ENB, or with no shader mod, waited five minutes at the main menu at each of its three game
	// starts. Anything that cannot be looked at counts as "there is one": the watch then runs as before.
	public static bool ShaderModHere(RunContext a_ctx, IReadOnlyList<string> a_logFolders, DateTime a_since)
	{
		try {
			if (NewestShaderLog(a_logFolders) is { } log && File.GetLastWriteTimeUtc(log) >= a_since.ToUniversalTime().AddSeconds(-5)) {
				return true;
			}
			var states = a_ctx.Profile.States();
			if (a_ctx.Setup.StepSettings("shaders") is { } shaders && Steps.ShaderStep.Caches(new StepSettings(shaders)).Any(c => states.TryGetValue(c.ShaderMod, out var f) && f == '+')) {
				return true;
			}
			return states.Where(s => s.Value == '+').Select(s => a_ctx.Mo2.ModDir(s.Key)).Append(a_ctx.Mo2.OverwriteDir).Append(Path.Combine(a_ctx.Mo2.GameDir, "Data"))
				.Select(d => Path.Combine(d, "SKSE", "Plugins")).Where(Directory.Exists)
				.Any(d => Directory.EnumerateFiles(d, "*.dll").Any(f => IsShaderDll(Path.GetFileName(f))));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return true;
		}
	}

	// What the shader mod itself says about its start-up pass, where it says anything (Open Shaders does, through DevBench's
	// "shadercache"): is it compiling, how many shaders it queued, how many of them are done, and how many it took from its
	// cache on disk (null where it does not say that).
	public sealed record ShaderPass(bool Compiling, long Total, long Done, long? FromCache)
	{
		// its pass ran and is over: nothing is left to wait for
		public bool Over => !Compiling && Total > 0 && Done >= Total;

		// how many shaders this start had to compile itself (0: every one came from the cache; null: it does not say)
		public long? Compiled => FromCache is { } hits ? Math.Max(0, Total - hits) : null;

		// how many of those it has compiled so far: the ones done that did not come from the cache. While the pass is still
		// going, the shaders not done yet say nothing - they may all still come from the cache (3,201 queued, 40 done, 40 of
		// them read from disk: none compiled so far, though Compiled reads 3,161)
		public long? CompiledSoFar => FromCache is { } hits ? Math.Max(0, Done - hits) : null;
	}

	// null: this shader mod's answer does not say (another shader mod, an older version)
	public static ShaderPass? ReadShaderPass(JsonObject a_state)
	{
		static long? Number(JsonNode? a_n) => a_n is JsonValue v && v.TryGetValue<long>(out var n) ? n : null;
		if (a_state["compiling"] is not JsonValue c || !c.TryGetValue<bool>(out var compiling) || Number(a_state["totalTasks"]) is not { } total ||
		    Number(a_state["completedTasks"]) is not { } done) {
			return null;
		}
		return new ShaderPass(compiling, total, done, Number(a_state["diskHitTasks"]));
	}

	static ShaderPass? ShaderPassNow(RunContext a_ctx)
	{
		try {
			return ReadShaderPass(a_ctx.DevBench.Call("inspect", new { kind = "shadercache" }, 5));
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or FormatException or System.Text.Json.JsonException) {
			// no shader mod that answers this, or a game that does not answer right now
			return null;
		}
	}

	// what a start's shader pass came to, in the log's words
	public static string ShaderPassSaid(ShaderPass a_pass) => a_pass.Compiled switch {
		0 => $"the shader mod says its shaders are ready: all {a_pass.Total} taken from its cache, none compiled",
		{ } compiled => $"the shader mod says its shaders are ready: {compiled} of {a_pass.Total} compiled at this start, the rest taken from its cache",
		_ => $"the shader mod says its shaders are ready ({a_pass.Done} of {a_pass.Total})",
	};

	// "You can't load a save while shaders are compiling" (his word). A compile can begin a minute after the game answers and
	// write its first line only when a shader finishes, so it is watched for `compile_watch_s` (300) before a load is sent -
	// unless the shader mod itself says its start-up pass is over: then there is nothing to watch for, and the load is sent
	// (a start with its cache whole once sat out the full five minutes at the main menu, at every game start of a run).
	// Returns whether shaders were compiled at this start (taken from the cache is not compiled).
	public static bool WaitCompile(RunContext a_ctx, string a_step, StepSettings a_s, DateTime a_since)
	{
		var logs = LogFolders(a_ctx.Setup, a_ctx.Mo2);
		if (!ShaderModHere(a_ctx, logs, a_since)) {
			a_ctx.Log.Detail("no shader mod in this game (no Community Shaders-type plugin, no shader log from this start) - no compile to wait for");
			return false;
		}
		var watch = TimeSpan.FromSeconds(a_s.Int("compile_watch_s", 300));
		var max = TimeSpan.FromMinutes(a_s.Int("compile_max_min", 90));
		var sw = Stopwatch.StartNew();
		// seen: the shader mod was at work - the wait goes on while it is. compiledSome: something says shaders were really
		// compiled (its own count, or its log). The two are not one: reading a whole cache back is work too, and a start that
		// only read its cache must never be reported as a cache that was out of date
		var seen = false;
		var compiledSome = false;
		var quietSince = TimeSpan.Zero;
		TimeSpan? passOverSince = null;
		TimeSpan? saidOverSince = null;
		while (sw.Elapsed < max) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (!GameUp(a_ctx)) {
				throw GameClosed(a_ctx, "while its shaders compiled");
			}
			// looked up again at every look: a folder a plugin maps the logs to may only be made, or get its first log, during the watch
			logs = LogFolders(a_ctx.Setup, a_ctx.Mo2);
			// The shader mod's own word comes first. Over (not compiling, every shader it queued done) and still over ten
			// seconds on: the load is sent. Compiling: that counts as seen, whatever its log shows.
			var pass = ShaderPassNow(a_ctx);
			if (pass is { Over: true }) {
				saidOverSince ??= sw.Elapsed;
				if (sw.Elapsed - saidOverSince >= TimeSpan.FromSeconds(10)) {
					a_ctx.Log.Detail(ShaderPassSaid(pass));
					return pass.Compiled is { } count ? count > 0 : compiledSome;
				}
			} else {
				saidOverSince = null;
			}
			// (only what it has done counts: a pass still going that has read every shader so far from the cache has compiled none)
			if (pass is { CompiledSoFar: > 0 }) {
				compiledSome = true;
			}
			if (pass is { Compiling: true }) {
				if (!seen) {
					// (compiled, or read back from the cache: the shader mod works through every shader either way)
					a_ctx.Log.Detail("the shader mod is making its shaders ready - the load waits for it");
					a_ctx.Activity(a_step, "waiting for the game's shaders to be ready");
				}
				seen = true;
				quietSince = sw.Elapsed;
				a_ctx.Pause(TimeSpan.FromSeconds(5));
				continue;
			}
			if (!seen && BootPassOverIn(logs, a_since) && !Compiling(logs, a_ctx.Mo2.OverwriteDir, a_since)) {
				passOverSince ??= sw.Elapsed;
				if (sw.Elapsed - passOverSince >= TimeSpan.FromSeconds(30)) {
					a_ctx.Log.Detail("the shader mod says its start-up pass is over - no compile to wait for");
					return false;
				}
			} else {
				passOverSince = null;
			}
			if (Compiling(logs, a_ctx.Mo2.OverwriteDir, a_since)) {
				if (!seen) {
					a_ctx.Log.Detail("shaders are compiling - the load waits for them");
					a_ctx.Activity(a_step, "waiting for the game's shaders to compile");
				}
				seen = true;
				compiledSome = true;
				quietSince = sw.Elapsed;
			} else if ((seen && sw.Elapsed - quietSince > TimeSpan.FromSeconds(30)) || (!seen && sw.Elapsed > watch)) {
				if (seen) {
					a_ctx.Log.Detail(compiledSome ? $"the shader compile finished after {sw.Elapsed.TotalMinutes:0.0} min"
						: $"the shader mod went quiet after {sw.Elapsed.TotalMinutes:0.0} min and could no longer be asked - nothing says a shader was compiled");
				}
				return compiledSome;
			}
			a_ctx.Pause(TimeSpan.FromSeconds(5));
		}
		throw new StepFailedException($"the shaders were still compiling after {max.TotalMinutes:0} minutes");
	}

	// the game's own save, waited until the file is on disk and stopped growing, then the game closed
	// how long a save may take to appear at all; one being written is never cut short by it (his checklist 459)
	public static TimeSpan SaveWait { get; set; } = TimeSpan.FromMinutes(3);

	// why a save did not happen, in the player's words: a mod that blocks saving is the usual reason
	public static string SaveNotMade(string a_name, bool a_forced) =>
		$"the save \"{a_name}\" was not made" + (a_forced
			? " - not even with the game's saving switched back on, so a mod blocks it some other way: switch that mod off for this run, or save by hand with the guided route"
			: " - a mod may be blocking saves (some survival or hardcore mods do). Settings > Game steps > \"Force the save\" tries once more with the game's saving switched back on");

	// true = the save is whole on disk; false = it never appeared within SaveWait
	static bool WaitForSave(RunContext a_ctx, string a_path, DateTime a_since)
	{
		var sw = Stopwatch.StartNew();
		while (true) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var there = File.Exists(a_path) && File.GetLastWriteTimeUtc(a_path) >= a_since;
			if (there) {
				var s1 = new FileInfo(a_path).Length;
				a_ctx.Pause(TimeSpan.FromSeconds(4));
				if (s1 > 0 && new FileInfo(a_path).Length == s1) {
					return true;
				}
			} else if (sw.Elapsed > SaveWait) {
				return false;
			}
			if (sw.Elapsed > TimeSpan.FromMinutes(10)) {
				throw new StepFailedException($"the save at {a_path} kept changing for 10 minutes");
			}
			a_ctx.Pause(TimeSpan.FromMilliseconds(1500));
		}
	}

	public static void SaveAndQuit(RunContext a_ctx, string a_name)
	{
		var t0 = DateTime.UtcNow.AddSeconds(-2);
		a_ctx.DevBench.Call("game", new { action = "save", name = a_name });
		var path = Path.Combine(SavesDir(a_ctx.Profile), a_name + ".ess");
		if (!WaitForSave(a_ctx, path, t0)) {
			if (Setup.Bool(a_ctx.Setup.Root, "forceSave") != true) {
				throw new StepFailedException(SaveNotMade(a_name, false));
			}
			a_ctx.Log.Warn($"the save \"{a_name}\" was not made - trying once more with the game's saving switched back on (\"Force the save\")");
			a_ctx.DevBench.Papyrus("Game", "SetInChargen", false, false, false);
			a_ctx.DevBench.Call("game", new { action = "save", name = a_name });
			if (!WaitForSave(a_ctx, path, t0)) {
				throw new StepFailedException(SaveNotMade(a_name, true));
			}
		}
		var cosave = Path.ChangeExtension(path, ".skse");
		var cw = Stopwatch.StartNew();
		while (cw.Elapsed < TimeSpan.FromSeconds(20)) {
			if (File.Exists(cosave) && File.GetLastWriteTimeUtc(cosave) >= t0) {
				var c1 = new FileInfo(cosave).Length;
				a_ctx.Pause(TimeSpan.FromSeconds(2));
				if (new FileInfo(cosave).Length == c1) {
					break;
				}
			} else {
				a_ctx.Pause(TimeSpan.FromSeconds(1));
			}
		}
		a_ctx.Log.Detail($"saved \"{a_name}\" ({new FileInfo(path).Length / 1024} KB{(File.Exists(cosave) ? ", its SKSE co-save too" : "")})");
		CloseGame(a_ctx);
	}

	public static void CloseGame(RunContext a_ctx)
	{
		using var g = Game(a_ctx);
		if (g == null) {
			return;
		}
		// through the process in hand (this run's very game, found by its number and start time): never by its number alone
		try {
			g.Kill(true);
		} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) {
			// it had ended already, or a program under it would not go: the wait below says how it stands
		}
		// Waited for by the process in hand. Listed again, a game that is still going away no longer says when it started and so
		// reads as not this run's: it was called closed while it ran on for most of a minute (a big load order lets go slowly)
		var sw = Stopwatch.StartNew();
		while (!Gone(g) && sw.Elapsed < TimeSpan.FromSeconds(30)) {
			Thread.Sleep(500);
		}
		a_ctx.Log.Detail(Gone(g) ? "the game is closed" : "the game is closing - it has not let go yet, and Mod Organizer's close waits for it");
	}

	// has this very process ended? (one Windows no longer lets us ask about is gone)
	static bool Gone(Process a_p)
	{
		try {
			return a_p.HasExited;
		} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) {
			return true;
		}
	}

	// guided: the player does the in-game part; the runner starts the game and checks a NEW save appeared and the game closed
	static string GuidedSave(RunContext a_ctx, string a_step, StepSettings a_s, string a_what)
	{
		var before = DateTime.Now;
		LaunchGame(a_ctx, a_s);
		a_ctx.Guide.Ask(a_step, a_what + " Then quit the game and press Done.", a_ctx.Cancel);
		var sw = Stopwatch.StartNew();
		while (GameUp(a_ctx)) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (sw.Elapsed > TimeSpan.FromMinutes(2)) {
				throw new StepFailedException("the game is still running - quit it, then start the step again");
			}
			a_ctx.Pause(TimeSpan.FromSeconds(1));
		}
		var save = NewestSave(a_ctx.Profile, before) ?? throw new StepFailedException("no new save was made");
		a_ctx.Log.Detail($"your save: \"{save}\"");
		return save;
	}

	static bool Auto(RunContext a_ctx) => DevBench.InProfile(a_ctx.Profile);

	// phase 1, the profile as it is (before any output goes off) - his flow, 2026-10-27: the player's newest save loaded and left a
	// full minute; DynDOLOD deactivated by its MCM's own call, 30 s; the place the player stands recorded (a persistent marker
	// left there); a teleport to QASmoke; saved there; quit. Only the generated output goes off later - the DLL, Resources and
	// every other DynDOLOD mod stay on (his word 2026-10-27).
	public static void Deactivate(RunContext a_ctx, StepSettings a_s)
	{
		CopySaves(a_ctx);
		// (an old output that is used goes off with the rest before the first tool, so what needs it is off for the clean save too)
		var outs = a_ctx.Setup.EnabledSteps.Concat(a_ctx.Setup.KeptSteps).Select(s => new StepSettings(s.Settings).OutputMod).OfType<string>().ToList();
		a_ctx.CleanSaveExtraOff = a_ctx.Profile.Dependents(outs);
		var dyn = a_s.Raw["dyndolod_off"] != null ? a_s.Bool("dyndolod_off", false) : HasDynDolod(a_ctx.Profile);
		var cell = a_s.Str("interior_cell") ?? "QASmoke";
		if (a_s.Str("save") is { Length: > 0 } namedSave) {
			SameCharacter(a_ctx, namedSave, "the save the setup names");
		}
		if (!Auto(a_ctx)) {
			var made = GuidedSave(a_ctx, "cleansave", a_s, $"In the game: {LoadWhat(a_ctx, a_s)} and wait a full minute. " +
				(dyn ? "Open DynDOLOD's menu (MCM), switch DynDOLOD off, and wait half a minute. " : "") +
				$"Note exactly where you are standing (you go back there at the end). Open the console and type: coc {cell} - once you are there and your followers have caught up, save.");
			SameCharacter(a_ctx, made, "the save you made");
			a_ctx.CleanSaveName = made;
			return;
		}
		var save = a_s.Str("save") is { Length: > 0 } named ? named : NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx), Character(a_ctx)) ?? throw new StepFailedException(
			Character(a_ctx) is { } who ? $"the profile has no save of {who} (the character this setup picked) to load" : "the profile has no save of yours to load");
		a_ctx.Log.Detail($"your newest save: \"{save}\"");
		try {
			StartAndLoad(a_ctx, "cleansave", a_s, save);
			Settle(a_ctx, "cleansave", a_s.Int("wait_s", 60), "letting your save settle for a minute");
			const string cls = "SHESON_DynDOLOD_Plugin";
			if (dyn) {
				var was = a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true;
				a_ctx.Log.Detail($"DynDOLOD is {(was ? "active" : "already inactive")}");
				if (was) {
					a_ctx.DevBench.Papyrus(cls, "SetMyActive", false);
				}
				Settle(a_ctx, "cleansave", a_s.Int("dyndolod_wait_s", 30), "DynDOLOD switched off - waiting half a minute");
				if (a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
					throw new StepFailedException("DynDOLOD did not switch off");
				}
			}
			ReadyToMove(a_ctx);
			// where the player stands, kept as a persistent marker in the save (the save to play takes them back to it)
			var marker = DevBench.FormKey(a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "PlaceAtMe", new { form = "0x0000003B" }, 1, true, false));
			if (marker != null) {
				var angle = a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "GetAngleZ")?.GetValue<double>() ?? 0;
				a_ctx.DevBench.PapyrusOn(marker, "ObjectReference", "SetAngle", 0.0, 0.0, angle);
				a_ctx.Journal.ReturnMarker = marker;
				a_ctx.Journal.Save();
				a_ctx.Log.Detail($"your place recorded (marker {marker})");
			} else {
				a_ctx.Log.Warn("your place could not be recorded - the save to play will leave you in " + cell);
			}
			MoveWithFollowers(a_ctx, () => a_ctx.DevBench.Console("coc " + cell), () => WaitPlace(a_ctx));
			a_ctx.Pause(TimeSpan.FromSeconds(a_s.Int("settle_s", 10)));
			if (dyn && a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() == true) {
				throw new StepFailedException($"DynDOLOD switched itself back on in {cell}");
			}
			a_ctx.Log.Detail(dyn ? $"DynDOLOD off and still off in {cell}" : $"in {cell}");
			SaveAndQuit(a_ctx, SaveOff);
			a_ctx.CleanSaveName = SaveOff;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}

	// phase 2, every old output off (Mod Organizer started again by the switch): that save loaded past its warnings, a full
	// minute, saved - the clean save the rebuild starts from (his flow)
	public static void CleanSaves(RunContext a_ctx, StepSettings a_s)
	{
		var from = a_ctx.CleanSaveName ?? throw new StepFailedException("no DynDOLOD-off save from this run - the clean save starts at the beginning of a run");
		foreach (var (m, why) in a_ctx.CleanSaveExtraOff) {
			a_ctx.Log.Detail($"also off for the clean save: {m} ({why})");
		}
		using var _ = new Toggle(a_ctx, [], a_ctx.CleanSaveExtraOff.Keys, "the clean save", a_ctx.Cancel);
		if (!Auto(a_ctx)) {
			a_ctx.CleanSaveName = GuidedSave(a_ctx, "cleansave", a_s, $"In the game: load the save \"{from}\" (answer its warnings about missing files with Yes / continue), wait a full minute, and save.");
			return;
		}
		try {
			StartAndLoad(a_ctx, "cleansave", a_s, from);
			Settle(a_ctx, "cleansave", a_s.Int("wait_s", 60), "letting the clean save settle for a minute");
			SaveAndQuit(a_ctx, SaveClean);
			a_ctx.CleanSaveName = SaveClean;
		} catch {
			CloseGame(a_ctx);
			throw;
		}
		a_ctx.Log.Detail($"clean save made: \"{SaveClean}\"");
	}

	// the save to play: the clean save loaded with every new output on, a full minute, the player taken back to the place
	// recorded before QASmoke, DynDOLOD on again, saved (his addition 1). A compile at this start means the shader cache was
	// out of date - said in the run's result (his question 2026-10-27, "rebuilding another tool wont cause shaders to need to
	// be rebuilt")
	// his checklist 464: before the first step that loads a save, the player's saves (each save and its SKSE co-save) are copied into
	// the run's backup folder - once a run, the setup's "copySaves" (on unless switched off). A copy that cannot be made, or that would
	// not leave the room the run needs, is said and the run goes on: it is a safety copy, never a reason to stop
	public const string SavesCopyFolder = "saves before the run";

	// the free bytes on a folder's drive (the offline tests answer for the disk)
	public static Func<string, long> FreeBytes { get; set; } = a_path => {
		var root = Path.GetPathRoot(Path.GetFullPath(a_path));
		// a network folder's room cannot be read this way: not known, so the copy goes ahead (a failure there is said like any other)
		return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? long.MaxValue / 2 : new DriveInfo(root).AvailableFreeSpace;
	};

	// a_lastStep: the run's work is behind it, so only a small margin is kept
	public static string? CopySaves(RunContext a_ctx, bool a_lastStep = false)
	{
		if (Setup.Bool(a_ctx.Setup.Root, "copySaves") == false) {
			return null;
		}
		var to = Path.Combine(a_ctx.BackupFolder, SavesCopyFolder);
		if (Directory.Exists(to)) {
			return to;
		}
		var part = to + ".part";
		try {
			var from = SavesDir(a_ctx.Profile);
			if (!Directory.Exists(from)) {
				a_ctx.Log.Warn($"your saves were not copied first: no saves folder at {from}");
				return null;
			}
			var files = new DirectoryInfo(from).EnumerateFiles().Where(f => f.Extension.Equals(".ess", StringComparison.OrdinalIgnoreCase) ||
				f.Extension.Equals(".skse", StringComparison.OrdinalIgnoreCase)).ToList();
			// a save unchanged since the last run's copy is linked to that copy, not copied again: only what changed takes room. Pruning that backup leaves this one whole
			var earlier = Runner.BackupDirsOf(a_ctx.Setup).Select(d => Path.Combine(d.FullName, SavesCopyFolder)).FirstOrDefault(d => Directory.Exists(d) && !Files.SamePath(d, to));
			FileInfo? Same(FileInfo a_f) => earlier != null && new FileInfo(Path.Combine(earlier, a_f.Name)) is { Exists: true } e && e.Length == a_f.Length && e.LastWriteTimeUtc == a_f.LastWriteTimeUtc ? e : null;
			var bytes = files.Where(f => Same(f) == null).Sum(f => f.Length);
			var mb = bytes / 1048576.0;
			Directory.CreateDirectory(a_ctx.BackupFolder);
			// the run's outputs and backups come first: the room the pre-check promised them stays free
			var margin = a_lastStep ? 1 : a_ctx.Setup.MinFreeGb;
			if (FreeBytes(a_ctx.BackupFolder) < bytes + margin * 1073741824L) {
				a_ctx.Log.Warn($"your saves were not copied first: {mb:0} MB would not leave the {margin} GB this run needs free - your saves folder is untouched by it");
				return null;
			}
			if (Directory.Exists(part)) {
				Files.DeleteDir(part);
			}
			Directory.CreateDirectory(part);
			// what the changed saves still to come will take: a link that fails is a copy on top of that
			var remaining = bytes;
			foreach (var f in files) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				var dest = Path.Combine(part, f.Name);
				if (Same(f) is not { } same) {
					f.CopyTo(dest, false);
					remaining -= f.Length;
				} else if (!Native.HardLink(dest, same.FullName)) {
					// a link that cannot be made (another disk, a file system without links) is a copy after all, and its room was
					// not counted above: never taken out of what the run's outputs were promised
					if (FreeBytes(a_ctx.BackupFolder) < f.Length + remaining + margin * 1073741824L) {
						throw new IOException($"an unchanged save could not be linked to the last run's copy, and copying it would not leave the {margin} GB this run needs free");
					}
					f.CopyTo(dest, false);
				}
			}
			// whole or not at all: a copy cut off part way is never taken for a full one
			Files.MoveDirectory(part, to);
			var saves = files.Count(f => f.Extension.Equals(".ess", StringComparison.OrdinalIgnoreCase));
			a_ctx.Log.Detail($"your saves copied first: {saves} save(s), {mb:0} MB new, into the run's backup ({SavesCopyFolder})");
			return to;
		} catch (OperationCanceledException) {
			DropPart(part);
			throw;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
			DropPart(part);
			a_ctx.Log.Warn($"your saves could not be copied first ({e.Message}) - the run goes on; your saves folder is untouched by it");
			return null;
		}
	}

	static void DropPart(string a_part)
	{
		try {
			if (Directory.Exists(a_part)) {
				Files.DeleteDir(a_part);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a part left behind goes with the run's backup, and is never taken for a copy
		}
	}

	public static void PlaySave(RunContext a_ctx, StepSettings a_s)
	{
		CopySaves(a_ctx, true);
		var player = a_ctx.CleanSaveName == null ? NewestPlayerSave(a_ctx.Profile, OurRuns(a_ctx), Character(a_ctx)) : null;
		// a named save that is gone (deleted, renamed) is passed over for the fallbacks, never loaded and failed on
		var named = a_s.Str("save") is { Length: > 0 } n && SaveTime(a_ctx.Profile, n) != null ? n : null;
		if (named == null && a_s.Str("save") is { Length: > 0 } missing) {
			a_ctx.Log.Warn($"the save the setup names (\"{missing}\") is not in the saves folder - another is used");
		}
		// with no clean save of this run, a named save or an earlier clean save must be the picked character's too
		var oldClean = SaveTime(a_ctx.Profile, SaveClean);
		if (a_ctx.CleanSaveName == null) {
			if (named != null) {
				SameCharacter(a_ctx, named, "the save the setup names");
			}
			if (oldClean != null && OldCleanSave(a_ctx.Profile, Character(a_ctx)) == null) {
				a_ctx.Log.Detail($"an earlier run's clean save belongs to another character than {Character(a_ctx)} - not used");
				oldClean = null;
			}
		}
		var (pick, why) = PlaySaveFrom(a_ctx.CleanSaveName, named, oldClean, player, SaveTime(a_ctx.Profile, player));
		var from = pick ?? throw new StepFailedException(why);
		a_ctx.Log.Detail($"the save to play starts from \"{from}\" ({why})");
		var marker = a_ctx.Journal.ReturnMarker;
		if (marker == null && a_ctx.CleanSaveName == null && from == SaveClean && MarkerOfCleanSave(a_ctx) is { } kept) {
			marker = kept;
			a_ctx.Journal.ReturnMarker = kept;
			a_ctx.Journal.Save();
			a_ctx.Log.Detail("the place you stood at before the clean save is taken from the run that made that save");
		}
		// a Stop ends this wait too (a download can hold Mod Organizer open for half an hour)
		a_ctx.Control.Close(null, a_ctx.Cancel);
		if (!Auto(a_ctx)) {
			GuidedSave(a_ctx, "playsave", a_s, $"In the game: load \"{from}\" and wait a full minute. Go back to the place you noted at the start" +
				" (the console: coc <that place>) and let your followers catch up, check DynDOLOD is on in its menu (MCM), and save.");
			return;
		}
		try {
			var compiled = StartAndLoad(a_ctx, "playsave", a_s, from);
			if (compiled) {
				a_ctx.Log.Warn("the game compiled shaders at this start - the shader cache was out of date (a shader mod, its settings or the game changed)");
				a_ctx.Journal.Notes.Add("the shader cache was out of date at the save to play - the next run rebuilds it");
			}
			Settle(a_ctx, "playsave", a_s.Int("wait_s", 60), "letting the save settle for a minute");
			if (marker != null) {
				ReadyToMove(a_ctx);
				MoveWithFollowers(a_ctx, () => a_ctx.DevBench.PapyrusOn(Player, "ObjectReference", "MoveTo", new { form = marker }, 0.0, 0.0, 0.0, true),
					() => WaitPlace(a_ctx));
				Settle(a_ctx, "playsave", a_s.Int("settle_s", 10), "back at your place");
				try {
					a_ctx.DevBench.PapyrusOn(marker, "ObjectReference", "Delete");
				} catch (Exception e) when (Helps(a_ctx, e)) {
					a_ctx.Log.Warn("the place marker was not removed: " + e.Message);
				}
				a_ctx.Log.Detail("you are back where you stood before the clean save");
			} else {
				a_ctx.Log.Warn("no place was recorded in this run (the clean save was made by an earlier run) - the player stays where the clean save left them");
				a_ctx.Journal.Notes.Add("the save to play was made where the clean save ended - no place from before it was recorded in this run");
			}
			if (DynOff(a_ctx)) {
				const string cls = "SHESON_DynDOLOD_Plugin";
				try {
					if (a_ctx.DevBench.Papyrus(cls, "GetMyActive")?.GetValue<bool>() != true) {
						a_ctx.DevBench.Papyrus(cls, "SetMyActive", true);
						Settle(a_ctx, "playsave", a_s.Int("dyndolod_wait_s", 30), "DynDOLOD switched on again");
					}
					a_ctx.Log.Detail("DynDOLOD is on");
				} catch (Exception e) when (Helps(a_ctx, e)) {
					a_ctx.Log.Warn("DynDOLOD's state could not be read (" + e.Message + ") - check its menu in game");
				}
			}
			SaveAndQuit(a_ctx, SaveReady);
		} catch {
			CloseGame(a_ctx);
			throw;
		}
	}
}
