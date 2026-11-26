using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner.App;

public sealed partial class StepRow : Observable
{
	public required string Key { get; init; }
	public required string Name { get; init; }
	public required string About { get; init; }
	public bool On { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } }
	public StepState? State { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(Glyph)); OnPropertyChanged(nameof(Status)); } } }
	public string Usual { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } } = "";
	public string Elapsed { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } } = "";
	public bool IsCurrent { get; set => Set(ref field, value); }
	public string Note { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } } = "";
	public int Number { get; set => Set(ref field, value); }
	// ticked, but left out of every run: the setup is set to a new game and this is a save step
	public bool LeftOut { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } }
	// ticked and not run: the output its tool made before is used (the setup's "useOld", for a step that makes one)
	public bool UsesOld { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } }
	// what the step is missing to run on its own ("" = nothing): DevBench for the steps in the game
	public string Needs { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Status)); } } = "";
	// its last run in one line: when, how it ended, and whether its output is still current ("" = it never ran)
	public string LastRun { get; set => Set(ref field, value); } = "";
	public string LastKind { get; set => Set(ref field, value); } = "";
	public bool Runs => On && !LeftOut && !UsesOld;
	// while a step is dragged over this row: the line that shows where it would land (above or below it)
	public bool DropAbove { get; set => Set(ref field, value); }
	public bool DropBelow { get; set => Set(ref field, value); }

	// the row in words for a screen reader (the glyph and its color say it to the eye)
	public string Status => A11y.StepStatus(On, LeftOut, State, Elapsed, Usual, Note, UsesOld, Needs);

	public string Glyph => State switch {
		StepState.Running => Theme.Glyph("Running"),
		StepState.Done => Theme.Glyph("Done"),
		StepState.Failed => Theme.Glyph("Failed"),
		StepState.Stopped => Theme.Glyph("Stopped"),
		StepState.Skipped => Theme.Glyph("Skipped"),
		_ => "",
	};

	// a new theme: the glyph and the state's color are read again
	public void Repaint()
	{
		OnPropertyChanged(nameof(State));
		OnPropertyChanged(nameof(Glyph));
		OnPropertyChanged(nameof(LastKind));
	}
}

// a list row is read out by its words, never by its type's name or a file path (screen readers use the item's text)
public sealed record SetupItem(string Name, string File)
{
	public override string ToString() => Name;
}

public sealed record HistoryRow(string When, string Setup, string Took, string Verdict, bool Ok, string Folder)
{
	public override string ToString() => $"{When}, {Setup}, {(Took == "-" ? "" : Took + ", ")}{Verdict}";
}

// Work the window carries itself (a put-back, an undo, a tool opened to learn its settings, old backups being removed). A run is
// the engine's and is stopped cleanly when the window closes; this would be cut off half way. While any is in hand the window
// says so and stays, and closes by itself afterwards when closing was asked for.
public sealed class WorkInHand
{
	readonly List<string> _now = [];

	// what is in hand, the newest first to be named ("" = nothing)
	public string Now => _now.Count == 0 ? "" : _now[^1];
	public event Action? Changed;
	// the last of it has ended
	public event Action? Freed;

	// in hand until what this returns is let go (once: a second letting-go changes nothing)
	public IDisposable Begin(string a_what)
	{
		_now.Add(a_what);
		Changed?.Invoke();
		return new Hand(() => {
			_now.Remove(a_what);
			Changed?.Invoke();
			if (_now.Count == 0) {
				Freed?.Invoke();
			}
		});
	}

	sealed class Hand(Action a_done) : IDisposable
	{
		bool _over;

		public void Dispose()
		{
			if (!_over) {
				_over = true;
				a_done();
			}
		}
	}
}

// The dashboard's state. The window reads the run's record off disk once a second; nothing here runs a tool.
public sealed partial class MainViewModel : Observable
{
	public ObservableCollection<SetupItem> Setups { get; } = [];
	public ObservableCollection<StepRow> Steps { get; } = [];
	public ObservableCollection<HistoryRow> History { get; } = [];
	public ObservableCollection<string> Issues { get; } = [];

	public SetupItem? SelectedSetup { get; set { if (Set(ref field, value)) LoadSteps(); } }
	public StepRow? SelectedStep { get; set { if (Set(ref field, value)) { ShowLastCard(); OnPropertyChanged(nameof(UseOldLabel)); } } }

	// the button under the steps and the right-click entry: what pressing it does for the selected step
	public string UseOldLabel => WindowWords.Word(SelectedStep?.UsesOld == true ? "Build it a_gain" : "Use o_ld output");
	public bool Mo2Open { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(Mo2Text)); } }
	public bool GameRunning { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(GameText)); } }
	public string Mo2Text => Mo2Open ? "Mod Organizer open" : "Mod Organizer closed";
	public string GameText => GameRunning ? "Skyrim running" : "Skyrim not running";
	public bool IsRunning { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(ShowProgress)); OnPropertyChanged(nameof(ProgressBusy)); OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(LastCardText)); OnPropertyChanged(nameof(WindowTitle)); } } }
	// START needs a setup that reads and no run going
	public bool CanStart => !IsRunning && Setup != null;
	// no moving bar while the run waits for the player: nothing is working then
	public bool ShowProgress => IsRunning && Waiting == "";
	// the bar that moves by itself ("working, its share not known") only while it shows: a hidden one still animates, and that
	// kept an idle window drawing sixty times a second. Not while the window is minimized or in the tray (nobody sees it), and
	// not at all when Windows' own "show animations" switch is off
	public bool ProgressBusy => ShowProgress && !ProgressKnown && !WindowAway && Drawing.Motion;

	// the window is minimized or hidden (set by the window itself)
	public bool WindowAway { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(ProgressBusy)); } }

	// Windows' "show animations" switch changed (the window hears it)
	public void MotionChanged() => OnPropertyChanged(nameof(ProgressBusy));
	public string NowTitle { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(WindowTitle)); OnPropertyChanged(nameof(HeadTitle)); } } } = "Ready";
	// the card's big line: what runs now, or how the last run ended ("Finished in 1 h 41 min")
	public string HeadTitle => Summary?.Title ?? NowTitle;
	// the engine's own sentence under it: left out after a run that finished (the summary says it all), kept when it names what stopped a run
	public string VerdictLine => Summary != null && VerdictKind == "good" ? "" : Verdict;
	public string NowActivity { get; set => Set(ref field, value); } = "";
	public double Progress { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(WindowTitle)); } }
	public bool ProgressKnown { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(ProgressBusy)); OnPropertyChanged(nameof(WindowTitle)); } } }
	// the title bar and the taskbar button say what runs (read out on Alt+Tab, seen with the window behind the game)
	public string WindowTitle => !IsRunning ? Brand.Name :
		Waiting != "" ? $"{WaitingTitle} - {Brand.Name}" :
		$"{NowTitle}{(ProgressKnown ? $" {Progress * 100:0}%" : "")}{(FinishAt == "" ? "" : ", " + FinishAt)} - {Brand.Name}";

	// What the run waits on the player for is either a guided part in the game (they do it, then press Done) or something
	// only they can end outside it - their own Mod Organizer left open - which the run notices by itself: no Done to press
	public bool WaitingInGame { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(WaitingOutside)); OnPropertyChanged(nameof(WaitingTitle)); OnPropertyChanged(nameof(WindowTitle)); } } } = true;
	public bool WaitingOutside => !WaitingInGame;
	public string WaitingTitle => WaitingInGame ? "Your turn in the game" : "The run waits for you";

	// a new theme: every color a converter picked is picked again from the new one
	public void Repaint()
	{
		OnPropertyChanged(nameof(MessageKind));
		OnPropertyChanged(nameof(VerdictKind));
		OnPropertyChanged(nameof(Mo2Open));
		OnPropertyChanged(nameof(GameRunning));
		foreach (var s in Steps) {
			s.Repaint();
		}
	}
	// the whole run's share done (steps finished + the running step's part), for the taskbar button
	public double RunProgress { get; set => Set(ref field, value); }
	// what the taskbar draws: the share done, at least a visible piece while it waits, all of it when it is red
	public double TaskbarValue { get; set => Set(ref field, value); }
	public System.Windows.Shell.TaskbarItemProgressState TaskbarState { get; set => Set(ref field, value); }
	public string Clock { get; set => Set(ref field, value); } = "";
	public string Left { get; set => Set(ref field, value); } = "";
	// when the run should be done, by the steps' usual times ("done about 21:40"; "" when not known): beside the clock, in the
	// title the taskbar button shows, and in the notification-area icon's tip
	public string FinishAt { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(WindowTitle)); } } = "";

	// the clock time in the player's own way of writing it (24 hours or AM / PM)
	public static string DoneAbout(DateTime a_at) => "done about " + a_at.ToString("t", System.Globalization.CultureInfo.CurrentCulture);
	public string Verdict { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(VerdictLine)); } } = "";
	public string VerdictKind { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(VerdictLine)); } } = "";
	public string Waiting { get; set { if (Set(ref field, value)) { OnPropertyChanged(nameof(ShowProgress)); OnPropertyChanged(nameof(ProgressBusy)); OnPropertyChanged(nameof(WindowTitle)); } } } = "";
	public string Interrupted { get; set => Set(ref field, value); } = "";
	public string Message { get; set => Set(ref field, value); } = "";
	public string MessageKind { get; set => Set(ref field, value); } = "";
	public string EstimateText { get; set => Set(ref field, value); } = "";
	public bool StopAfterAsked { get; set => Set(ref field, value); }
	// the LOG box shows the detailed log (every line) when ticked, else the simple step-by-step one
	public bool DetailedLog { get; set { if (Set(ref field, value)) { TailLog(true); } } }
	// the step the setup's last run stopped at ("" = nothing to run again): the "Run again from ..." button
	public string RetryFrom { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(RetryText)); } } = "";
	public string RetryText => RetryFrom == "" ? "" : "Run _again from " + StepCatalog.Name(RetryFrom);
	// a queue of setups going: which one runs ("" = none)
	public string QueueText { get; set => Set(ref field, value); } = "";
	// his checklist 450: the picked step's last run and what it did, and the steps out of date (hidden while a run goes - the log
	// is what matters then)
	public string LastCardTitle { get; set => Set(ref field, value); } = "";
	public string LastCardText => IsRunning ? "" : _lastCard;
	string _lastCard = "";
	string _outOfDate = "";
	Dictionary<string, string> _lastDetail = [];
	int _lastLoad;

	// the run that ended, summed up under its verdict (null while a run goes, and before the first run of a setup)
	public RunSummary? Summary { get; private set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); OnPropertyChanged(nameof(SummaryRows)); OnPropertyChanged(nameof(HasSummary)); OnPropertyChanged(nameof(HeadTitle)); OnPropertyChanged(nameof(VerdictLine)); } } }
	public IReadOnlyList<RunSummary.Row> SummaryRows => Summary?.Rows ?? [];
	public bool HasSummary => Summary != null;
	// the header drops what it can spare in a narrow window (set by the window as it is sized)
	public bool Roomy { get; set => Set(ref field, value); } = true;
	Journal? _summaryOf;
	// "step 6 of 13" beside the clock while a run goes
	public string StepOf { get; set => Set(ref field, value); } = "";

	// The first start, step by step (shown until a setup has run once): what to press next, and what each press does. A
	// new modder follows it without the readme; anyone else sees it once
	// Kind colors the mark (done / next / later); only the step that comes next shows its text, and stands out
	public sealed record GuideRow(string Mark, string Title, string Text, string Kind)
	{
		public bool Next => Kind == "";
		public string TitleKind => Next ? "" : "muted";
	}

	public IReadOnlyList<GuideRow> Guide { get; private set { field = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasGuide)); } } = [];
	public bool HasGuide => Guide.Count > 0;
	bool _checkedClean, _dried, _everRan;

	public static List<GuideRow> FirstSteps(bool a_hasSetup, bool a_checked, bool a_dried)
	{
		var next = !a_hasSetup ? 0 : !a_checked ? 1 : !a_dried ? 2 : 3;
		(string Title, string Text)[] steps = [
			("Make a setup", "Press New setup and pick your Mod Organizer and the profile you play. Its steps are found from Mod Organizer's Executables list, and Find my tools names any tool that is missing."),
			("Check it", "Press Check (F5). Every tool and setting is looked at, and whatever would stop a run is named with what to do about it."),
			("Try a dry run", "Press Dry run to see what a run would switch off, what it would build and the room it needs on disk. Nothing is started and nothing is changed."),
			("Start", "Press START. Before a setup's first run it lists your tools, each to set up once in its own window (Tools, at the top, any time later). Then leave the PC to it: the tools run in order, each output is switched on as it is built, and your profile is put back when the run ends - after a Stop or a failure too."),
		];
		return steps.Select((s, i) => new GuideRow(i < next ? Theme.Glyph("Done") : (i + 1).ToString(InvariantCulture), s.Title, s.Text, i < next ? "good" : i == next ? "" : "muted")).ToList();
	}

	void ShowGuide()
	{
		IReadOnlyList<GuideRow> now = Setups.Count > 0 && (_everRan || IsRunning) ? [] : FirstSteps(Setup != null, _checkedClean, _dried);
		// a look every second or three: the card is laid out again only when a step of it changed
		if (!now.SequenceEqual(Guide)) {
			Guide = now;
		}
	}

	// the window's own work (WorkInHand): while any is in hand the window says so and stays open
	readonly WorkInHand _work = new();
	public string InHand => _work.Now;
	public event Action? Freed { add => _work.Freed += value; remove => _work.Freed -= value; }

	public IDisposable Working(string a_what) => _work.Begin(a_what);

	// the status bar's plain answer to "may I close this now?"
	public string CloseText { get; set => Set(ref field, value); } = "Safe to close";
	public string CloseKind { get; set => Set(ref field, value); } = "good";

	public static (string Kind, string Text) CloseState(bool a_running, bool a_starting, string a_inHand) =>
		a_inHand != "" ? ("bad", "Do not close yet: " + a_inHand) :
		a_running ? ("warn", "Closing stops the run") :
		a_starting ? ("warn", "Closing stops the run that is starting") : ("good", "Safe to close");

	void ShowClose() => (CloseKind, CloseText) = CloseState(IsRunning, Starting || _queueGoing, InHand);

	public Cmd DryRunCommand => field ??= new(() => _ = StartWith(null, null, null, true));
	public Cmd StartCommand => field ??= new(Start);
	public Cmd StopAfterCommand => field ??= new(StopAfter);
	public Cmd StopNowCommand => field ??= new(StopNow);
	public Cmd ContinueCommand => field ??= new(Continue);
	public Cmd RestoreCommand => field ??= new(() => _ = RunRestore());
	public Cmd CheckCommand => field ??= new(() => _ = RunCheck());
	public Cmd RetryCommand => field ??= new(() => _ = RunRetry());

	// "A new version is out" (his checklist 471): the banner's text ("" = nothing to tell), its download page and "Not now"
	public string NewVersion { get; set { if (Set(ref field, value)) OnPropertyChanged(nameof(CanInstallNew)); } } = "";
	string _newVersion = "";
	// the program can take that version and install it itself: while its newest version comes from its own GitHub release
	// (a Nexus download needs the player's own key - there the banner has only the download page)
	public bool CanInstallNew => ProgramUpdate.Installs && _newVersion != "";
	public Cmd OpenNewVersionCommand => field ??= new(() => {
		if (OpenPage(ProgramUpdate.Page) is { } why) {
			Say("The download page did not open: " + why, "bad");
		}
	});
	public Cmd SetAsideNewVersionCommand => field ??= new(() => {
		if (_newVersion != "") {
			ProgramUpdate.SetAside(AppPaths.Data, _newVersion);
		}
		NewVersion = "";
	});

	// the off-screen pictures: the banner as it shows for a made-up newer version ("" = no banner)
	public void DemoNewVersion(string a_version)
	{
		_newVersion = a_version;
		NewVersion = a_version == "" ? "" : $"{Brand.Name} {a_version} is out (you have {MainWindow.AppVersion}). Its download page has what changed.";
		OnPropertyChanged(nameof(CanInstallNew));
	}

	// asked once the window is up, off its thread; a look that fails says nothing (the next start tries again)
	public async Task LookForNewVersion(string a_current)
	{
		var newer = await Task.Run(() => ProgramUpdate.Newer(AppPaths.Data, a_current, DateTime.UtcNow, CancellationToken.None)).ConfigureAwait(true);
		// switched off in Settings while the look was out: nothing is shown
		if (newer != null && !ProgramUpdate.IsOff(AppPaths.Data)) {
			_newVersion = newer;
			NewVersion = $"{Brand.Name} {newer} is out (you have {a_current}). Its download page has what changed.";
		}
	}
	Task RunRestore() => Restore();
	Task RunCheck() => Check();

	public Setup? Setup { get; private set { field = value; ++_setupEdits; OnPropertyChanged(nameof(CanStart)); OnPropertyChanged(nameof(NewGame)); } }
	// Counts every change to the setup shown: another one picked or read again, and each change saved into it (a step
	// switched, moved, set to use its old output, its settings). What a check found for the setup as it was before the
	// change is not shown as the findings of the setup as it is now
	int _setupEdits;
	public event Action<string, string>? Alert;
	// something was just set going from here: the window looks at once and then at its lively pace
	public event Action? WantsLook;
	// new log text (and true when the box must be emptied first: another run's log)
	public event Action<string, bool>? LogChanged;

	Journal? _run;
	RunState? _lastState;
	bool _wasGoing;

	// The run ended, for the notice: it was going at the last look and is over now, its last chores too - only then is its verdict
	// written. A run whose engine died (still "running" on record) is told by the interrupted-run band, not here
	public static bool RunEnded(bool a_wasGoing, bool a_going, RunState a_state) => a_wasGoing && !a_going && a_state != RunState.Running;
	string _lastWaiting = "";
	readonly LogTail _logTail = new();
	Dictionary<string, TimeSpan> _usual = [];

	// the usual step times of the run shown, worked out again for each new run (a run of the same setup brings the times its
	// earlier runs learned), from the records the look already read off the window's thread
	string? _runUsualFor;
	Dictionary<string, TimeSpan> _runUsual = [];

	Dictionary<string, TimeSpan> RunUsual(Journal a_run, List<Journal> a_runs)
	{
		var key = a_run.SetupFile + "\n" + a_run.Id;
		if (!string.Equals(_runUsualFor, key, StringComparison.OrdinalIgnoreCase)) {
			_runUsualFor = key;
			_runUsual = Engine.UsualTimes(a_run.SetupFile, a_runs);
		}
		return _runUsual;
	}

	public MainViewModel()
	{
		_work.Changed += ShowClose;
		LoadSetups();
		Poll();
	}

	public void LoadSetups(string? a_select = null)
	{
		Directory.CreateDirectory(AppPaths.Setups);
		var keep = a_select ?? SelectedSetup?.File;
		Setups.Clear();
		foreach (var f in Directory.GetFiles(AppPaths.Setups, "*.json").Where(f => !ToolRunner.Setup.IsStore(f)).Order(StringComparer.OrdinalIgnoreCase)) {
			Setups.Add(new SetupItem(Path.GetFileNameWithoutExtension(f), f));
		}
		SelectedSetup = Setups.FirstOrDefault(s => string.Equals(s.File, keep, StringComparison.OrdinalIgnoreCase)) ?? Setups.FirstOrDefault();
	}


	// the setup of this Mod Organizer and profile, selected; false when there is none
	public bool SelectFor(string a_mo2Exe, string a_base, string a_profile)
	{
		foreach (var item in Setups) {
			try {
				var s = ToolRunner.Setup.Load(item.File);
				if (string.Equals(s.Profile, a_profile, StringComparison.OrdinalIgnoreCase) && Files.SamePath(s.Mo2Exe, a_mo2Exe) &&
				    Files.SamePath(Mo2Instance.Load(s.Mo2Exe, s.Mo2Instance).BaseDir, a_base)) {
					SelectedSetup = item;
					return true;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) {
				// a setup that does not read is no match
			}
		}
		return false;
	}

	// the setup the window shows was read (again, or another one): what stands open for one setup can follow
	public event Action? SetupShown;

	public void LoadSteps()
	{
		ReadSteps();
		SetupShown?.Invoke();
	}

	void ReadSteps()
	{
		Steps.Clear();
		Setup = null;
		_lastDetail = [];
		_outOfDate = "";
		_checkedClean = _dried = false;
		ShowLastCard();
		if (SelectedSetup == null) {
			ShowGuide();
			return;
		}
		try {
			Setup = Setup.Load(SelectedSetup.File);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) {
			Say("This setup does not read: " + e.Message, "bad");
			return;
		}
		_usual = Engine.UsualTimes(SelectedSetup.File);
		var n = 0;
		foreach (var s in Setup.Steps) {
			var info = StepCatalog.Info(s.Key);
			Steps.Add(new StepRow { Key = s.Key, Name = info.Name, About = info.About, On = s.On, Number = ++n,
				Usual = _usual.TryGetValue(s.Key, out var t) ? Short(t) : "" });
		}
		MarkLeftOut();
		Estimate();
		LoadHistory();
		_everRan = History.Count > 0;
		ShowGuide();
		_ = LoadLastRuns();
		_ = LookForDevBench();
	}

	// The clean save and the final save are carried out by DevBench in the game; without it ticked in the profile each one is done
	// by hand, guided step by step. Their rows say so (the profile's mods are read off the window's thread)
	public static readonly IReadOnlyList<string> DevBenchSteps = ["cleansave", "playsave"];
	public const string NoDevBench = "DevBench is not enabled in this profile: you will be guided through this step by hand.";

	async Task LookForDevBench()
	{
		if (Setup is not { } setup) {
			return;
		}
		bool on;
		try {
			on = await Task.Run(() => DevBench.InProfile(new Profile(Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), setup.Profile, () => false)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
			return;
		}
		// another setup shown meanwhile: its own look answers for it
		if (!ReferenceEquals(Setup, setup)) {
			return;
		}
		foreach (var row in Steps.Where(r => DevBenchSteps.Contains(r.Key))) {
			row.Needs = on ? "" : NoDevBench;
		}
	}

	// each step's last run, worked out off the window's thread (old run logs and the profile's mod list are read); a newer load
	// or another setup picked meanwhile wins
	public async Task LoadLastRuns()
	{
		if (Setup is not { } setup) {
			return;
		}
		var load = ++_lastLoad;
		_lastLoaded = DateTime.UtcNow;
		List<(LastRuns.Section Section, string Line, string Text)> got;
		try {
			got = await Task.Run(() => {
				var logs = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
				return LastRuns.Build(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), Engine.Runs(), AppPaths.Data)
					.Select(s => (s, LastRuns.Line(s), string.Join("\n", LastRuns.Detail(s, setup.Profile, LastRuns.LogOf(s, logs))))).ToList();
			});
		} catch (Exception e) {
			// a setup whose Mod Organizer does not read (Check says more): the card says so, never an empty "never run"
			if (load == _lastLoad && ReferenceEquals(setup, Setup)) {
				_outOfDate = "The last runs could not be worked out: " + e.Message;
				ShowLastCard();
			}
			return;
		}
		if (load != _lastLoad || !ReferenceEquals(setup, Setup)) {
			return;
		}
		// a setup that lists a step twice shares one section between both rows (never a duplicate-key throw)
		_lastDetail = got.GroupBy(g => g.Section.Key).ToDictionary(g => g.Key, g => g.First().Text);
		foreach (var row in Steps) {
			var (s, line, _) = got.FirstOrDefault(g => g.Section.Key == row.Key);
			row.LastRun = s?.Latest == null ? "" : line;
			row.LastKind = s == null ? "" : s.OutOfDate ? "warn" : s.Latest?.State is StepState.Failed or StepState.Stopped ? "bad" : s.Current ? "good" : "muted";
		}
		var stale = got.Where(g => g.Section.OutOfDate && Steps.Any(r => r.Key == g.Section.Key && r.Runs)).Select(g => StepCatalog.Name(g.Section.Key)).ToList();
		_outOfDate = stale.Count == 0 ? "" :
			$"Changed in Mod Organizer since they last finished: {string.Join(", ", stale)}. Run them again to bring their output up to date (right-click a step to run only it, or START for every step).";
		// a step that uses its old output is not run again by a START: that it is out of date is said on its own
		var oldStale = got.Where(g => g.Section.OutOfDate && Steps.Any(r => r.Key == g.Section.Key && r.UsesOld)).Select(g => StepCatalog.Name(g.Section.Key)).ToList();
		if (oldStale.Count > 0) {
			_outOfDate += (_outOfDate == "" ? "" : "\n\n") +
				$"Set to use their old output, and your mods changed in Mod Organizer since it was made: {string.Join(", ", oldStale)}. START's preview says what that means for each; \"Build it again\" under the steps has a step built anew.";
		}
		ShowLastCard();
	}

	DateTime _lastLoaded = DateTime.MinValue;

	// the window came back into focus (a mod may have been ticked in Mod Organizer meanwhile): at most every 10 s
	public Task LoadLastRunsIfStale() => DateTime.UtcNow - _lastLoaded < TimeSpan.FromSeconds(10) ? Task.CompletedTask : LoadLastRuns();

	void ShowLastCard()
	{
		var text = SelectedStep is { } r && _lastDetail.TryGetValue(r.Key, out var d) ? d : "";
		// a card that holds only why the last runs could not be read says LAST RUNS, never OUT OF DATE over an error
		LastCardTitle = text != "" ? "LAST RUN - " + SelectedStep!.Name.ToUpperInvariant() :
			_outOfDate.StartsWith("The last runs could not", StringComparison.Ordinal) ? "LAST RUNS" : _outOfDate != "" ? "OUT OF DATE" : "";
		_lastCard = _outOfDate == "" ? text : text == "" ? _outOfDate : text + "\n\n" + _outOfDate;
		OnPropertyChanged(nameof(LastCardText));
	}

	// a new game (the dashboard's tick beside START, kept in the setup): no save of the player's to keep clean, so the clean
	// save and the final save are left out of every run while it is on
	public bool NewGame { get => Setup?.NewGame ?? false; set => _ = SetNewGame(value); }

	async Task SetNewGame(bool a_on)
	{
		if (Setup is not { } setup || a_on == setup.NewGame) {
			return;
		}
		if (NotNow("changed") is { } why) {
			Say(why, "bad");
		} else {
			setup.Root["newGame"] = a_on;
			if (!await SaveSetup()) {
				setup.Root["newGame"] = !a_on;
			}
			if (ReferenceEquals(Setup, setup)) {
				MarkLeftOut();
				Estimate();
				// the last check's findings were for the other set of steps
				Issues.Clear();
				ShowReady();
			}
		}
		OnPropertyChanged(nameof(NewGame));
	}

	// why the setup shown is not changed at this moment, in words (null: it may be) - a run uses it, or the change before
	// this one is still on its way into the setup's file
	string? NotNow(string a_done) =>
		IsRunning || SetupInUse ? $"Not {a_done}: a run is using this setup right now." : NotYetSaved(a_done);

	// each row's standing in the setup beyond its tick: left out for a new game, or not run because its old output is used
	void MarkLeftOut()
	{
		var entries = Setup?.Steps ?? [];
		foreach (var s in Steps) {
			s.LeftOut = Setup?.LeftOutForNewGame(s.Key) == true;
			s.UsesOld = entries.FirstOrDefault(x => x.Key == s.Key) is { } entry && Setup!.UsesOld(entry);
		}
		OnPropertyChanged(nameof(NewGame));
		OnPropertyChanged(nameof(UseOldLabel));
	}

	// What using a step's old output means right now, read off the disk away from the window's thread: the step as it stands
	// (null with why when it has none to use) - worked out as if the step were already set so, since the tools that still run
	// are part of the answer
	public async Task<(OldOutput.Look? Look, string? WhyNot)> OldOutputOf(StepRow a_row)
	{
		if (Setup is not { } setup || setup.Steps.FirstOrDefault(s => s.Key == a_row.Key) is not { } entry) {
			return (null, "No setup is shown.");
		}
		if (OldOutput.WhyNot(entry.Key, new StepSettings(entry.Settings)) is { } no) {
			return (null, no);
		}
		var with = setup.Clone();
		foreach (var o in (with.Root["steps"] as JsonArray)?.OfType<JsonObject>().Where(o => Setup.Str(o, "step") == a_row.Key) ?? []) {
			o["on"] = true;
			o[OldOutput.Key] = true;
		}
		var look = await Task.Run(() => {
			var mo2 = Mo2Instance.Load(with.Mo2Exe, with.Mo2Instance);
			var control = new Mo2Control(mo2, with.Profile, RunLog.Null);
			var states = new Profile(mo2, with.Profile, () => control.Running).States();
			// every step that uses its old output is looked at, as the check before a run does, and this step's answer taken:
			// looked at alone, a master that an earlier old output brings was called gone here and found there by the check
			return OldOutput.LookAt(with, mo2, states, with.EnabledSteps, DryRun.LastOf(with, mo2, AppPaths.Data), with.KeptSteps).FirstOrDefault(l => l.Step == a_row.Key);
		});
		return look == null ? (null, $"{a_row.Name} has no old output to use.") : (look, null);
	}

	// the box's text: what happens, then every line of what it means for this tool
	public string OldOutputText(OldOutput.Look a_look) =>
		$"{a_look.Name} will not run. The output it made before (\"{a_look.Mod}\") stays in your game, and the tools after it build on it.\n\n" +
		string.Join("\n\n", OldOutput.Warning(a_look, Setup?.Profile ?? "")) +
		"\n\nYou can take this back any time: select the step and press \"Build it again\".";

	// a step set to use its old output, or back to being built (saved at once; the row shows it once it is saved)
	public async Task SetUseOld(StepRow a_row, bool a_use)
	{
		if (Setup is not { } setup || setup.Root["steps"] is not JsonArray arr) {
			return;
		}
		// the row of a setup that is no longer the one shown (another was picked while its box was worked out): a step of
		// the same name in the setup shown now is never switched in its place
		if (!Steps.Contains(a_row)) {
			Say("Not changed: another setup was picked meanwhile - select the step again.", "warn");
			return;
		}
		if (NotNow("changed") is { } why) {
			Say(why, "bad");
			return;
		}
		var steps = arr.OfType<JsonObject>().Where(o => Setup.Str(o, "step") == a_row.Key).ToList();
		var before = steps.Select(o => o[OldOutput.Key]?.DeepClone()).ToList();
		foreach (var o in steps) {
			if (a_use) {
				o[OldOutput.Key] = true;
			} else {
				o.Remove(OldOutput.Key);
			}
		}
		if (!await SaveSetup()) {
			for (var i = 0; i < steps.Count; ++i) {
				if (before[i] != null) {
					steps[i][OldOutput.Key] = before[i];
				} else {
					steps[i].Remove(OldOutput.Key);
				}
			}
			return;
		}
		// (another setup picked while this one was being written: its file is saved, and nothing shown is its)
		if (!ReferenceEquals(Setup, setup) || !Steps.Contains(a_row)) {
			return;
		}
		MarkLeftOut();
		Estimate();
		// the last check's findings were for the other set of steps
		Issues.Clear();
		ShowReady();
		Say(a_use ? $"{a_row.Name} is not run from now on: its old output is used. START's preview says what that means each time." : $"{a_row.Name} is built again in the next run.", a_use ? "warn" : "good");
		_ = LoadLastRuns();
	}

	void Estimate()
	{
		var total = Steps.Where(s => s.Runs).Select(s => _usual.TryGetValue(s.Key, out var t) ? t : TimeSpan.Zero).Aggregate(TimeSpan.Zero, (a, b) => a + b);
		var unknown = Steps.Count(s => s.Runs && !_usual.ContainsKey(s.Key));
		EstimateText = total == TimeSpan.Zero ? "" : $"about {Long(total)}{(unknown > 0 ? $" + {unknown} step(s) never timed" : "")}";
	}

	public static string Short(TimeSpan a_t) => a_t.TotalMinutes < 1 ? $"{a_t.TotalSeconds:0}s" : a_t.TotalMinutes < 60 ? $"{a_t.TotalMinutes:0}m" : $"{(int)a_t.TotalHours}h {a_t.Minutes:00}m";

	public static string Long(TimeSpan a_t) => a_t.TotalMinutes < 60 ? $"{Math.Max(1, (int)Math.Round(a_t.TotalMinutes))} min" : $"{(int)a_t.TotalHours} h {a_t.Minutes:00} min";

	void LoadHistory()
	{
		History.Clear();
		foreach (var j in Engine.Runs().Where(j => SelectedSetup == null || string.Equals(j.SetupFile, SelectedSetup.File, StringComparison.OrdinalIgnoreCase)).Take(100)) {
			var took = j.Ended is { } e ? Short(e - j.Started) : j.OwnerAlive ? "going" : "-";
			History.Add(new HistoryRow(j.Started.ToString("ddd MMM d  HH:mm", InvariantCulture), j.SetupName, took,
				j.Verdict == "" ? (j.Unfinished ? (j.OwnerAlive ? "running" : "did not finish") : "") : j.Verdict, j.State is RunState.Finished or RunState.Restored, j.Folder));
		}
	}

	void ShowReady()
	{
		IsRunning = false;
		Summary = null;
		_summaryOf = null;
		StepOf = "";
		TaskbarState = System.Windows.Shell.TaskbarItemProgressState.None;
		NowTitle = "Ready";
		var on = Steps.Count(s => s.Runs);
		NowActivity = SelectedSetup == null ? "Make a setup first (New setup)." :
			$"Press START to run the {on} step(s) that will run{(EstimateText == "" ? "" : ", " + EstimateText)}. Press Check (F5) first to see that every tool is found.";
	}

	public void Say(string a_text, string a_kind = "")
	{
		// the kind first: a screen reader hears the message as soon as it changes, and its urgency comes from the kind
		MessageKind = a_kind;
		Message = a_text;
	}

	// ---------------------------------------------------------------- the live view
	bool _polling, _pollAgain;

	// Mod Organizer, then the game's images: one look at the process list answers both
	static readonly string[] Watched = ["ModOrganizer.exe", .. Mo2Control.GameImages];

	static (bool Mo2, bool Game) Seen()
	{
		var up = Procs.Running(Watched);
		return (up[0], up.AsSpan(1).Contains(true));
	}

	// the window's look: the process list and the run records are read off the window's thread, one look at a time. A look
	// asked for while one is out (a record changed meanwhile) runs right after it, so the newest state is never left unread
	public async Task PollAsync()
	{
		if (_polling) {
			_pollAgain = true;
			return;
		}
		_polling = true;
		try {
			do {
				_pollAgain = false;
				var (seen, runs, queue) = await Task.Run(() => {
					var runs = Engine.Runs();
					return (Seen(), runs, QueueLook(runs));
				});
				Poll(seen.Mo2, seen.Game, runs, queue);
			} while (_pollAgain);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException) {
			// a file or process that changed mid-read: the next look reads it again
		} finally {
			_polling = false;
		}
	}

	public void Poll()
	{
		var runs = Engine.Runs();
		var (mo2, game) = Seen();
		Poll(mo2, game, runs, QueueLook(runs));
	}

	// something is going that the window shows second by second (a run and its clock, a start being checked, a queue between two
	// setups); while nothing is, the window looks seldom
	public bool Busy => IsRunning || Starting || _queueGoing;
	bool _queueGoing;

	// the queue going (its line for the window), else the last queue's summary while nothing has started since it ended
	sealed record QueueSeen(QueueRun.State? Going, string Text);

	// a start from this window hides the last queue's summary; so does any run started after that queue ended
	DateTime _queueShownSince = DateTime.MinValue;

	QueueSeen QueueLook(List<Journal> a_runs)
	{
		var q = QueueRun.Read(AppPaths.Data);
		if (q?.Running is { } now) {
			var next = q.Setups.Skip(q.Current + 1).Select(Path.GetFileNameWithoutExtension).ToList();
			return new QueueSeen(q, $"Queue: setup {q.Current + 1} of {q.Setups.Count} ({Path.GetFileNameWithoutExtension(now)})" + (next.Count > 0 ? ", then " + string.Join(", ", next) : ""));
		}
		var last = QueueRun.LastFile(AppPaths.Data);
		try {
			if (File.Exists(last) && File.GetLastWriteTime(last) is var ended && ended > _queueShownSince && !a_runs.Any(j => j.Started > ended)) {
				return new QueueSeen(null, "Last queue - " + string.Join("; ", Files.ReadSharedText(last).Split('\n', StringSplitOptions.RemoveEmptyEntries)));
			}
		} catch (IOException) {
		}
		return new QueueSeen(null, "");
	}

	void Poll(bool a_mo2, bool a_game, List<Journal> a_runs, QueueSeen a_queue)
	{
		Mo2Open = a_mo2;
		GameRunning = a_game;
		var runs = a_runs;
		FollowStart(runs);
		var active = Engine.Active(runs);
		// the run going, else the one just watched, else this setup's last run (its log and verdict stay on screen)
		_run = active ?? (_run is { } r && Directory.Exists(r.Folder) ? Fresh(runs, r) : Engine.Latest(SelectedSetup?.File, runs));
		var queue = a_queue.Going;
		_queueGoing = queue != null;
		QueueText = a_queue.Text;
		var interrupted = Engine.Interrupted(runs);
		Interrupted = interrupted == null ? "" : $"The run of {interrupted.Started:ddd HH:mm} ({interrupted.SetupName}) did not finish - your profile may still have outputs switched off.";
		_everRan = SelectedSetup != null && Engine.Latest(SelectedSetup.File, runs) != null;
		if (_run == null) {
			RetryFrom = "";
			ShowReady();
			ShowGuide();
			ShowClose();
			return;
		}
		IsRunning = _run.Live && _run.OwnerAlive;
		RetryFrom = !IsRunning && queue == null && SelectedSetup != null && string.Equals(_run.SetupFile, SelectedSetup.File, StringComparison.OrdinalIgnoreCase) &&
		            Retry.FromStep(_run) is { } from ? from : "";
		StopAfterAsked = _run.StopAfterStep;
		var cur = _run.Steps.FirstOrDefault(s => s.State == StepState.Running);
		NowTitle = IsRunning ? (cur?.Name ?? (_run.FinishingUp ? "Finishing up" : "Getting ready")) : _run.State switch {
			RunState.Finished => "Finished",
			RunState.Stopped => "Stopped",
			RunState.Restored => "Put back",
			_ => "Stopped by a problem",
		};
		// between and after the steps the run itself says what it is at (a setup's command, packing the old outputs); and what it
		// waits on the player for - their own Mod Organizer to close - whatever step is going
		var outside = IsRunning && _run.Doing.StartsWith(Runner.WaitingPrefix, StringComparison.Ordinal);
		var act = outside ? _run.Doing : cur?.Activity ?? (IsRunning ? _run.Doing : "");
		WaitingInGame = !outside;
		Waiting = act.StartsWith(Runner.WaitingPrefix, StringComparison.Ordinal) ? act[Runner.WaitingPrefix.Length..] : "";
		// a box the run does not answer itself is said beside what the step does (the run's watch names it)
		var box = IsRunning && _run.Box != "" ? "ON SCREEN: " + _run.Box : "";
		NowActivity = Waiting != "" ? "" : box == "" ? act : act == "" ? box : act + " - " + box;
		ProgressKnown = cur?.Progress != null;
		Progress = cur?.Progress ?? 0;
		var steps = _run.Steps.Count;
		RunProgress = steps == 0 ? 0 : Math.Clamp((_run.Steps.Count(s => s.State is StepState.Done or StepState.Skipped) + (cur?.Progress ?? 0)) / steps, 0, 1);
		if (_lastState == RunState.Running && IsRunning == false && _run.State != RunState.Finished) {
			_redSinceWatched = true;
		}
		TaskbarState = TaskbarFor(IsRunning, Waiting != "", _redSinceWatched || (_run.Unfinished && !_run.OwnerAlive));
		TaskbarValue = TaskbarState switch {
			System.Windows.Shell.TaskbarItemProgressState.Error => 1,
			System.Windows.Shell.TaskbarItemProgressState.Paused => Math.Max(RunProgress, 0.1),
			_ => RunProgress,
		};
		var elapsed = (_run.Ended ?? DateTime.Now) - _run.Started;
		Clock = Long(elapsed);
		// the running setup's own usual times, whichever setup is picked in the list (CodeRabbit on pull request #38)
		var usual = RunUsual(_run, runs);
		var ahead = _run.Steps.Where(s => s.State is StepState.Pending or StepState.Running).ToList();
		var leftT = ahead.Select(s => usual.TryGetValue(s.Key, out var t) ? (s.State == StepState.Running && s.Started != null ? Max(TimeSpan.Zero, t - (DateTime.Now - s.Started.Value)) : t) : TimeSpan.Zero)
			.Aggregate(TimeSpan.Zero, (a, b) => a + b);
		// a step still to come that never ran before has no usual time: what is left is then at least that much, and no
		// finish time is promised
		var partial = ahead.Any(s => !usual.ContainsKey(s.Key));
		Left = IsRunning && leftT > TimeSpan.Zero ? (partial ? "at least " : "about ") + Long(leftT) + " left" : "";
		FinishAt = IsRunning && leftT > TimeSpan.Zero && !partial ? DoneAbout(DateTime.Now + leftT) : "";
		StepOf = IsRunning && cur != null ? $"step {_run.Steps.IndexOf(cur) + 1} of {steps}" : "";
		// a run that ended is summed up once (its record is the same object until its file changes)
		if (IsRunning || _run.Live) {
			Summary = null;
			_summaryOf = null;
		} else if (!ReferenceEquals(_summaryOf, _run)) {
			_summaryOf = _run;
			Summary = RunSummary.Of(_run);
		}
		ShowGuide();
		ShowClose();
		Verdict = IsRunning ? "" : _run.Verdict;
		VerdictKind = _run.State switch {
			RunState.Finished or RunState.Restored => "good",
			RunState.Stopped => "warn",
			RunState.Failed => "bad",
			_ => "",
		};
		if (SelectedSetup != null && string.Equals(_run.SetupFile, SelectedSetup.File, StringComparison.OrdinalIgnoreCase)) {
			foreach (var row in Steps) {
				var rec = _run.Step(row.Key);
				row.State = rec?.State;
				row.IsCurrent = rec?.State == StepState.Running;
				row.Note = rec?.Note ?? "";
				row.Elapsed = rec?.Started is { } st ? Short((rec.Ended ?? DateTime.Now) - st) : "";
			}
		}
		TailLog();
		// alerts once per change: the run ended, or it waits for the player
		if (RunEnded(_wasGoing, IsRunning, _run.State)) {
			Alert?.Invoke(NowTitle, _run.Verdict);
			LoadHistory();
			_ = LoadLastRuns();
		}
		if (Waiting != "" && Waiting != _lastWaiting) {
			Alert?.Invoke(WaitingTitle, Waiting);
			_remindedAt = DateTime.Now;
		} else if (Waiting != "" && ReminderDue(ReminderMinutes(_run), _remindedAt, DateTime.Now)) {
			// his checklist 463: the same step still waits - said again, every so many minutes as the setup asks
			Alert?.Invoke(WaitingInGame ? "Still your turn in the game" : "The run still waits for you", Waiting);
			_remindedAt = DateTime.Now;
		}
		_lastWaiting = Waiting;
		_lastState = _run.State;
		_wasGoing = IsRunning;
	}

	static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

	// set when a run this window watched ended other than finished; cleared by the next start
	bool _redSinceWatched;

	// a run going: its share done; waiting for the player: paused (yellow); a run that needs the player's look: red; else none
	public static System.Windows.Shell.TaskbarItemProgressState TaskbarFor(bool a_running, bool a_waiting, bool a_needsLook) =>
		a_running ? (a_waiting ? System.Windows.Shell.TaskbarItemProgressState.Paused : System.Windows.Shell.TaskbarItemProgressState.Normal) :
		a_needsLook ? System.Windows.Shell.TaskbarItemProgressState.Error : System.Windows.Shell.TaskbarItemProgressState.None;
	DateTime _remindedAt;
	(string File, double Minutes)? _reminder;

	// the waiting run's own setup says how often (read once per setup file, not the setup picked in the window)
	double ReminderMinutes(Journal a_run)
	{
		if (_reminder is not { } r || !string.Equals(r.File, a_run.SetupFile, StringComparison.OrdinalIgnoreCase)) {
			double min = 0;
			try {
				min = Setup.Num(Setup.Load(a_run.SetupFile).Root, "guidedReminderMin") ?? 0;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException) {
			}
			_reminder = r = (a_run.SetupFile, min);
		}
		return r.Minutes;
	}

	// a reminder is due once every a_everyMin minutes (0 or less = never)
	public static bool ReminderDue(double a_everyMin, DateTime a_last, DateTime a_now) => a_everyMin > 0 && a_now - a_last >= TimeSpan.FromMinutes(a_everyMin);

	// the watched run's newest record (the cache re-reads it only when its file changed)
	static Journal Fresh(List<Journal> a_runs, Journal a_j) =>
		a_runs.FirstOrDefault(j => string.Equals(j.Folder, a_j.Folder, StringComparison.OrdinalIgnoreCase)) ?? a_j;

	// a run folder's log to show: the simple one unless the detailed one is asked for, or the run is older than the simple log
	public static string LogFile(string a_folder, bool a_detailed)
	{
		var simple = Path.Combine(a_folder, RunLog.SimpleName);
		// a run from before the simple log (its steps.txt empty, or made only by a later restore) shows its detailed log
		return !a_detailed && File.Exists(simple) && new FileInfo(simple).Length > 0 ? simple : Path.Combine(a_folder, "log.txt");
	}

	// The log is read on a worker - a slow disk never holds the window - one read at a time, its text handed over on the window's
	// thread. A look while a read is still going is left to the next one; a switch between the simple and the detailed log
	// (a_restart) waits for that read and then reads the other log from its start. LogRead: the newest read, so what needs its
	// text (First problem) can wait for it
	Task _tailing = Task.CompletedTask;
	public Task LogRead => _tailing;

	void TailLog(bool a_restart = false)
	{
		if (!a_restart && !_tailing.IsCompleted) {
			return;
		}
		_tailing = TailLogAfter(_tailing, _run == null ? null : LogFile(_run.Folder, DetailedLog), a_restart);
	}

	async Task TailLogAfter(Task a_before, string? a_file, bool a_restart)
	{
		await a_before;
		var (text, reset) = await Task.Run(() => {
			if (a_restart) {
				_logTail.Restart();
			}
			return _logTail.Read(a_file);
		});
		if (reset) {
			LogChanged?.Invoke("", true);
		}
		if (text != null) {
			LogChanged?.Invoke(text, false);
		}
	}

	// ---------------------------------------------------------------- actions
	void Start() => _ = StartWith(null, null, null);

	// his checklist 270: what the run will do, shown before anything changes (the setup's "previewBeforeRun", on unless switched
	// off); the window answers it (true = start). Title, text, and the button that starts - null when the start would be refused:
	// the box then only tells
	public Func<string, string, string?, bool>? ConfirmRun { get; set; }
	bool _previewing;

	// His word of 2026-11-21: before a setup's first run the player is shown its tools, each to set up by hand in its own
	// window. The window answers it (true = go on to the run, false = not now).
	public Func<Setup, IReadOnlyList<ToolSettings.ToSet>, bool>? OfferTools { get; set; }

	// "went on from that box" is kept per setup, with the window's own state
	public static string ToolsOfferedKey(string a_setupFile) => "tools.offered." + Path.GetFileNameWithoutExtension(a_setupFile).ToLowerInvariant();

	// True: go on with the start. Shown once, for a setup that has never run and has a tool to set up; what does not read here
	// is the start's own first finding. a_cannot: what stops the start, asked again once the box has closed (a tool may have
	// been open for minutes). a_stillPicked: a single run's setup is still the one picked in the window - asked once the
	// tools are read, before the box opens: another setup picked meanwhile never gets this one's box. a_unchanged: no change
	// was saved into the setup while its tools were read (a step switched on then is not in the list read, and the box would
	// be skipped or show too few tools) - asked before anything is decided from that list; the offer stays open for the next START
	async Task<bool> ToolsFirst(string a_file, Func<Setup> a_read, Func<string?> a_cannot, Func<bool>? a_stillPicked = null, Func<bool>? a_unchanged = null)
	{
		var key = ToolsOfferedKey(a_file);
		if (OfferTools == null || UiState.Flag(key)) {
			return true;
		}
		Setup setup;
		List<ToolSettings.ToSet> tools;
		bool ran;
		try {
			(setup, tools, ran) = await Task.Run(() => {
				var s = a_read();
				return (s, ToolSettings.ToSetUp(s), Engine.Latest(a_file) != null);
			});
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) {
			return true;
		}
		if (a_unchanged != null && !a_unchanged()) {
			Say("Not started: the setup changed while its tools were read - press Start again.", "warn");
			return false;
		}
		if (!ToolSettings.OfferFirst(ran, false, tools.Count)) {
			return true;
		}
		if (a_stillPicked != null && !a_stillPicked()) {
			Say("Not started: another setup was picked meanwhile - press Start again.", "warn");
			return false;
		}
		if (!OfferTools(setup, tools)) {
			Say("Not started. Your tools can be set up any time: Tools, at the top of the window.", "");
			return false;
		}
		UiState.Set(key, true);
		if (a_cannot() is { } now) {
			Say(now, "bad");
			return false;
		}
		return true;
	}

	string? CannotStart() =>
		SelectedSetup == null || Setup == null ? "Make or pick a setup first." : CannotStartAny();

	// what stops any start, a single run or a queue. A change to a setup that is still on its way into its file stops one
	// too: the run reads the file, and started in that moment it ran with the steps as they stood before the change
	string? CannotStartAny() =>
		Engine.Active() != null || QueueRun.Read(AppPaths.Data) != null ? "A run is going already." :
		Engine.Interrupted() != null ? "The last run did not finish - put your profile back first (the button at the top)." :
		NotYetSaved("started");

	string? NotYetSaved(string a_done) =>
		MovingData ? $"Not {a_done}: the program's data is being moved - it starts again when that is done." :
		Renaming ? $"Not {a_done} yet: the setup is being renamed - try again in a moment." :
		Saving ? $"Not {a_done} yet: the change before this one is still being saved - try again in a moment." : null;

	// the profile's mod list and plugin list as they are on disk (unreadable = a stamp of its own, so it never matches)
	static string ProfileStamp(string a_dir)
	{
		try {
			return string.Join("|", new[] { "modlist.txt", "plugins.txt" }.Select(f => Path.Combine(a_dir, f))
				.Select(f => File.Exists(f) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Files.ReadShared(f))) : "-"));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return "unreadable " + Guid.NewGuid();
		}
	}

	// a_dryOnly: the Dry run button - the same answer START gets, asked for on its own (also while a run goes: it then says so)
	public async Task StartWith(string? a_only, string? a_from, string? a_until, bool a_dryOnly = false)
	{
		if (_previewing) {
			return;
		}
		// (a dry run reads the setup's file as well: one made while a change is on its way into that file left the change out)
		if ((a_dryOnly ? (SelectedSetup == null || Setup == null ? "Make or pick a setup first." : NotYetSaved("looked at")) : CannotStart()) is { } why) {
			Say(why, "bad");
			return;
		}
		if (!a_dryOnly) {
			var mine = Setup!;
			var picked = SelectedSetup!.File;
			_previewing = true;
			try {
				// (its tools are read from a copy made here: the worker never walks the shown setup's own step list, which a
				// step dragged meanwhile rewrites under it)
				var asItIs = mine.Clone();
				var edits = _setupEdits;
				if (!await ToolsFirst(mine.File, () => asItIs, CannotStart, () => string.Equals(SelectedSetup?.File, picked, StringComparison.OrdinalIgnoreCase), () => _setupEdits == edits)) {
					return;
				}
			} finally {
				_previewing = false;
			}
			if (!string.Equals(SelectedSetup?.File, picked, StringComparison.OrdinalIgnoreCase)) {
				Say("Not started: another setup was picked meanwhile - press Start again.", "warn");
				return;
			}
		}
		if ((a_dryOnly || Setup.Bool(Setup!.Root, "previewBeforeRun") != false) && ConfirmRun != null) {
			_previewing = true;
			Say(a_dryOnly ? "Dry run: going through everything a start would meet ..." : "Working out what the run will do ...", "");
			var file = SelectedSetup!.File;
			byte[] shown;
			Setup setup;
			try {
				// the preview is built from the file's bytes as read now - the very bytes compared before the start (the
				// window's own copy can be older than the file - the fourth review, M4)
				shown = Files.ReadShared(file);
				setup = Setup.Parse(shown, file);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) {
				_previewing = false;
				Say("Could not read the setup: " + e.Message, "bad");
				return;
			}
			var usual = _usual;
			DryRunReport report;
			string? profileDir = null;
			string profileBefore = "";
			try {
				report = await Task.Run(() => {
					// the profile's lists are stamped before the dry run reads them; a Mod Organizer that does not load is the dry
					// run's own first finding
					try {
						profileDir = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance).ProfileDir(setup.Profile);
						profileBefore = ProfileStamp(profileDir);
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) {
						profileDir = null;
					}
					return DryRun.Make(new RunRequest(file, AppPaths.Data, a_only, a_from, a_until));
				});
			} catch (Exception e) {
				Say("Could not work out what the run would do: " + e.Message, "bad");
				return;
			} finally {
				_previewing = false;
			}
			if (a_dryOnly) {
				_dried = true;
				ShowGuide();
			}
			var text = string.Join("\n", report.Lines(k => usual.TryGetValue(k, out var t) ? t : null));
			var title = a_dryOnly ? "Dry run - nothing is started" : report.Starts ? "What this run will do" : "This run would be refused";
			if (!ConfirmRun(title, text, report.Starts ? "Start the run" : null)) {
				var refusals = WithTodo(string.Join("; ", report.Refusals.Select(r => r.Text)));
				Say(report.Starts ? "Not started - nothing was changed." : a_dryOnly ? "Dry run: a start now would be refused - " + refusals : "Not started: " + refusals, report.Starts ? "" : "bad");
				return;
			}
			// a mod ticked or a download installed in Mod Organizer while the box was open changes what goes off - looked at
			// first, so the checks below run after the last wait and nothing can change between them and the start
			if (profileDir != null && await Task.Run(() => ProfileStamp(profileDir)) != profileBefore) {
				Say("Not started: the profile changed in Mod Organizer while the preview was open - press Start again to see the new one.", "warn");
				return;
			}
			// the window waited on the player: look again, and start only the setup that was previewed
			if (CannotStart() is { } now) {
				Say(now, "bad");
				return;
			}
			if (!string.Equals(SelectedSetup?.File, file, StringComparison.OrdinalIgnoreCase)) {
				Say("Not started: another setup was picked while the preview was open - press Start again.", "warn");
				return;
			}
			// a step switched while the box was open changes what runs: the player sees a new preview first
			bool same;
			try {
				same = Files.ReadShared(file).AsSpan().SequenceEqual(shown);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				same = false;
			}
			if (!same) {
				Say("Not started: the setup changed while the preview was open - press Start again to see the new one.", "warn");
				return;
			}
			// the very file previewed, never whatever is selected by now
			StartNow(file, a_only, a_from, a_until);
			return;
		}
		if (a_dryOnly) {
			return;
		}
		// (no preview: looked at again here - the tools box may have waited on the player or on the disk, and a change to
		// the setup begun meanwhile is still on its way into the file the run reads)
		if (CannotStart() is { } notNow) {
			Say(notNow, "bad");
			return;
		}
		StartNow(SelectedSetup!.File, a_only, a_from, a_until);
	}

	void StartNow(string a_file, string? a_only, string? a_from, string? a_until)
	{
		_queueShownSince = DateTime.Now;
		var (ok, text) = Engine.StartRun(a_file, a_only, a_from, a_until);
		if (!ok) {
			Say("Could not start: " + text, "bad");
			return;
		}
		_redSinceWatched = false;
		// the engine checks everything first and may refuse: the next looks read its run record or its refusal
		Say("Starting - checking every tool and setting ...", "");
		_startAsked = DateTime.Now;
		_startPid = int.TryParse(text, System.Globalization.NumberStyles.Integer, InvariantCulture, out var pid) ? pid : null;
		_run = null;
		WantsLook?.Invoke();
	}

	DateTime? _startAsked;
	// the engine that start began, while it checks (a big profile, or a Mod Organizer it waits for, can take minutes)
	int? _startPid;

	// a start was asked and the engine has neither made its run record nor refused yet (it checks every tool first)
	public bool Starting => _startAsked != null;

	// the engine started at a_asked still runs (by its number and its start time: a number Windows gave out again is not it)
	static bool EngineAlive(int? a_pid, DateTime a_asked)
	{
		if (a_pid is not { } pid) {
			return false;
		}
		try {
			using var p = Process.GetProcessById(pid);
			return !p.HasExited && p.StartTime >= a_asked.AddSeconds(-5);
		} catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) {
			return false;
		}
	}

	// after Start: the run's own record appears (it started), or the engine's refusal (shown with its reason), or nothing
	void FollowStart(List<Journal> a_runs)
	{
		if (_startAsked is not { } asked) {
			return;
		}
		if (a_runs.FirstOrDefault(j => j.Started >= asked.AddSeconds(-5)) is { } fresh) {
			Say(fresh.Unfinished ? "Started." : fresh.Verdict, fresh.State is RunState.Failed ? "bad" : "good");
			_startAsked = null;
		} else if (Engine.LastRefusal(asked) is { } why) {
			// the engine's own words begin "not started: " already
			Say("Not started: " + WithTodo(why.StartsWith("not started: ", StringComparison.Ordinal) ? why["not started: ".Length..] : why), "bad");
			_startAsked = null;
		} else if (DateTime.Now - asked > TimeSpan.FromSeconds(90) && (!EngineAlive(_startPid, asked) || DateTime.Now - asked > TimeSpan.FromMinutes(15))) {
			Say("The run did not start and gave no reason - nothing was changed. Press Check (F5) to see what is wrong.", "bad");
			_startAsked = null;
		}
	}

	// a refusal with what to do about it, when the catalog knows
	static string WithTodo(string a_why) => Explain.For("", a_why) is { } a ? $"{a_why}. What to do: {a.Todo}" : a_why;

	// the last run again, from the step it stopped at, to where it ended: the very steps previewed are the ones started
	Task RunRetry()
	{
		if (_run == null || Retry.Request(_run, AppPaths.Data) is not { } req) {
			return Task.CompletedTask;
		}
		return StartWith(null, req.From, req.Until);
	}

	// several setups one after another (the queue box picked them; the window follows each run as it goes). Each setup's
	// preview is shown first, in one box, unless every one of them has the preview switched off.
	public async Task StartQueue(List<string> a_files, bool a_keepGoing)
	{
		if (_previewing) {
			return;
		}
		if (CannotStartAny() is { } why) {
			Say(why, "bad");
			return;
		}
		// each setup of the queue that has never run: its tools first, as before a single run
		_previewing = true;
		try {
			foreach (var f in a_files) {
				if (!await ToolsFirst(f, () => Setup.Parse(Files.ReadShared(f), f), CannotStartAny)) {
					return;
				}
			}
		} finally {
			_previewing = false;
		}
		// the queue is always asked about: its order and its rule, and each setup's preview unless that setup has it switched off;
		// what was shown is stamped (each setup file's bytes, each profile's lists) and started only if nothing changed meanwhile
		_previewing = true;
		Say("Working out what the queue will do ...", "");
		string text;
		Func<string> stamp;
		try {
			(text, stamp) = await Task.Run(() => {
				var parts = new List<string>();
				var profiles = new List<string>();
				foreach (var (f, i) in a_files.Select((f, i) => (f, i))) {
					var setup = Setup.Parse(Files.ReadShared(f), f);
					var mo2 = Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance);
					profiles.Add(mo2.ProfileDir(setup.Profile));
					var head = $"{i + 1}. {setup.Name}";
					if (Setup.Bool(setup.Root, "previewBeforeRun") == false) {
						parts.Add(head);
						continue;
					}
					var usual = Engine.UsualTimes(f);
					parts.Add(head + "\n" + string.Join("\n", RunPreview.Build(setup, mo2, Runner.Plan(setup, null, null, null), DryRun.LastOf(setup, mo2, AppPaths.Data)).Lines(k => usual.TryGetValue(k, out var t) ? t : null)));
				}
				string Stamp() => string.Join("|", a_files.Select(f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Files.ReadShared(f))))
					.Concat(profiles.Distinct(StringComparer.OrdinalIgnoreCase).Select(ProfileStamp)));
				return (string.Join("\n\n", parts) + "\n\n" + (a_keepGoing ? "A failed run is skipped and the next setup starts." : "The queue ends at the first run that does not finish."),
					(Func<string>)Stamp);
			});
		} catch (Exception e) {
			Say("Could not work out what the queue would do: " + e.Message, "bad");
			return;
		} finally {
			_previewing = false;
		}
		string before;
		try {
			before = await Task.Run(stamp);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			Say("Could not read a setup of the queue: " + e.Message, "bad");
			return;
		}
		if (ConfirmRun == null || !ConfirmRun("What this queue will do", text, "Start the queue")) {
			Say("Not started - nothing was changed.", "");
			return;
		}
		string after;
		try {
			after = await Task.Run(stamp);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			after = "unreadable";
		}
		if (after != before) {
			Say("Not started: a setup or its profile changed while the box was open - press Queue again to see the new one.", "warn");
			return;
		}
		if (CannotStartAny() is { } now) {
			Say(now, "bad");
			return;
		}
		_queueShownSince = DateTime.Now;
		var (ok, started) = Engine.StartQueue(a_files, a_keepGoing);
		if (!ok) {
			Say("Could not start the queue: " + started, "bad");
			return;
		}
		Say($"Queue of {a_files.Count} setups starting - checking the first one ...", "");
		_startAsked = DateTime.Now;
		_startPid = int.TryParse(started, System.Globalization.NumberStyles.Integer, InvariantCulture, out var queuePid) ? queuePid : null;
		_run = null;
		WantsLook?.Invoke();
	}

	void StopAfter()
	{
		if (Engine.Active() is { } j) {
			if (Send(j, "stop after")) {
				Say("It will stop when this step is done, and put your profile back.", "warn");
			}
		} else {
			Say("No run is going.", "");
		}
	}

	void StopNow()
	{
		if (Engine.Active() is { } j) {
			if (Send(j, "stop")) {
				Say("Stopping now: the tool is closed and your profile put back.", "warn");
			}
		} else {
			Say("No run is going.", "");
		}
	}

	void Continue()
	{
		if (Engine.Active() is { } j) {
			if (Send(j, "continue")) {
				Waiting = "";
			}
		} else {
			Say("No run is waiting for you.", "");
		}
	}

	// the run reads its commands from a file in its folder; one that could not be written there is said, to press again
	bool Send(Journal a_run, string a_command)
	{
		if (TrySend(a_run.Folder, a_command) is { } why) {
			Say($"The run did not get \"{a_command}\": {why} - press it again.", "bad");
			return false;
		}
		return true;
	}

	// null when the command is in the run's folder, else why not
	public static string? TrySend(string a_runFolder, string a_command)
	{
		try {
			Commands.Send(a_runFolder, a_command);
			return null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return e.Message;
		}
	}

	// a web page in the player's browser; null when it opened, else why not
	public static string? OpenPage(string a_url)
	{
		try {
			Process.Start(new ProcessStartInfo(a_url) { UseShellExecute = true })?.Dispose();
			return null;
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			return e.Message;
		}
	}

	public bool Restoring { get; private set => Set(ref field, value); }

	async Task Restore()
	{
		var j = Engine.Interrupted();
		if (j == null || Restoring) {
			return;
		}
		Restoring = true;
		Say("Putting your profile back ...", "warn");
		using var hand = Working("your profile is being put back");
		// its own copy: the records the window reads are shared with the next look
		try {
			var r = await Task.Run(() => Runner.Restore(Journal.Load(j.Folder)));
			Say(r.Verdict, r.State == RunState.Restored ? "good" : "bad");
		} catch (Exception e) {
			Say("Could not finish putting it back: " + e.Message + " - press the button again once the file is free.", "bad");
		} finally {
			Restoring = false;
		}
		Poll();
		LoadHistory();
	}

	// his checklist 454: the setup's last run undone, from the History drawer (its own copy of the record, as Restore)
	public async Task Undo(string a_folder)
	{
		if (Restoring) {
			return;
		}
		Restoring = true;
		Say("Undoing the run ...", "warn");
		using var hand = Working("the run is being undone");
		try {
			var r = await Task.Run(() => Runner.Undo(Journal.Load(a_folder))).ConfigureAwait(true);
			Say(r.Verdict, r.State == RunState.Restored ? "good" : "bad");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) {
			Say("The run could not be undone: " + e.Message, "bad");
		} finally {
			Restoring = false;
		}
		Poll();
		LoadHistory();
		_ = LoadLastRuns();
	}

	bool _checking;

	async Task Check()
	{
		if (_checking || Setup == null) {
			return;
		}
		_checking = true;
		Issues.Clear();
		Say("Checking every tool and setting ...", "");
		// the setup as it stands at this moment, for the check's own thread, and the count of its changes: the setup shown can
		// be changed while the check reads (a step set to use its old output during a check of that very tool)
		var setup = Setup.Clone();
		var edits = _setupEdits;
		try {
			var found = await Task.Run(() => Preflight.Run(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), setup.EnabledSteps.Select(s => s.Key).ToList()));
			// another setup was picked while this one was being checked, or this one was changed: the findings are for a setup
			// that is no longer the one shown, and "checked" is not true of it either (a finding that stops a tool which no
			// longer runs stood among the findings of the setup as changed)
			if (edits != _setupEdits || !StillShown(setup.File, Setup?.File)) {
				return;
			}
			foreach (var i in found) {
				var todo = i.Blocking && Explain.For(i.Step, i.Text) is { } a ? "\n      What to do: " + a.Todo : "";
				Issues.Add($"{(i.Blocking ? Theme.Glyph("IssueBlocks") : Theme.Glyph("IssueNote"))}  {(i.Step == "" ? "" : StepCatalog.Name(i.Step) + ": ")}{i.Text}{todo}");
			}
			var blocks = found.Count(i => i.Blocking);
			_checkedClean = blocks == 0;
			ShowGuide();
			Say(blocks > 0 ? $"{blocks} thing(s) would stop the run." : found.Count > 0 ? "Ready - with notes." : "Ready - everything checks out.", blocks > 0 ? "bad" : "good");
			// his list of 2026-11-16: the check also looks for updates - each tool's and the program's own. Their pages are
			// asked only on this press; a setup can switch it off (Settings > Tools)
			if (!App.Headless && ToolRunner.Setup.Bool(setup.Root, "checkUpdates") != false) {
				await LookForUpdates(setup, edits);
			}
		} catch (Exception e) {
			Say("The check could not finish: " + e.Message, "bad");
		} finally {
			_checking = false;
		}
	}

	// The tools of the setup and the program itself, asked for a newer version. Each newer one is a line among the check's
	// findings, with where to get it: a tool is installed by hand from its own page (the runner replaces no other author's
	// program on its own), and its settings are learned again after. A look that fails is one plain line, never an error.
	// a_edits: the count of the shown setup's changes when its check began (a_setup is that setup as it stood then)
	async Task LookForUpdates(Setup a_setup, int a_edits)
	{
		List<ToolUpdates.Result>? res = null;
		var failed = "";
		try {
			res = await Task.Run(async () => await ToolUpdates.Check(a_setup, Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance), CancellationToken.None)).ConfigureAwait(true);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or System.Net.Http.HttpRequestException or TaskCanceledException) {
			failed = e.Message;
		}
		// another setup was picked while this one's pages were asked, or this one was changed: neither what they said nor
		// that they could not be asked belongs among the findings of the setup shown now
		if (a_edits != _setupEdits || !StillShown(a_setup.File, Setup?.File)) {
			return;
		}
		if (res == null) {
			Issues.Add($"{Theme.Glyph("IssueNote")}  Updates: the tools' pages could not be asked ({failed})");
			return;
		}
		foreach (var line in UpdateLines(res)) {
			Issues.Add($"{Theme.Glyph("IssueNote")}  {line}");
		}
		await LookForNewVersion(MainWindow.AppVersion).ConfigureAwait(true);
	}

	// Is the setup something was asked or edited for still the one the window shows? (by its file: the same setup read
	// again is the same setup)
	public static bool StillShown(string a_askedFor, string? a_shownNow) => string.Equals(a_askedFor, a_shownNow, StringComparison.OrdinalIgnoreCase);

	// what the check says about the tools' versions: one line per tool with a newer version, else one line for all
	public static List<string> UpdateLines(IReadOnlyCollection<ToolUpdates.Result> a_res)
	{
		var lines = a_res.Where(r => r.Newer).Select(r =>
			$"Update: {r.Tool} {r.Newest} is out (you have {r.Yours}). Install it by hand from its page (Settings > Tools > Check its tools for updates opens it), then learn its settings again.").ToList();
		if (lines.Count == 0 && a_res.Count > 0) {
			var unasked = a_res.Where(r => r.Problem != null).Sum(r => r.Tools);
			lines.Add(unasked > 0 ? $"Updates: {unasked} tool(s) could not be checked for a newer version; the others are up to date." : "Updates: every tool is up to date.");
		}
		return lines;
	}

	// a step switched on / off for the setup (saved at once)
	// a start asked for and not yet in its run record counts too: the engine reads the setup in that gap
	public bool SetupInUse => Setup != null && (_startAsked != null || (Engine.Active() is { } j && string.Equals(j.SetupFile, Setup.File, StringComparison.OrdinalIgnoreCase)));

	public async Task SetOn(StepRow a_row, bool a_on)
	{
		if (Setup is not { } setup || setup.Root["steps"] is not JsonArray arr) {
			return;
		}
		if (SetupInUse || Saving || Renaming || MovingData) {
			a_row.On = !a_on;
			Say(SetupInUse ? "Not changed: a run is using this setup right now." : NotYetSaved("changed")!, "bad");
			return;
		}
		var mine = arr.OfType<JsonObject>().Where(o => Setup.Str(o, "step") == a_row.Key).ToList();
		var before = mine.Select(o => o["on"]?.DeepClone()).ToList();
		foreach (var o in mine) {
			o["on"] = a_on;
		}
		if (!await SaveSetup()) {
			// the file holds the step as it was: so does the setup shown again, and its row
			for (var i = 0; i < mine.Count; ++i) {
				if (before[i] != null) {
					mine[i]["on"] = before[i];
				} else {
					mine[i].Remove("on");
				}
			}
			if (Steps.Contains(a_row)) {
				a_row.On = !a_on;
			}
			return;
		}
		if (!ReferenceEquals(Setup, setup)) {
			return;
		}
		// (a step that is off uses nothing: its "uses its old output" line goes with the tick and comes back with it)
		MarkLeftOut();
		Estimate();
	}

	// the order the setup would have after moving a step; the warnings it would break (empty = fine)
	public List<string> WarningsIfMoved(StepRow a_row, int a_delta) => WarningsIfMovedTo(a_row, Steps.IndexOf(a_row) + a_delta);

	// the same for a step put at any place (a_to: its place in the list afterwards) - a step dragged there
	public List<string> WarningsIfMovedTo(StepRow a_row, int a_to)
	{
		// each row keeps its own tick: a step listed twice is two rows, and the moved one is this very row
		var rows = Steps.Select(s => (s.Key, s.On)).ToList();
		var i = Steps.IndexOf(a_row);
		if (i < 0 || a_to < 0 || a_to >= rows.Count || a_to == i) {
			return [];
		}
		var before = StepCatalog.OrderWarnings(rows.Where(r => r.On).Select(r => r.Key).ToList()).ToHashSet();
		var moved = rows[i];
		rows.RemoveAt(i);
		rows.Insert(a_to, moved);
		var on = rows.Where(r => r.On).Select(r => r.Key).ToList();
		return StepCatalog.OrderWarnings(on).Where(w => !before.Contains(w)).ToList();
	}

	public Task Move(StepRow a_row, int a_delta) => MoveTo(a_row, Steps.IndexOf(a_row) + a_delta);

	public async Task MoveTo(StepRow a_row, int a_to)
	{
		if (Setup is not { } setup || setup.Root["steps"] is not JsonArray arr) {
			return;
		}
		if (SetupInUse || Saving || Renaming || MovingData) {
			Say(SetupInUse ? "Not moved: a run is using this setup right now." : NotYetSaved("moved")!, "bad");
			return;
		}
		var i = Steps.IndexOf(a_row);
		var j = a_to;
		if (i < 0 || j < 0 || j >= Steps.Count || j == i) {
			return;
		}
		// the file's own places of the rows, in the list's order (the list shows exactly the entries that name a step; one that
		// names none keeps its place, and a step listed twice moves the very entry its row shows)
		var items = arr.ToList();
		var shown = Enumerable.Range(0, items.Count).Where(x => items[x] is JsonObject o && Setup.Str(o, "step") != null).ToList();
		if (shown.Count != Steps.Count) {
			return;
		}
		// the step entries in their new order, written back into the very places step entries stood in
		var entries = shown.Select(x => items[x]).ToList();
		var entry = entries[i];
		entries.RemoveAt(i);
		entries.Insert(j, entry);
		for (var k = 0; k < shown.Count; ++k) {
			items[shown[k]] = entries[k];
		}
		// the setup's own order as it stood, to go back to when the new one cannot be saved
		var asItWas = arr.ToList();
		arr.Clear();
		foreach (var it in items) {
			arr.Add(it);
		}
		if (!await SaveSetup()) {
			// The file holds the old order, and a run reads the file: the setup shown goes back to it and the list stays
			// as it is drawn - never a list in one order over a file in another
			arr.Clear();
			foreach (var it in asItWas) {
				arr.Add(it);
			}
			return;
		}
		// the rows move once the new order is in the file (another setup picked meanwhile shows its own rows: nothing moves)
		if (!ReferenceEquals(Setup, setup) || Steps.IndexOf(a_row) != i || j >= Steps.Count) {
			return;
		}
		Steps.Move(i, j);
		var n = 0;
		foreach (var s in Steps) {
			s.Number = ++n;
		}
		SelectedStep = a_row;
	}

	// a change to the setup shown is on its way into its file (the next change waits for it: NotNow)
	public bool Saving { get; private set; }

	// the setup shown is being renamed: its file moves, and no change or save may land in the old one meanwhile (CodeRabbit on
	// pull request #38) - held from before the last save is waited for until the renamed setup is shown
	public bool Renaming { get; set; }

	// the program's data folder is being moved (Settings > Keep data through updates): no setup is written or started meanwhile,
	// so nothing lands in the folder being copied
	public bool MovingData { get; set; }

	// a setup is being copied: the data move waits for it, as for a setup's own save
	public bool CopyingSetup { get; set; }

	// a shared copy of a setup is being written into the data folder: the data move waits for it too
	public bool SharingSetup { get; set; }

	// why a setup may not be written or started at this moment (null: it may)
	public string? WhyNotWriting(string a_done) => NotYetSaved(a_done);

	// The write of a setup that is under way (finished when none is). A window that closes waits for it, and so does the
	// program's end: a change the window took is in the file before the program is gone
	public static Task PendingSetupSave { get; private set; } = Task.CompletedTask;

	// A setup's text written to its file, with the data's write lock held: a move of the data folder by this or another copy
	// of the program finishes first, and a write after it - into a folder that moved, or is gone - is refused, never made in
	// the old place. Null when written; else why not
	public static string? WriteSetup(string a_data, string a_file, string a_text, string a_copies)
	{
		using var writes = DataFolder.HoldWrites();
		return DataFolder.WhyNotWritable(a_data) ?? Setup.WriteKeepingACopy(a_file, a_text, a_copies);
	}

	// The setup shown, written to its file - away from the window's own thread, where the copy into "Setup backups", the
	// tidying of older copies and the write itself held up every click on a slow disk. Its text is made here (memory
	// only); one save at a time, and while one is on its way the setup is not changed again, so what is shown and what is
	// being written never part ways. Says how it went; true when the file holds the setup
	public async Task<bool> SaveSetup()
	{
		if (Setup is not { } setup) {
			return false;
		}
		if (NotYetSaved("saved") is { } notNow) {
			Say(notNow, "bad");
			return false;
		}
		if (Engine.Active() is { } j && string.Equals(j.SetupFile, setup.File, StringComparison.OrdinalIgnoreCase)) {
			Say("Not saved: a run is using this setup right now.", "bad");
			return false;
		}
		// what a check that is under way finds is for the setup as it was before this change
		++_setupEdits;
		var (file, text) = (setup.File, setup.Text());
		var copies = Path.Combine(AppPaths.Data, "Setup backups");
		Saving = true;
		string? why;
		try {
			// (with the data's write lock: a move of the data folder by this or another copy of the program finishes first, and a
			// save after it would land in a folder that is no longer the data's)
			var data = AppPaths.Data;
			var write = Task.Run(() => WriteSetup(data, file, text, copies));
			PendingSetupSave = write;
			why = await write;
		} finally {
			Saving = false;
		}
		Say(why == null ? "Saved." : "Not saved: " + why, why == null ? "good" : "bad");
		if (why != null) {
			// The caller takes the change back out of the setup shown. A check that began while the change stood in memory
			// read a setup that is now neither shown nor saved: its findings are not this setup's either
			++_setupEdits;
		} else if (!ReferenceEquals(Setup, setup) && StillShown(file, Setup?.File)) {
			// This setup was picked away and picked again while it was being written: what is shown was read from the file as
			// it stood before the write. Read again, with what was saved - shown stale, the next change would have written
			// the old values back over this one
			LoadSteps();
		}
		return why == null;
	}

	// each step's usual minutes (the old updater's own logs, 30 Sep - 26 Oct 2026) - the made-up run of the pictures only
	internal static readonly Dictionary<string, double> DemoMinutes = new() {
		["shaders"] = 28, ["cleansave"] = 7.5, ["synthesis"] = 1.3, ["landscapefixer"] = 9, ["pgpatcher"] = 4,
		["xlodgen"] = 17.5, ["acmos"] = 1.5, ["grass"] = 0.8, ["treelodgen"] = 2.3, ["texgen"] = 5.5, ["dyndolod"] = 24, ["anvillod"] = 24,
		["pandora"] = 0.5, ["playsave"] = 7,
	};

	// a made-up run for the off-screen pictures (--snapshot, --page): a guided in-game step, or a run in progress that a_frame
	// moves on a minute at a time (xLODGen working; from frame 6 it is done and ACMOS works)
	public void Demo(bool a_waiting, int a_frame = 0)
	{
		IsRunning = true;
		Message = "";
		ClearLastRuns();
		var current = a_waiting ? "cleansave" : a_frame >= 6 ? "acmos" : "xlodgen";
		// a setup without that step shows nothing running (never another step under its text)
		var cur = Steps.ToList().FindIndex(s => s.Key == current);
		double Usual(StepRow a_s) => DemoMinutes.GetValueOrDefault(a_s.Key, 5);
		var start = new DateTime(2026, 10, 27, 21, 2, 10);
		var log = new List<string>();
		void Line(double a_min, string a_text) => log.Add($"{start.AddMinutes(a_min).ToString("HH:mm:ss", InvariantCulture)}  {a_text}");
		Line(0, $"Tool Runner - {SelectedSetup?.Name} (profile {Setup?.Profile}), {Steps.Count(s => s.On)} steps");
		Line(0.1, "every output switched off - each back on once its tool rebuilt it");
		double spent = 0.2, now = 0, left = 0;
		for (var i = 0; i < Steps.Count; i++) {
			var s = Steps[i];
			s.Usual = Short(TimeSpan.FromMinutes(Usual(s)));
			s.Note = "";
			s.Elapsed = "";
			if (s.Key == "shaders") {
				s.State = StepState.Skipped;
				s.Note = "skipped: the shader mods and their settings did not change";
				Line(spent, "=== Shader caches === skipped: the shader mods did not change");
			} else if (i < cur) {
				var took = s.Key == "xlodgen" ? 17.2 : Usual(s);
				s.State = StepState.Done;
				s.Elapsed = Short(TimeSpan.FromMinutes(took));
				Line(spent, $"=== {s.Name} ===");
				if (s.Key == "cleansave") {
					// a warning as the log shows one (the pictures show its color)
					Line(spent + 2.1, RunLog.MarkOf(Level.Warn) + "your followers could not be read before the teleport: DevBench did not answer in time");
				}
				spent += took;
				Line(spent, $"=== {s.Name} done in {took.ToString("0.0", InvariantCulture)} min ===");
			} else if (i == cur) {
				now = a_waiting ? 1.2 : current == "xlodgen" ? 9 + a_frame : 0.3 + (a_frame - 6) * 0.5;
				s.State = StepState.Running;
				s.Elapsed = Short(TimeSpan.FromMinutes(now));
				Line(spent, $"=== {(a_waiting ? "the clean save, first half: to QASmoke with the profile as it is" : s.Name)} ===");
				if (current == "xlodgen") {
					Line(spent + 0.2, "    running xLODGenx64 through Mod Organizer");
					Line(spent + 0.8, "    the worldspace list: 17 worldspace(s) offered");
					Line(spent + 0.9, "    worldspaces: 17 of 17 ticked");
				}
				left += Math.Max(0.5, Usual(s) - now);
				spent += now;
			} else {
				s.State = StepState.Pending;
				left += s.On ? Usual(s) : 0;
			}
		}
		NowTitle = cur >= 0 ? Steps[cur].Name : "Getting ready";
		Waiting = a_waiting ? "Load your newest save, wait until you can move, then press Done here." : "";
		NowActivity = a_waiting || cur < 0 ? "" : current == "xlodgen" ? "generating terrain LOD" : "painting roads into the terrain LOD";
		ProgressKnown = !a_waiting && cur >= 0;
		Progress = current == "xlodgen" ? Math.Min(0.97, now / 17.5) : Math.Min(0.95, now / 1.5);
		Clock = Long(TimeSpan.FromMinutes(spent));
		Left = $"about {Long(TimeSpan.FromMinutes(left))} left";
		FinishAt = DoneAbout(new DateTime(2026, 10, 27, 20, 0, 0, DateTimeKind.Local).AddMinutes(spent + left));
		Verdict = "";
		Mo2Open = a_waiting || current == "xlodgen";
		GameRunning = a_waiting;
		Summary = null;
		RetryFrom = "";
		WaitingInGame = true;
		StepOf = cur >= 0 ? $"step {cur + 1} of {Steps.Count}" : "";
		Guide = [];
		ShowClose();
		LogChanged?.Invoke(string.Join("\n", log) + "\n", true);
	}

	// the off-screen pictures: the made-up run waits on the player's own Mod Organizer (no Done to press: it goes on by itself)
	public void DemoWaitsOutside()
	{
		Demo(false);
		WaitingInGame = false;
		Waiting = "Mod Organizer is open and you are using it - the run goes on by itself once you close it.";
		NowActivity = "";
	}

	// the off-screen pictures: the made-up run ended - finished, or stopped by a problem at DynDOLOD - with its summary
	public void DemoEnded(bool a_failed)
	{
		Demo(false, 9);
		var start = new DateTime(2026, 10, 27, 21, 2, 10);
		var at = start;
		var j = new Journal { SetupName = SelectedSetup?.Name ?? "", Started = start, State = a_failed ? RunState.Failed : RunState.Finished, BackupDir = @"D:\Backups\2026-10-27 21-02-10 " + (SelectedSetup?.Name ?? "") };
		foreach (var s in Steps.Where(s => s.On)) {
			var failedHere = a_failed && s.Key == "dyndolod";
			var after = a_failed && j.Steps.Any(r => r.State == StepState.Failed);
			var took = TimeSpan.FromMinutes(DemoMinutes.GetValueOrDefault(s.Key, 5) * (failedHere ? 0.4 : 1));
			var output = s.Key is "shaders" or "cleansave" or "playsave" ? null : s.Name + " Output";
			j.Steps.Add(new StepRecord { Key = s.Key, Name = s.Name, State = after ? StepState.Skipped : failedHere ? StepState.Failed : StepState.Done, Started = after ? null : at,
				Ended = after ? null : at + took, Output = output, OutputBytes = after || failedHere || output == null ? null : (long)(DemoMinutes.GetValueOrDefault(s.Key, 5) * 0.9 * (1L << 30)),
				Note = failedHere ? "DynDOLOD's window said: Out of memory" : "" });
			if (!after && !failedHere && output != null) {
				j.Built.Add(output);
				j.Parked.Add(new ParkRecord { Step = s.Key, Mod = output, Replaced = true });
			}
			if (!after) {
				at += took;
			}
			s.State = j.Steps[^1].State;
			s.Elapsed = after ? "" : Short(took);
			s.Note = j.Steps[^1].Note;
		}
		j.Ended = at;
		j.Verdict = a_failed ? "stopped at DynDOLOD: DynDOLOD's window said: Out of memory - the profile is back as it was, with this run's finished outputs on"
			: $"finished: {j.Steps.Count} step(s) in {((int)(at - start).TotalMinutes).ToString(InvariantCulture)} min";
		IsRunning = false;
		NowTitle = a_failed ? "Stopped by a problem" : "Finished";
		NowActivity = "";
		Waiting = "";
		StepOf = "";
		Clock = Long(at - start);
		Left = FinishAt = "";
		Verdict = j.Verdict;
		VerdictKind = a_failed ? "bad" : "good";
		RetryFrom = a_failed ? "dyndolod" : "";
		Summary = RunSummary.Of(j);
		Mo2Open = GameRunning = false;
		Guide = [];
		ShowClose();
		// written as the run's own log writes it: each line after its time, with its mark
		LogChanged?.Invoke(string.Join("\n", RunSummary.Of(j).Marked().Select(l => $"{at:HH:mm}  {RunLog.MarkOf(l.Level)}{l.Text}")) + "\n", false);
	}

	// the off-screen pictures: the window as a first start shows it (no setup made yet)
	public void DemoFirstStart()
	{
		DemoReady();
		Steps.Clear();
		EstimateText = "";
		Summary = null;
		RetryFrom = "";
		NowActivity = "Make a setup first (New setup).";
		Guide = FirstSteps(false, false, false);
	}

	// the page's pictures (--page): the dashboard with real-looking usual times, nothing running
	public void DemoReady()
	{
		_usual = Steps.ToDictionary(s => s.Key, s => TimeSpan.FromMinutes(DemoMinutes.GetValueOrDefault(s.Key, 5)));
		ClearLastRuns();
		foreach (var s in Steps) {
			s.Usual = Short(_usual[s.Key]);
			s.State = null;
			s.Elapsed = "";
			s.Note = "";
		}
		Estimate();
		ShowReady();
		Waiting = "";
		Clock = Left = FinishAt = Verdict = "";
		Progress = 0;
		Mo2Open = false;
		GameRunning = false;
		Message = "";
		LogChanged?.Invoke("", true);
	}

	// the off-screen pictures changed a row's "uses its old output" by hand: the button's words and the estimate follow
	public void DemoRefreshOld()
	{
		OnPropertyChanged(nameof(UseOldLabel));
		Estimate();
		ShowReady();
	}

	// the made-up runs of the pictures carry no last runs (a load still going is dropped)
	void ClearLastRuns()
	{
		++_lastLoad;
		_lastDetail = [];
		_outOfDate = "";
		foreach (var s in Steps) {
			s.LastRun = "";
			s.LastKind = "";
		}
		ShowLastCard();
	}

	// the page's pictures (--page): past runs of a made-up setup; their logs are written by the caller into each Folder
	public void DemoHistory(IEnumerable<HistoryRow> a_rows)
	{
		History.Clear();
		foreach (var r in a_rows) {
			History.Add(r);
		}
	}

	public static void OpenFolder(string a_dir)
	{
		if (Directory.Exists(a_dir)) {
			Process.Start(new ProcessStartInfo("explorer.exe", "\"" + a_dir + "\"") { UseShellExecute = true })?.Dispose();
		}
	}
}
