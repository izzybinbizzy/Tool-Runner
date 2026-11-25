namespace ToolRunner.App;

// The command line (for players who script):
//   "Tool Runner.exe" run --setup <file> [--only <step> | --from <step> | --until <step>]   a run, no window
//   "Tool Runner.exe" retry --setup <file>                                                  its last run again, from the step that did not finish
//   "Tool Runner.exe" queue --setup <file> --setup <file> ... [--keep-going]               several setups, one after another
//   "Tool Runner.exe" check --setup <file>                                                  the pre-check, printed
//   "Tool Runner.exe" preview --setup <file> [--only | --from | --until <step>]              what a run would do (nothing changed)
//   "Tool Runner.exe" dryrun --setup <file> [--only | --from | --until <step>]               everything a start would meet and then do: nothing started, nothing written
//   "Tool Runner.exe" status [--setup <file>]                                               what is going right now
//   "Tool Runner.exe" catalog                                                               every refusal and failure of its own: its code, what it means, what to do
//   "Tool Runner.exe" lod --setup <file> --step <step>                                      the mods a LOD step builds from, with their pages
//   "Tool Runner.exe" restore [--setup <file>]                                              put back an interrupted run
//   "Tool Runner.exe" undo --setup <file>                                                   the setup's last run undone: its outputs and lists as before it
//   "Tool Runner.exe" import --file <setup file> [--name <setup name>]                      a setup from elsewhere brought into the Setups folder
//   "Tool Runner.exe" stop | stop-after                                                     ask the running run to stop
//   "Tool Runner.exe" updates --setup <file>                                                its tools' newest versions (asks their pages)
//   "Tool Runner.exe" changes --setup <file> [--look]                                       what Mod Organizer changed, newest first
//   "Tool Runner.exe" tools --mo2 <ModOrganizer.exe> [--instance <folder>] [--add]          which tools it finds, where to get the rest; --add puts the found ones in Mod Organizer's list
//   "Tool Runner.exe" test-tools --setup <file>                                             each tool opened to the window a run presses; nothing pressed
//   "Tool Runner.exe" addon-check <file>                                                    what is wrong with an add-on file, in plain words
//   "Tool Runner.exe" bugreport [--setup <file>]                                            one zip for a bug report (the setup's last run), names masked
//   "Tool Runner.exe" share --setup <file> [--out <folder>]                                 a copy of the setup to share: its paths and commands taken out
// --json after run, retry, check, preview, dryrun, status, catalog, updates, changes or tools prints one JSON object and nothing else (Reports.cs)
// exit 0 = finished / clean / would start / idle, 1 = failed / refused / a run was cut off, 2 = stopped, 3 = bad arguments
//      (an unknown command, a missing option, one the command does not have or one without what it names, a file that is not
//      there or is no setup), 4 = (updates) a tool has a newer version,
//      5 = (status) a run is going
public static class Cli
{
	// What the window itself is started with (App.OnStartup): nothing, the off-screen pictures, a start from Mod Organizer's
	// Tools menu. Anything else typed is a command - a known one is carried out, an unknown one is said to be none. It never
	// opens the window: a script with a misspelt command would sit behind a window nobody asked for, waiting on nothing
	public static bool Handles(string[] a_args) => a_args.Length > 0 && a_args[0] is not ("--snapshot" or "--page" or "--for-profile");

	// one JSON object on the output and nothing else: no line of words before or after it
	static bool Json(string[] a_args) => a_args.Contains("--json");

	static int Print(System.Text.Json.Nodes.JsonObject a_o, int a_exit)
	{
		Console.WriteLine(Reports.Text(a_o));
		return a_exit;
	}

	static string? Opt(string[] a_args, string a_name)
	{
		var i = Array.IndexOf(a_args, a_name);
		return i >= 0 && i + 1 < a_args.Length ? a_args[i + 1] : null;
	}

	// What each command takes: the options that name something (what they name follows), its switches, and how many words
	// of its own it takes with no option before them
	static readonly Dictionary<string, (string[] Named, string[] Switches, int Words)> Takes = new(StringComparer.Ordinal) {
		["run"] = (["--setup", "--only", "--from", "--until"], ["--json"], 0),
		["retry"] = (["--setup"], ["--json"], 0),
		["queue"] = (["--setup"], ["--keep-going"], 0),
		["check"] = (["--setup"], ["--json"], 0),
		["preview"] = (["--setup", "--only", "--from", "--until"], ["--json"], 0),
		["dryrun"] = (["--setup", "--only", "--from", "--until"], ["--json"], 0),
		["status"] = (["--setup"], ["--json"], 0),
		["catalog"] = ([], ["--json"], 0),
		["lod"] = (["--setup", "--step"], [], 0),
		["restore"] = (["--setup"], [], 0),
		["undo"] = (["--setup"], [], 0),
		["stop"] = ([], [], 0),
		["stop-after"] = ([], [], 0),
		["import"] = (["--file", "--name"], [], 0),
		["updates"] = (["--setup"], ["--json"], 0),
		["changes"] = (["--setup"], ["--look", "--json"], 0),
		["bugreport"] = (["--setup"], [], 0),
		["tools"] = (["--mo2", "--instance"], ["--add", "--json"], 0),
		["addon-check"] = ([], [], 1),
		["test-tools"] = (["--setup"], [], 0),
		["share"] = (["--setup", "--out"], [], 0),
	};

	// What was typed that its command does not take, in words (null: all of it is the command's, or the word is no command).
	// It was passed over without a word: "run --setup x --form texgen" (a slip for --from) and "run --setup x --only" (the
	// step forgotten) both ran every step of the setup
	public static string? NotTaken(string[] a_args)
	{
		if (a_args.Length == 0 || !Takes.TryGetValue(a_args[0], out var takes)) {
			return null;
		}
		var given = new HashSet<string>(StringComparer.Ordinal);
		var words = 0;
		for (var i = 1; i < a_args.Length; ++i) {
			var a = a_args[i];
			if (takes.Switches.Contains(a)) {
				continue;
			}
			if (takes.Named.Contains(a)) {
				if (i + 1 >= a_args.Length || a_args[i + 1].StartsWith("--", StringComparison.Ordinal)) {
					return $"{a} needs what it names right after it";
				}
				// (a queue names a setup once per setup)
				if (!given.Add(a) && !(a_args[0] == "queue" && a == "--setup")) {
					return $"{a} is given twice";
				}
				++i;
			} else if (a.StartsWith("--", StringComparison.Ordinal)) {
				return $"{a} is no option of {a_args[0]} " + (takes.Named.Length + takes.Switches.Length == 0 ? "(it takes none)" : $"(it takes: {string.Join(", ", takes.Named.Concat(takes.Switches))})");
			} else if (++words > takes.Words) {
				return $"\"{a}\" is not expected there - what an option names goes right after the option, and a name or a path with a blank in it goes in quotes";
			}
		}
		return null;
	}

	public static int Execute(string[] a_args)
	{
		ConsoleAttach.ToParent();
		// before anything is looked at or started
		if (NotTaken(a_args) is { } slip) {
			Said(a_args, slip);
			return 3;
		}
		// runs at a set time are gone: a task an older version left is cleared on the command line too (a player who never
		// opens the window), before the command itself
		// (never before a stop: Windows' task list can take half a minute, and a run is waiting on it)
		try {
			if (a_args[0] is not ("stop" or "stop-after" or "status" or "catalog")) {
				foreach (var left in Schedule.RemoveLeftoversOnce(AppPaths.Data)) {
					if (!Json(a_args)) {
						Console.WriteLine("an old timed run's task could not be removed - delete it in Windows' Task Scheduler: " + left);
					}
				}
			}
		} catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) {
		}
		try {
			return a_args[0] switch {
				"run" => Run(a_args),
				"retry" => RetryCmd(a_args),
				"queue" => Queue(a_args),
				"check" => Check(a_args),
				"preview" => Preview(a_args),
				"dryrun" => DryRunCmd(a_args),
				"status" => StatusCmd(a_args),
				"catalog" => CatalogCmd(a_args),
				"lod" => Lod(a_args),
				"restore" => RestoreCmd(a_args),
				"undo" => UndoCmd(a_args),
				"stop" or "stop-after" => Stop(a_args[0]),
				"import" => Import(a_args),
				"updates" => Updates(a_args),
				"changes" => Changes(a_args),
				"bugreport" => BugReportCmd(a_args),
				"tools" => ToolsCmd(a_args),
				"addon-check" => AddOnCheckCmd(a_args),
				"test-tools" => TestToolsCmd(a_args),
				"share" => ShareCmd(a_args),
				"--help" or "-h" or "/?" => Help(),
				_ => Unknown(a_args),
			};
		} catch (Exception e) when (e is ArgumentException or FileNotFoundException or DirectoryNotFoundException or InvalidDataException or System.Text.Json.JsonException) {
			// what was typed is wrong: an option left out, a file that is not there or is no setup
			Said(a_args, e.Message);
			return 3;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// the disk or Windows refused: its plain reason, and no crash note for what is no fault of the program
			Said(a_args, e.Message);
			return 1;
		} catch (Exception e) {
			// never Windows' crash box (a window on the player's screen): said, logged, exit 1
			Said(a_args, "something went wrong - " + e.GetType().Name + ": " + e.Message);
			App.LogCrash(e);
			return 1;
		}
	}

	// a command that could not be carried out: in words, or as the one JSON object a script was promised
	static void Said(string[] a_args, string a_error)
	{
		if (Json(a_args)) {
			Console.WriteLine(Reports.Text(new System.Text.Json.Nodes.JsonObject { ["schema"] = Reports.Schema, ["program"] = Brand.Name, ["command"] = a_args[0], ["error"] = a_error }));
		} else {
			Console.WriteLine("Tool Runner: " + a_error);
		}
	}

	static int Unknown(string[] a_args)
	{
		Said(a_args, $"\"{a_args[0]}\" is no command");
		if (!Json(a_args)) {
			Help();
		}
		return 3;
	}

	static int Help()
	{
		Console.WriteLine("Tool Runner - run | retry | queue | check | preview | dryrun | status | catalog | lod | restore | undo | stop | stop-after | updates | changes | import | bugreport | share | tools | test-tools | addon-check");
		Console.WriteLine("  --json after run, retry, check, preview, dryrun, status, catalog, updates, changes or tools prints one JSON object (see the readme)");
		return 0;
	}

	// a copy of the setup another player can import - paths of this PC and its commands taken out
	static int ShareCmd(string[] a_args)
	{
		var file = Opt(a_args, "--setup") ?? throw new ArgumentException("--setup <file> is needed");
		var setup = Setup.Load(Path.GetFullPath(file));
		var (outFile, removed) = SetupShare.Write(setup, Opt(a_args, "--out") is { } o ? Path.GetFullPath(o) : Path.Combine(AppPaths.Data, "Shared setups"), MainWindow.AppVersion);
		Console.WriteLine($"shared copy: {outFile}");
		foreach (var r in removed) {
			Console.WriteLine("  taken out: " + r);
		}
		return 0;
	}

	// the newest run of the setup (or of any setup), zipped with its logs for a bug report; prints where the zip is
	static int BugReportCmd(string[] a_args)
	{
		var setup = Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : null;
		var run = Journal.All(AppPaths.Runs).FirstOrDefault(j => setup == null || string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase));
		var zip = BugReport.Make(AppPaths.Data, run, Path.Combine(AppPaths.Data, "Bug reports"), MainWindow.AppVersion);
		Console.WriteLine($"bug report: {zip}");
		Console.WriteLine($"attach it to an issue: {BugReport.Issues}");
		return 0;
	}

	static string SetupArg(string[] a_args) =>
		Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : throw new ArgumentException("--setup <setup file> is needed");

	// every value of an option given more than once (queue --setup a --setup b)
	static List<string> Opts(string[] a_args, string a_name)
	{
		var outList = new List<string>();
		for (int i = 0; i + 1 < a_args.Length; ++i) {
			if (a_args[i] == a_name) {
				outList.Add(a_args[++i]);
			}
		}
		return outList;
	}

	static int Run(string[] a_args)
	{
		var setup = SetupArg(a_args);
		return RunOne(new RunRequest(setup, AppPaths.Data, Opt(a_args, "--only"), Opt(a_args, "--from"), Opt(a_args, "--until")), Json(a_args));
	}

	static int RetryCmd(string[] a_args)
	{
		var setup = SetupArg(a_args);
		var last = Journal.All(AppPaths.Runs).FirstOrDefault(j => string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase));
		if (last == null) {
			Said(a_args, "this setup has no run to start again");
			return 1;
		}
		if (Retry.Request(last, AppPaths.Data) is not { } req) {
			Said(a_args, last.Unfinished ? "its last run did not finish - put the profile back first (restore)" : "its last run finished - nothing to run again");
			return 1;
		}
		if (!Json(a_args)) {
			Console.WriteLine($"starting again from {StepCatalog.Name(req.From!)}");
		}
		return RunOne(req, Json(a_args));
	}

	static int Queue(string[] a_args)
	{
		var setups = Opts(a_args, "--setup").Select(Path.GetFullPath).ToList();
		if (setups.Count == 0) {
			throw new ArgumentException("--setup <setup file> is needed, once per setup, in the order they run");
		}
		// a setup refused before anything changed: the window reads the reason as for a single run, at once
		var items = QueueRun.Run(setups, AppPaths.Data, a_args.Contains("--keep-going"), null, i => {
			if (i.Outcome is { Folder: null } refused) {
				WriteRefusal(Path.GetFileNameWithoutExtension(i.SetupFile) + ": " + refused.Verdict);
			}
		});
		foreach (var l in QueueRun.Summary(items)) {
			Console.WriteLine(l);
		}
		// the first that did not finish names the queue's outcome
		var notFinished = items.Select(i => i.Outcome).FirstOrDefault(o => o != null && o.State != RunState.Finished);
		return notFinished == null ? 0 : notFinished.State == RunState.Stopped ? 2 : 1;
	}

	// the run's record as it stands on disk at its end (null: a refused start made none, or it no longer reads)
	static Journal? Ended(RunOutcome a_outcome)
	{
		try {
			return a_outcome.Folder == null ? null : Journal.Load(a_outcome.Folder);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) {
			return null;
		}
	}

	static int RunOne(RunRequest a_req, bool a_json = false)
	{
		// the run's log on the command line while it goes; a script that asked for JSON gets its one object and nothing else
		var outcome = Runner.Run(a_json ? a_req : a_req with { Echo = Console.WriteLine });
		var ended = Ended(outcome);
		if (a_json) {
			Console.WriteLine(Reports.Text(Reports.Outcome(outcome, ended)));
		} else if (ended != null) {
			// the verdict, then what the run did in a few lines
			Console.WriteLine(outcome.Verdict);
			foreach (var line in RunSummary.Of(ended).Lines()) {
				Console.WriteLine(line);
			}
		} else {
			foreach (var line in Explain.Lines("", outcome.Verdict)) {
				Console.WriteLine(line);
			}
		}
		if (outcome.Folder == null) {
			WriteRefusal(outcome.Verdict);
		}
		return outcome.State switch {
			RunState.Finished => 0,
			RunState.Stopped => 2,
			_ => 1,
		};
	}

	// refused before anything changed: the window reads the reason here
	static void WriteRefusal(string a_why)
	{
		try {
			Directory.CreateDirectory(AppPaths.Runs);
			Files.WriteAtomic(Engine.RefusalFile, a_why);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// (the reason was printed already; a window started later reads it as "no reason given")
		}
	}

	static int Check(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var issues = Preflight.Run(setup, mo2, setup.EnabledSteps.Select(s => s.Key).ToList());
		var ready = !issues.Any(i => i.Blocking);
		if (Json(a_args)) {
			return Print(Reports.Check(setup, issues), ready ? 0 : 1);
		}
		foreach (var i in issues) {
			Console.WriteLine($"{(i.Blocking ? "BLOCKS" : "note  ")}  {(i.Step == "" ? "" : StepCatalog.Name(i.Step) + ": ")}{i.Text}");
			// what stops a run comes with what to do about it
			if (i.Blocking && Explain.For(i.Step, i.Text) is { } why) {
				Console.WriteLine($"        what to do: {why.Todo}{(why.Code == null ? "" : $" [{why.Code}]")}");
			}
		}
		Console.WriteLine(ready ? "READY" : "NOT READY");
		return ready ? 0 : 1;
	}

	// everything a start would meet and then do, and nothing started: exit 0 = it would start, 1 = it would be refused
	static int DryRunCmd(string[] a_args)
	{
		var file = SetupArg(a_args);
		var report = DryRun.Make(new RunRequest(file, AppPaths.Data, Opt(a_args, "--only"), Opt(a_args, "--from"), Opt(a_args, "--until")));
		var usual = Engine.UsualTimes(file);
		TimeSpan? Usual(string a_key) => usual.TryGetValue(a_key, out var t) ? t : null;
		if (Json(a_args)) {
			return Print(Reports.DryRun(report, Usual), report.Starts ? 0 : 1);
		}
		Console.WriteLine("DRY RUN - nothing is started and nothing is changed.");
		foreach (var l in report.Lines(Usual)) {
			Console.WriteLine(l);
		}
		return report.Starts ? 0 : 1;
	}

	// every refusal and failure the program knows as its own, by its code (the guide's list is made from this)
	static int CatalogCmd(string[] a_args)
	{
		if (Json(a_args)) {
			return Print(Reports.Catalog(), 0);
		}
		foreach (var e in RunnerErrors.All.OrderBy(e => e.Code, StringComparer.Ordinal)) {
			Console.WriteLine($"{e.Code}  {e.What}");
			Console.WriteLine($"        {e.Todo}");
		}
		return 0;
	}

	// what is going right now: exit 0 = nothing, 5 = a run is going, 1 = a run was cut off and its profile is not back
	static int StatusCmd(string[] a_args)
	{
		var s = RunStatus.Take(AppPaths.Data, Opt(a_args, "--setup") is { } f ? Path.GetFullPath(f) : null);
		var exit = s.Run != null ? 5 : s.Interrupted != null ? 1 : 0;
		if (Json(a_args)) {
			return Print(Reports.Status(s, MainWindow.AppVersion), exit);
		}
		foreach (var l in RunStatus.Lines(s)) {
			Console.WriteLine(l);
		}
		return exit;
	}

	static int Preview(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var usual = Engine.UsualTimes(setup.File);
		var p = RunPreview.Build(setup, mo2, Runner.Plan(setup, Opt(a_args, "--only"), Opt(a_args, "--from"), Opt(a_args, "--until")), DryRun.LastOf(setup, mo2, AppPaths.Data));
		if (Json(a_args)) {
			return Print(Reports.Preview(setup, p, k => usual.TryGetValue(k, out var t) ? t : null), 0);
		}
		foreach (var l in p.Lines(k => usual.TryGetValue(k, out var t) ? t : null)) {
			Console.WriteLine(l);
		}
		return 0;
	}

	static int Lod(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var step = Opt(a_args, "--step") ?? throw new ArgumentException("--step <step> is needed (grass, texgen, dyndolod, treelodgen, xlodgen, anvillod)");
		if (!LodGuide.Applies(step)) {
			throw new ArgumentException($"\"{step}\" is not a LOD step");
		}
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		if (LodGuide.SetupNote(setup, mo2, step) is { } note) {
			Console.WriteLine("note    " + note);
		}
		foreach (var (title, url) in LodGuide.DocsFor(setup, mo2, step)) {
			Console.WriteLine($"help    {title}: {url}");
		}
		foreach (var h in LodGuide.Find(setup, mo2, step)) {
			Console.WriteLine($"mod     {h.Mod} ({h.What}){(h.Page == null ? "" : "  " + h.Page)}");
		}
		return 0;
	}

	static int Updates(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var res = Task.Run(() => ToolUpdates.Check(setup, mo2, CancellationToken.None)).GetAwaiter().GetResult();
		if (Json(a_args)) {
			return Print(Reports.Updates(setup, res), res.Any(r => r.Newer) ? 4 : 0);
		}
		foreach (var r in res) {
			Console.WriteLine($"{(r.Newer ? "NEWER " : "      ")}  {r.Tool}: yours {r.Yours}, its page {r.Newest}{(r.Problem == null ? "" : " - " + r.Problem)}  {r.Page}");
		}
		return res.Any(r => r.Newer) ? 4 : 0;
	}

	// --look notes what changed since the last look first (refused while a run goes: its changes would read as made outside it)
	static int Changes(string[] a_args)
	{
		var setup = Setup.Load(SetupArg(a_args));
		var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
		var dir = Mo2Changes.Dir(AppPaths.Data, mo2);
		int? looked = null;
		if (a_args.Contains("--look")) {
			if (Mo2Changes.RecordOutside(mo2, AppPaths.Data) is not { } n) {
				Said(a_args, "not looked: a run is going or did not finish - its changes would read as made outside the runner");
				return 1;
			}
			looked = n;
			if (!Json(a_args)) {
				Console.WriteLine($"{n} new change(s)");
			}
		}
		var since = Mo2Changes.WatchedSince(dir);
		if (Json(a_args)) {
			return Print(Reports.Changes(setup, since, looked, Enumerable.Reverse(Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods))), 0);
		}
		Console.WriteLine(since == null ? "not watched yet - a run or a look starts it" : $"watched since {since.Value.ToString("yyyy-MM-dd HH:mm", InvariantCulture)}");
		foreach (var r in Enumerable.Reverse(Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods))) {
			Console.WriteLine($"{r.When.ToString("yyyy-MM-dd HH:mm", InvariantCulture)}  {r.Mod}: {r.What}  [{(r.Profiles.Count == 0 ? "Mod Organizer" : string.Join(", ", r.Profiles))}]  {r.Who}");
		}
		return 0;
	}

	// test-tools --setup <file>, exit 0 = what a run presses is there in every tool
	static int TestToolsCmd(string[] a_args)
	{
		var results = ToolProof.TestAll(Setup.Load(SetupArg(a_args)), RunLog.Null, CancellationToken.None);
		foreach (var r in results) {
			Console.WriteLine((r.Good ? "good  " : "LOOK  ") + r.Text);
		}
		return results.All(r => r.Good) ? 0 : 1;
	}

	// for modders: addon-check <file>, exit 0 = it is right
	static int AddOnCheckCmd(string[] a_args)
	{
		if (a_args.Length < 2) {
			Console.WriteLine("addon-check needs the add-on file");
			return 3;
		}
		var file = Path.GetFullPath(a_args[1]);
		// the mod's folder is the one above the add-on folder, when it sits in one
		var parent = Path.GetDirectoryName(file);
		var mod = AddOns.IsFolderInMod(Path.GetFileName(parent)) ? Path.GetDirectoryName(parent) : null;
		var problems = AddOns.Problems(Files.ReadSharedText(file), mod);
		Console.WriteLine(problems.Count == 0 ? "It is right." : string.Join(Environment.NewLine, problems));
		return problems.Count == 0 ? 0 : 1;
	}

	// the first-start helper: tools --mo2 <ModOrganizer.exe> [--instance <folder>] [--add]
	static int ToolsCmd(string[] a_args)
	{
		if (Opt(a_args, "--mo2") is not { } exe) {
			Said(a_args, "tools needs --mo2 <ModOrganizer.exe>");
			return 3;
		}
		var mo2 = Mo2Instance.Load(exe, Opt(a_args, "--instance"));
		var rows = ToolFinder.Find(mo2);
		if (Json(a_args)) {
			return Print(Reports.Tools(rows, a_args.Contains("--add") ? ToolFinder.AddFound(mo2, rows) : null), 0);
		}
		foreach (var r in rows) {
			Console.WriteLine(r.State switch {
				ToolFinder.Found.Listed => $"{r.Tool.Name}: in the list",
				ToolFinder.Found.NotListed => $"{r.Tool.Name}: found, not in the list - {r.Binary}",
				_ => $"{r.Tool.Name}: not found - {r.Tool.Where}: {r.Tool.Page}",
			});
		}
		if (a_args.Contains("--add")) {
			var added = ToolFinder.AddFound(mo2, rows);
			Console.WriteLine(added.Count == 0 ? "nothing to add" : "added to Mod Organizer's list: " + string.Join(", ", added));
		}
		return 0;
	}

	// the setup's last run that left anything undone: undo --setup <file>
	static int UndoCmd(string[] a_args)
	{
		var setup = SetupArg(a_args);
		var j = Journal.All(AppPaths.Runs).Where(j => !j.Undone && (j.Unfinished || RunUndo.LeftSomething(j)) && string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase)).OrderByDescending(j => j.Started).FirstOrDefault();
		if (j == null) {
			Console.WriteLine("nothing to undo: this setup has no run that changed anything");
			return 0;
		}
		var r = Runner.Undo(j);
		Console.WriteLine(r.Verdict);
		return r.State == RunState.Restored ? 0 : 1;
	}

	static int RestoreCmd(string[] a_args)
	{
		var setup = Opt(a_args, "--setup") is { } s ? Path.GetFullPath(s) : null;
		var j = Journal.All(AppPaths.Runs).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive && (setup == null || string.Equals(j.SetupFile, setup, StringComparison.OrdinalIgnoreCase)));
		if (j == null) {
			Console.WriteLine("nothing to put back: no run was interrupted");
			return 0;
		}
		var r = Runner.Restore(j);
		Console.WriteLine(r.Verdict);
		return r.State == RunState.Restored ? 0 : 1;
	}

	// The name a setup from elsewhere is kept under. It becomes a file name in the Setups folder and the name of its run
	// folders: no folder parts, no characters Windows refuses, no dot or space at its end (Windows drops those from a folder's
	// name). What is left passes the test a setup's name must pass before a run; null when nothing plain is left
	public static string? ImportName(string a_given)
	{
		var name = string.Concat(Path.GetFileName(a_given).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim().TrimEnd('.', ' ');
		return Setup.PlainName(name) ? name : null;
	}

	// a setup file from elsewhere brought into the Setups folder: import --file <setup json> [--name <setup name>]
	static int Import(string[] a_args)
	{
		var file = Opt(a_args, "--file") ?? throw new ArgumentException("--file <setup file> is needed");
		var s = Setup.Load(Path.GetFullPath(file));
		if (s.Root["steps"] is not System.Text.Json.Nodes.JsonArray || s.Root["mo2"] is not System.Text.Json.Nodes.JsonObject) {
			Console.WriteLine("not a setup file (no steps list, no Mod Organizer): " + file);
			return 1;
		}
		var name = ImportName(Opt(a_args, "--name") ?? s.Name) ?? throw new ArgumentException("the setup needs a plain name - give it one with --name <setup name>");
		s.Root["name"] = name;
		var dst = Path.Combine(AppPaths.Setups, name + ".json");
		if (File.Exists(dst)) {
			Console.WriteLine("a setup with that name exists: " + dst);
			return 1;
		}
		// a file from elsewhere: what it gives as a mod's name must be one, and it brings no command of its own to run here
		var wrong = s.ModNames().Where(m => !Setup.PlainName(m.Name)).Select(m => $"{m.Where}: {m.Name}").Distinct().ToList();
		if (wrong.Count > 0) {
			Console.WriteLine("not brought in - these are not mod names (a plain folder name each): " + string.Join("; ", wrong.Take(8)));
			return 1;
		}
		var commands = s.TakeOutCommands();
		var programs = s.TakeOutWhatStartsPrograms();
		programs.AddRange(SetupShare.TakeOutPaths(s.Root));
		Directory.CreateDirectory(AppPaths.Setups);
		s.Save(dst);
		Console.WriteLine("setup written: " + dst);
		if (commands > 0) {
			Console.WriteLine($"its {commands} own command(s) to run before or after a run were taken out - a setup from elsewhere runs no command on this PC; add your own in Settings");
		}
		if (programs.Count > 0) {
			// a setup from elsewhere starts no program the player did not point it at, and allows no add-on for them
			Console.WriteLine("taken out - set your own in Settings: " + string.Join("; ", programs));
		}
		return 0;
	}

	static int Stop(string a_what)
	{
		var j = Engine.Active();
		// between two setups of a queue no run is going for a moment: the queue's next run is waited for, so the stop is not lost
		for (var waited = System.Diagnostics.Stopwatch.StartNew(); j == null && QueueRun.Read(AppPaths.Data) != null && waited.Elapsed < TimeSpan.FromSeconds(15);) {
			Thread.Sleep(250);
			j = Engine.Active();
		}
		if (j == null) {
			if (QueueRun.Read(AppPaths.Data) != null) {
				Console.WriteLine("a queue of setups is going, and its next run has not started yet - nothing was asked to stop; try again in a moment");
				return 1;
			}
			Console.WriteLine("no run is going");
			return 0;
		}
		Commands.Send(j.Folder, a_what == "stop" ? "stop" : "stop after");
		Console.WriteLine(a_what == "stop" ? "asked to stop now (safely)" : "asked to stop after the running step");
		return 0;
	}
}
