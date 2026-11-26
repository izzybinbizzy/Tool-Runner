using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner;

// What a step works with during a run.
public sealed class RunContext
{
	public required Setup Setup { get; init; }
	public required Mo2Instance Mo2 { get; init; }
	public required Profile Profile { get; init; }
	public required Mo2Control Control { get; init; }
	public required RunLog Log { get; init; }
	// the game's own words for its load boxes, read the first time a box is not in English
	public GameWords? GameWords { get; set; }
	// the settings files the small, muted game window changed for this step
	public List<string> QuietFiles { get; } = [];
	public required Journal Journal { get; init; }
	public required CancellationToken Cancel { get; init; }
	public required string BackupFolder { get; init; }
	public IGuide Guide { get; init; } = new NoGuide();
	public DevBench DevBench { get; init; } = new();
	// the run's watch for boxes (none when a step is run alone, as the offline tests do)
	public BoxWatch? Boxes { get; init; }
	// the box nobody here could answer that ended the game of the step in hand, in words
	public string? BlockedBy => Boxes?.Blocked;

	// One look at the game's own message box at a time (InGame.LookAtGameBox): the run's watch and the step in hand both
	// look. GameBoxAnswered: the box last answered and how often in a row; DevBenchGame: the run's game DevBench was last
	// found to answer for (0: none yet).
	public Lock GameBoxGate { get; } = new();
	public (string Body, int Times) GameBoxAnswered { get; set; } = ("", 0);
	public int DevBenchGame { get; set; }

	// What the watch leaves for the run's own thread: the texts of the game's boxes it answered (the step in hand takes them:
	// a box that says another follows holds its settle), and lines for the run's notes (only the run's thread writes the
	// run's record).
	readonly Lock _fromWatch = new();
	readonly Queue<string> _boxesAnswered = new();
	readonly List<string> _notesToAdd = [];

	public void BoxAnswered(string? a_body, string? a_note)
	{
		lock (_fromWatch) {
			if (a_body != null) {
				_boxesAnswered.Enqueue(a_body);
				// (a step that never asks leaves them here: only the newest few are kept)
				while (_boxesAnswered.Count > 20) {
					_boxesAnswered.Dequeue();
				}
			}
			if (a_note != null && !_notesToAdd.Contains(a_note)) {
				_notesToAdd.Add(a_note);
			}
		}
	}

	public string? TakeAnsweredBox()
	{
		lock (_fromWatch) {
			return _boxesAnswered.TryDequeue(out var body) ? body : null;
		}
	}

	// the notes left so far go into the run's record - called by the run's own thread, after each step and at the run's end
	public void AddWatchNotes()
	{
		List<string> notes;
		lock (_fromWatch) {
			notes = [.. _notesToAdd];
			_notesToAdd.Clear();
		}
		notes.RemoveAll(Journal.Notes.Contains);
		if (notes.Count > 0) {
			Journal.Notes.AddRange(notes);
			Journal.TrySave();
		}
	}

	// outputs to tick once the step's mods are put back (applied by the runner after each step)
	public List<string> PendingEnable { get; } = [];
	public string? CleanSaveName { get; set; }

	// when this run last started its game: a crash log is that start's only if written after it (an earlier start's crash, retried
	// since, is never named as the cause of a later close). Set where a game start begins - and with it the texts of the
	// boxes the watch answered during an earlier start are dropped: left there, each one cost the next start's load a wait
	// for a box that was long gone. What the watch answers from here on is this start's
	public DateTime? GameLaunched
	{
		get;
		set {
			field = value;
			lock (_fromWatch) {
				_boxesAnswered.Clear();
			}
		}
	}
	public Dictionary<string, string> CleanSaveExtraOff { get; set; } = [];
	public string Mo2Dir => Path.GetDirectoryName(Mo2.Exe)!;

	// the step in hand starts the game with a mod set to list the game's files anew (Crdw.Apply): when it finishes, that list is current
	public bool FreshList { get; set; }

	// the step in hand put its mods back as they were after such a list was made (Toggle), or the player switched a mod
	// while it ran: the list is older than that again
	public bool SwitchedBack { get; set; }

	// The player's own switches in Mod Organizer that the run has counted as a change to the game's files so far (mod ->
	// how they left it). True when a_theirs - their switches as a step finds them - is not what was counted: one is new,
	// stands otherwise, or was taken back. The same switch, met again at every later step, is counted once.
	readonly Dictionary<string, char> _theirsCounted = new(StringComparer.OrdinalIgnoreCase);

	public bool CountTheirs(IReadOnlyDictionary<string, char> a_theirs)
	{
		if (a_theirs.Count == _theirsCounted.Count && a_theirs.All(p => _theirsCounted.TryGetValue(p.Key, out var was) && was == p.Value)) {
			return false;
		}
		_theirsCounted.Clear();
		foreach (var (mod, state) in a_theirs) {
			_theirsCounted[mod] = state;
		}
		return true;
	}

	// After a put-back: the mods whose line in the mod list the run had written and the put-back could not take out again
	// (the list was held by another program). The profile is not whole back while one is left: the put-back stays on offer
	public List<string> LinesLeft { get; } = [];

	// After a put-back: the ticked mods whose folder would not be read, so the player's plugin ticks of them could not all be
	// set again. The put-back stays on offer while one is left (the marks it needs are kept in the run's record)
	public List<string> MarksLeft { get; } = [];

	public void SetAfter(params string[] a_mods) => PendingEnable.AddRange(a_mods);

	// one output of a step that makes several is complete: on record as built at once, so it stays - and is switched on at the
	// put-back - even when a later part of the same step fails or is stopped (a step's failure used to lose its finished part)
	public void Finished(string a_mod)
	{
		// "built" and "its old output stays in the backup" go on record in ONE write (MarkReplaced saves): in two, a crash
		// between them left a finished output that no record called this run's - never switched on, its old one never back
		if (!Journal.Built.Contains(a_mod, StringComparer.OrdinalIgnoreCase)) {
			Journal.Built.Add(a_mod);
		}
		Steps.StepKit.MarkReplaced(this, a_mod);
		SetAfter(a_mod);
	}

	// a step's own wait (a tool settling, a pause between two tries): a Stop ends it at once - never Thread.Sleep in a step
	public void Pause(TimeSpan a_for) => Waits.Pause(a_for, Cancel);

	// what a step does now, for the window: saved at most once a second (a command-line tool prints thousands of lines); a
	// "WAITING FOR YOU" text always at once. Only the run's own thread calls it.
	public void Activity(string a_step, string a_text, double? a_progress = null)
	{
		var s = Journal.Step(a_step);
		if (s != null && (s.Activity != a_text || s.Progress != a_progress)) {
			s.Activity = a_text;
			s.Progress = a_progress;
			if (DateTime.UtcNow - _lastActivitySave > TimeSpan.FromSeconds(1) || a_text.StartsWith("WAITING", StringComparison.Ordinal)) {
				_lastActivitySave = DateTime.UtcNow;
				Journal.Save();
			}
		}
	}

	DateTime _lastActivitySave = DateTime.MinValue;

	public void Note(string a_text)
	{
		Journal.Notes.Add(a_text);
		Journal.Save();
	}
}

// how a step asks the player to do something by hand (no DevBench): the window shows the text and a Done button
public interface IGuide
{
	// returns when the player pressed Done; throws OperationCanceledException on Stop
	void Ask(string a_step, string a_text, CancellationToken a_ct);
}

public sealed class NoGuide : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct) =>
		throw new StepFailedException("this step needs your hands in the game and nobody can answer here: " + a_text);
}

// the guide over the run's command file: the journal says what is asked, the window writes "continue"
public sealed class FileGuide(Journal a_journal, Func<bool> a_continued, Action a_clear) : IGuide
{
	public void Ask(string a_step, string a_text, CancellationToken a_ct)
	{
		a_clear();
		var s = a_journal.Step(a_step);
		if (s != null) {
			s.Activity = "WAITING FOR YOU: " + a_text;
			a_journal.Save();
		}
		while (!a_continued()) {
			a_ct.ThrowIfCancellationRequested();
			a_ct.WaitHandle.WaitOne(500);
		}
		a_clear();
		if (s != null) {
			s.Activity = "";
			a_journal.Save();
		}
	}
}

// settings of one step with plain fallbacks
public sealed class StepSettings(JsonObject a_o)
{
	public JsonObject Raw { get; } = a_o;
	public string? Str(string a_key) => Setup.Str(Raw, a_key);
	public string Req(string a_key) => Str(a_key) is { Length: > 0 } s ? s : throw new StepFailedException($"the step's setting \"{a_key}\" is not set");
	// A number of a setup file that can come from anywhere (shared, brought in, written by hand): one below zero is no
	// number - the step's own default stands - and a time in whole minutes is one minute at least. The Settings window
	// holds a typed number to the same two rules; a number read past them made a step fail at once on "its -5 minute limit"
	// (the sign is read before the number is made whole: -0.5 would be 0, and as minutes taken for one)
	public int Int(string a_key, int a_default) =>
		Setup.Num(Raw, a_key) is { } d && d >= 0 && Setup.Int(Raw, a_key) is { } n ? (a_key.EndsWith("_min", StringComparison.Ordinal) ? Math.Max(1, n) : n) : a_default;
	// (nor is one past what a whole number holds: as minutes it was more time than there is, and the step ended on that)
	public double Num(string a_key, double a_default) => Setup.Num(Raw, a_key) is { } d && d >= 0 && d <= int.MaxValue ? d : a_default;
	public bool Bool(string a_key, bool a_default) => Setup.Bool(Raw, a_key) ?? a_default;
	public List<string> List(string a_key) => Setup.List(Raw, a_key);
	public string? OutputMod => Str("output_mod") ?? Str("then_move_to_mod");
	// (a setup written by hand may say 0 or less: a step then failed at once on "its -5 minute limit")
	public TimeSpan Deadline(int a_defaultMin) => TimeSpan.FromMinutes(Math.Max(1, Int("deadline_min", a_defaultMin)));
}

// Mods on / off for one step (MO2 closed) and ALWAYS put back afterwards: the order files are saved before and written back
// byte for byte (a tool that regenerates a plugin makes it vanish for a moment, and MO2 would re-add it elsewhere).
public sealed class Toggle : IDisposable
{
	readonly RunContext _ctx;
	readonly Dictionary<string, byte[]> _snap;
	readonly string _label;

	// a_ct: a Stop while it waits to close MO2 (a download, the player's own program) ends the wait
	public Toggle(RunContext a_ctx, IEnumerable<string> a_enable, IEnumerable<string> a_disable, string a_label, CancellationToken a_ct = default)
	{
		_ctx = a_ctx;
		_label = a_label;
		// a Stop already asked for: nothing is switched and nothing started
		a_ct.ThrowIfCancellationRequested();
		var st = a_ctx.Profile.States();
		// (each list gone through once, whatever the caller handed in: one that is worked out as it is read was worked out
		// three times)
		var (enable, disable) = (a_enable.ToList(), a_disable.ToList());
		var en = enable.Where(m => st.ContainsKey(m)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var dis = disable.Where(m => st.ContainsKey(m) && !en.Contains(m, StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var m in enable.Concat(disable).Where(m => !st.ContainsKey(m)).Distinct()) {
			a_ctx.Log.Detail($"(not in this profile, skipped: {m})");
		}
		a_ctx.Control.Close(null, a_ct);
		_snap = a_ctx.Profile.Snapshot();
		_oursBefore = a_ctx.Profile.OursNow();
		if (en.Count > 0 || dis.Count > 0) {
			// A profile that already stands as the step needs it has nothing switched: nothing is said, and nothing counts as
			// changed (a real run's log read "mods for TexGen:" with nothing under it, and counted a change that was none)
			var now = Profile.StatesOf(_snap);
			bool Stands(string a_mod, char a_as) => now.TryGetValue(a_mod, out var flag) && flag == a_as;
			if (en.Any(m => Stands(m, '-')) || dis.Any(m => Stands(m, '+'))) {
				a_ctx.Log.Detail($"mods for {a_label}:");
			}
			Dictionary<string, char> switched;
			try {
				switched = a_ctx.Profile.SetMods(en, dis, a_ctx.Log);
				a_ctx.Profile.ActivatePlugins(en, a_ctx.Setup.PlaceAfter);
			} catch {
				// half switched: the order files back byte for byte at once, as the put-back promises
				a_ctx.Profile.Restore(_snap);
				a_ctx.Profile.OursBack(_oursBefore);
				throw;
			}
			// With these mods switched the game finds other files than its last list names: a game start of this step makes
			// the list anew (Crdw.Apply). Taken back in Dispose when no list was made while they were switched.
			if (switched.Count > 0) {
				_switched = true;
				++a_ctx.Journal.FilesChanged;
			}
		}
	}

	readonly Dictionary<string, char>? _oursBefore;
	readonly bool _switched;

	public void Dispose()
	{
		try {
			_ctx.Control.Close();
		} catch (Mo2BusyException e) {
			_ctx.Log.Error($"could not close Mod Organizer to put the mods back after {_label}: {e.Message}");
			throw;
		}
		// what the player changed in Mod Organizer while the step's switches were on: read before the order
		// files go back, laid back over them after, and still counted as the player's (the expectation goes back to before the
		// step), so the run's final put-back sees and keeps it too
		var theirs = _ctx.Profile.ChangedByOthers();
		var marks = theirs.Count > 0 ? Runner.KeepTheirs(_ctx, theirs).Marks : [];
		try {
			_ctx.Profile.Restore(_snap);
		} catch (Mo2OpenException) {
			// a Mod Organizer the step started a moment ago was not there for the close above and is there now (a Stop seen right
			// after the start left the step FAILED on "Mod Organizer is open"): closed, then once more
			_ctx.Log.Detail("Mod Organizer came up while the mods were being put back - closing it, then putting them back");
			_ctx.Control.Close();
			// looked at again: nothing was put back yet (the refusal comes before the first file is written), and the Mod
			// Organizer that came up may have saved a change of the player's since the look above
			theirs = _ctx.Profile.ChangedByOthers();
			marks = theirs.Count > 0 ? Runner.KeepTheirs(_ctx, theirs).Marks : [];
			_ctx.Profile.Restore(_snap);
		}
		_ctx.Profile.OursBack(_oursBefore);
		if (_switched) {
			if (_ctx.FreshList) {
				// The game made its list of files while this step's mods were switched: with them back as they were, that list
				// names other files than the game now finds. Counted when the step ends, after a finished game step has called
				// its list current (Crdw.StepEnded) - a game step that writes nothing left a list from before the put-back
				// marked current, and no later game start made a new one
				_ctx.SwitchedBack = true;
			} else if (_ctx.Journal.FilesChanged > 0) {
				// no list was made meanwhile, and the mods are as they were before: nothing has changed for the game's list
				--_ctx.Journal.FilesChanged;
			}
		}
		if (theirs.Count > 0) {
			var expected = _ctx.Profile.OursNow();
			_ctx.Log.Detail($"kept what you changed in Mod Organizer during {_label}: " + string.Join(", ", theirs.Select(p => (p.Value == '+' ? "on " : "off ") + p.Key)));
			_ctx.MarksLeft.Clear();
			Runner.ReapplyTheirs(_ctx, theirs, marks);
			_ctx.Profile.OursBack(expected);
			// all set again: what was kept of it goes (kept on, a switch the player undid later would come back from it)
			if (_ctx.MarksLeft.Count == 0) {
				Runner.LetGoOfTheirs(_ctx);
			}
		}
		// What the player switched in Mod Organizer changes the files the game finds, whatever was counted or taken back
		// above for the step's own switches. Each such switch counts once, when it is first met here: by what the run has
		// counted so far - never by this step's own snapshot, which already holds a switch made before the step began.
		if (_ctx.CountTheirs(theirs)) {
			if (_ctx.FreshList) {
				// The game made its list during this step, and when the player switched cannot be told from here: counted
				// when the step ends, after the finished step has called that list current (Crdw.StepEnded) - counted
				// now, it was wiped by that very reset
				_ctx.SwitchedBack = true;
			} else {
				++_ctx.Journal.FilesChanged;
			}
		}
	}
}

// A stopwatch of the time the PC was awake. A plain stopwatch goes on counting through a closed lid or the player's own
// Sleep: a tool twenty minutes into its work then woke to a limit that had "run out" overnight
public sealed class AwakeClock
{
	readonly ulong _start = Native.AwakeTicks();

	public static AwakeClock StartNew() => new();

	public TimeSpan Elapsed => TimeSpan.FromTicks((long)(Native.AwakeTicks() - _start));
}

// waiting on a tool: its own "done" signal (poll), a deadline, and its whole process tree's CPU - a tool that uses no CPU for
// the setup's idle minutes is looked at at once (a finished tool is noticed within 2 minutes)
public static class Waits
{
	public enum Poll
	{
		Wait,
		Done,
	}

	// the one wait of the engine's steps: it ends the moment a Stop is asked, and says so by throwing as every other look at Stop
	// does (a Stop pressed during a wait that could not see it did nothing until that wait was over). The offline test
	// NoStepWaitIsBlindToStop holds the steps to it.
	public static void Pause(TimeSpan a_for, CancellationToken a_ct)
	{
		if (a_for > TimeSpan.Zero) {
			a_ct.WaitHandle.WaitOne(a_for);
		}
		a_ct.ThrowIfCancellationRequested();
	}

	// the offline tests only: a short idle limit
	public static TimeSpan? IdleForTests { get; set; }

	// how often a tool's whole window text is read for the idle rule (the tests shorten it with their idle limit)
	public static TimeSpan ProgressEvery { get; set; } = TimeSpan.FromSeconds(30);

	// How long a tool that has closed is still given to show it finished (its last file written, its output gone quiet) before
	// the step fails as "closed before it finished" - never the rest of its limit, which is an hour for most tools (the offline
	// tests shorten it). Longer than any step's own settling time: PGPatcher's output must be quiet for a minute
	public static TimeSpan GoneGrace { get; set; } = TimeSpan.FromMinutes(3);

	// a_progress: for a tool driven through its window - what its window says; a change counts as work, and so does CPU its CHILD
	// processes use (a long patcher shows nothing new for minutes), never the window process's own (a
	// window drawing itself uses CPU while the tool does nothing - Synthesis sat 4 min on its main screen)
	// a_clock: the step's own clock, when an earlier wait of the step counts against the same limit
	public static void Tool(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline, Func<Poll> a_poll,
		Func<bool>? a_onIdle = null, TimeSpan? a_every = null, Func<string>? a_progress = null, AwakeClock? a_clock = null)
	{
		string? lastSeen = null;
		var every = a_every ?? TimeSpan.FromSeconds(2);
		// time the PC was awake: a closed lid or the player's own Sleep in the middle of a long tool is not time the tool had,
		// and on waking neither its limit nor the idle rule may have run out
		var sw = a_clock ?? AwakeClock.StartNew();
		TimeSpan? goneAt = null;
		var idleLimit = IdleForTests ?? TimeSpan.FromMinutes(Math.Max(1, a_ctx.Setup.IdleMinutes));
		var lastCpu = TimeSpan.Zero;
		IReadOnlyDictionary<int, TimeSpan> lastChildren = new Dictionary<int, TimeSpan>();
		var lastBusy = sw.Elapsed;
		var lastSample = TimeSpan.FromSeconds(-10);
		// a window's whole text is read at most every ProgressEvery: each read makes the tool walk its own window for UI Automation
		// (work in the tool's process), and .NET's client keeps a little of every element it made; the idle rule needs minutes,
		// not seconds
		var lastProgress = TimeSpan.FromSeconds(-1000);
		while (true) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			if (a_poll() == Poll.Done) {
				return;
			}
			if (sw.Elapsed > a_deadline) {
				throw new StepFailedException($"{StepCatalog.Name(a_step)} passed its {a_deadline.TotalMinutes:0} minute limit");
			}
			if (sw.Elapsed - lastSample >= TimeSpan.FromSeconds(5)) {
				lastSample = sw.Elapsed;
				bool alive;
				try {
					alive = !a_proc.HasExited;
				} catch (InvalidOperationException) {
					alive = false;
				}
				if (!alive) {
					// closed (or crashed) and still not done by the step's own signs: a short while more, then said - it used to
					// be waited for until the tool's whole limit
					goneAt ??= sw.Elapsed;
					if (sw.Elapsed - goneAt > GoneGrace) {
						throw new StepFailedException($"{StepCatalog.Name(a_step)} closed before it finished");
					}
				}
				if (alive) {
					// a window whose text cannot be read (empty) is judged by the tool's CPU, so a working tool is never called idle
					string? seen = null;
					// at least three reads inside the idle limit, so a window that changes is never taken for an idle one
					if (a_progress != null && sw.Elapsed - lastProgress >= TimeSpan.FromTicks(Math.Min(ProgressEvery.Ticks, idleLimit.Ticks / 3))) {
						lastProgress = sw.Elapsed;
						seen = a_progress();
					} else if (a_progress != null) {
						seen = lastSeen;
					}
					if (!string.IsNullOrEmpty(seen)) {
						if (seen != lastSeen) {
							lastSeen = seen;
							lastBusy = sw.Elapsed;
						}
						var children = Procs.ChildCpu(a_proc.Id);
						if (Procs.CpuGain(lastChildren, children) > TimeSpan.FromMilliseconds(300)) {
							lastBusy = sw.Elapsed;
						}
						lastChildren = children;
					} else {
						var cpu = Procs.TreeCpu(a_proc.Id);
						if (cpu - lastCpu > TimeSpan.FromMilliseconds(300)) {
							lastBusy = sw.Elapsed;
						}
						lastCpu = cpu;
					}
					if (sw.Elapsed - lastBusy > idleLimit) {
						if (a_onIdle?.Invoke() == true) {
							lastBusy = sw.Elapsed;
						} else {
							var tree = Procs.Tree(a_proc.Id);
							var boxes = Uia.Boxes(tree);
							// a window of its own that is no message box (a Delphi form, a WPF dialog) is named by its title
							var owned = boxes.Count > 0 ? [] : Uia.OwnedWindows(tree);
							var said = boxes.Count > 0 ? " It shows: " + string.Join(" / ", boxes.Select(b => b.Text)) :
								owned.Count > 0 ? " It has a window open: " + string.Join(" / ", owned) :
								lastSeen is { Length: > 0 } s ? " Its window shows: " + (s.Length > 300 ? "..." + s[^300..] : s) : " It shows no question.";
							throw new StepFailedException($"{StepCatalog.Name(a_step)} sat idle for {idleLimit.TotalMinutes:0} minutes without finishing.{said}");
						}
					}
				}
			}
			// a Stop ends this wait too; the look at the top of the loop then throws
			a_ctx.Cancel.WaitHandle.WaitOne(every);
		}
	}

	// wait for a process to end on its own (with a deadline); the stall rule as above
	public static int? Exit(RunContext a_ctx, string a_step, Process a_proc, TimeSpan a_deadline)
	{
		Tool(a_ctx, a_step, a_proc, a_deadline, () => {
			try {
				return a_proc.HasExited ? Poll.Done : Poll.Wait;
			} catch (InvalidOperationException) {
				return Poll.Done;
			}
		});
		try {
			a_proc.WaitForExit(5000);
			return a_proc.ExitCode;
		} catch (InvalidOperationException) {
			return null;
		}
	}
}
