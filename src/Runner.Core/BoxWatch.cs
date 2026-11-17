namespace ToolRunner;

// The run's one watch for boxes, from its first step to its last. A game start once sat 80 minutes under a mod's question and
// the run's log never named it: each step looked only for the signals it knew (a log, a cache) and only where it thought a box
// could come. So every few seconds, whatever the step in hand does, the windows of every process the run started (Mod
// Organizer, the game, SKSE's loader, each tool and what they start) are looked at:
// - a box is named in the log and in the window the moment it is seen;
// - one the runner knows is answered (KnownBoxes);
// - one of the game that nobody here can answer ends that game after Wait, and the step in hand stops with its text (Blocked);
// - a tool's or Mod Organizer's own box is only named: the step that runs the tool has its own rules for its windows.
public sealed class BoxWatch : IDisposable
{
	// one open box as a look sees it: Id tells it from every other window, Press presses its button of that name
	public sealed record Open(long Id, BoxSeen Box, Action<string> Press);

	sealed class Known
	{
		public required DateTime First { get; init; }
		public DateTime? Pressed { get; set; }
		public int Presses { get; set; }
		public bool Blocked { get; set; }
	}

	readonly Func<IReadOnlyList<Open>> _look;
	readonly RunLog _log;
	readonly Action<string> _show;
	readonly Action? _endGame;
	readonly Dictionary<long, Known> _open = [];
	// held while a look acts on what it saw (a press, a game ended, a line for the window), and by the run's end while it
	// lets go of the watch. The watch's sleep waits on it too
	readonly object _gate = new();
	// Idle, Acting (a press, a game's end or a line for the window is under way) or Released (the run let go of the watch).
	// To begin an act and to release are one step each (Do, Dispose): no act BEGINS once the watch is released - also when
	// the run's end could wait no longer for the gate (a look held up in a log line or in a dead window). Only an act that
	// was under way by then still finishes.
	int _state;
	const int Idle = 0, Acting = 1, Released = 2;
	bool Gone => Volatile.Read(ref _state) == Released;
	Thread? _thread;
	string _shown = "";
	string _lastFault = "";
	volatile string? _blocked;

	public static TimeSpan Every { get; set; } = TimeSpan.FromSeconds(3);
	// how long the run's end waits for a look that is in the middle of a press, and then for the watch's thread to end
	public static TimeSpan EndWait { get; set; } = TimeSpan.FromSeconds(10);
	const int PressTries = 3;
	static readonly TimeSpan PressAgain = TimeSpan.FromSeconds(10);

	// how long a box of the game that the runner cannot answer may stay open (a player at the PC answers it) before its step stops
	public TimeSpan Wait { get; init; } = TimeSpan.FromSeconds(60);

	// the box that stopped the step in hand, in words; null while none did
	public string? Blocked => _blocked;

	readonly Lock _saidGate = new();
	(DateTime At, string Text)? _gameSaid;

	// The last box the game showed that was an error or a notice of its own (never a question the runner answers as a matter of
	// course), when it was seen no longer ago than a_span: a game that closes right after one has said why, and the step
	// that finds it gone passes that on
	public string? GameSaidWithin(TimeSpan a_span, DateTime? a_now = null)
	{
		lock (_saidGate) {
			return _gameSaid is { } said && (a_now ?? DateTime.UtcNow) - said.At <= a_span ? said.Text : null;
		}
	}

	// a_look: the open boxes of the run's processes. a_show: what is on screen and unanswered, for the window ("" = nothing).
	// a_endGame: ends the game a box nobody can answer belongs to
	public BoxWatch(Func<IReadOnlyList<Open>> a_look, RunLog a_log, Action<string> a_show, Action? a_endGame = null)
	{
		_look = a_look;
		_log = a_log;
		_show = a_show;
		_endGame = a_endGame;
	}

	public void Start()
	{
		if (_thread != null) {
			return;
		}
		_thread = new Thread(Loop) { IsBackground = true, Name = "box watch" };
		_thread.Start();
	}

	void Loop()
	{
		while (!Gone) {
			try {
				LookOnce(DateTime.UtcNow);
			} catch (OutOfMemoryException) {
				// no memory for this look (nor for a line about it): it is made again at the next beat. Left to leave this thread
				// it would end the whole program, and a run that ends that way never puts the profile back
			} catch (Exception e) when (e is not OutOfMemoryException) {
				// a look that could not be made is made again; the watch is never what ends a run. Said once per kind of fault
				if (e.Message != _lastFault && !Gone) {
					_lastFault = e.Message;
					_log.Detail($"the watch for boxes could not look this time ({e.GetType().Name}: {e.Message}) - it looks again");
				}
			}
			// nothing here is ever disposed under a look that is still out (a wait handle was, and the look that came back to it
			// took the whole program down): the sleep is a wait on the gate, which the run's end pulses
			lock (_gate) {
				if (!Gone) {
					Monitor.Wait(_gate, Every);
				}
			}
		}
	}

	// One thing the watch does to the world. It begins only while the run still holds the watch, and "begun" is taken in the
	// same step that Dispose's "released" is given in: a check and then the act would let the act start after the release.
	bool Do(Action a_act)
	{
		if (Interlocked.CompareExchange(ref _state, Acting, Idle) != Idle) {
			return false;
		}
		try {
			a_act();
		} finally {
			Interlocked.CompareExchange(ref _state, Idle, Acting);
		}
		return true;
	}

	// one look (the offline tests call it with a clock of their own)
	public void LookOnce(DateTime a_now)
	{
		// reading the windows can take long (a hung window answers UI Automation only at its timeout): outside the gate
		var boxes = _look();
		lock (_gate) {
			Act(boxes, a_now);
		}
	}

	// under the gate: what the look does about the boxes it saw
	void Act(IReadOnlyList<Open> a_boxes, DateTime a_now)
	{
		foreach (var gone in _open.Keys.Except(a_boxes.Select(b => b.Id)).ToList()) {
			_open.Remove(gone);
		}
		var waiting = new List<string>();
		foreach (var b in a_boxes) {
			// the run let go of the watch (while this look read the windows, or while it was held up in a press): nothing more
			if (Gone) {
				return;
			}
			var v = KnownBoxes.Judge(b.Box);
			if (!_open.TryGetValue(b.Id, out var k)) {
				k = _open[b.Id] = new Known { First = a_now };
				// the game's among the log's warnings; a tool's or Mod Organizer's as a plain line (their steps show boxes as a matter of course)
				_log.Say("a box is open - " + KnownBoxes.Describe(b.Box), b.Box.Game ? Level.Warn : Level.Detail);
				if (v.Move == BoxMove.Stop) {
					_log.Simple($"Note: the game shows a box nobody here can answer - {KnownBoxes.Describe(b.Box)} - answer it within {Wait.TotalSeconds:0} seconds, or the step stops");
				}
				if (b.Box.Game && v.Move is BoxMove.Press or BoxMove.Stop && v.Note != Crdw.Answered) {
					lock (_saidGate) {
						_gameSaid = (a_now, KnownBoxes.Describe(b.Box));
					}
				}
			}
			var answerable = v.Move == BoxMove.Press && k.Presses < PressTries;
			if (answerable) {
				// pressed again when the box still stands ten seconds on (a busy window drops a click)
				if (k.Pressed == null || a_now - k.Pressed >= PressAgain) {
					if (k.Presses == 0) {
						_log.Warn(v.Note);
					}
					k.Pressed = a_now;
					++k.Presses;
					Do(() => b.Press(v.Button!));
				}
				continue;
			}
			if (v.Move == BoxMove.Leave) {
				waiting.Add(v.Note);
				continue;
			}
			var words = "a box is open - " + KnownBoxes.Describe(b.Box);
			if (v.Move == BoxMove.Name || !b.Box.Game) {
				waiting.Add(words);
				continue;
			}
			// the game's, and nobody here can answer it (or its known answer did not take)
			if (k.Blocked) {
				continue;
			}
			if (a_now - k.First < Wait) {
				waiting.Add(words + $" - answer it within {Wait.TotalSeconds:0} seconds, or the step stops here");
				continue;
			}
			k.Blocked = true;
			_blocked = v.Move == BoxMove.Stop ? v.Note :
				$"the game's box did not close after its answer was pressed {PressTries} times - {KnownBoxes.Describe(b.Box)}. Answer it in the game once, then run again from this step";
			_log.Warn(_blocked);
			Do(() => _endGame?.Invoke());
		}
		var show = string.Join("; ", waiting.Distinct(StringComparer.Ordinal));
		// (never after the run let go of the watch: a look that was stuck in a hung window would write into a finished run's record)
		if (show != _shown && Do(() => _show(show))) {
			_shown = show;
		}
	}

	// has the watch's own thread ended (within a_within)? For who must know that a look still out has come back
	public bool Ended(TimeSpan a_within) => _thread == null || _thread.Join(a_within);

	// The run lets go of the watch. A look that is acting holds the gate: it is waited for, so that nothing is under way when
	// this returns - but never without end (a press that hangs in a dead window, a log line the disk holds up, must not hold
	// the end of a run). Released is given in one step either way, and no act begins after it (Do); one that was under way
	// when the wait ran out is all that may still finish.
	public void Dispose()
	{
		if (Gone) {
			return;
		}
		var held = Monitor.TryEnter(_gate, EndWait);
		try {
			Interlocked.Exchange(ref _state, Released);
			if (held) {
				Monitor.PulseAll(_gate);
			}
		} finally {
			if (held) {
				Monitor.Exit(_gate);
			}
		}
		_thread?.Join(EndWait);
	}
}

// What the watch reads off Windows: the box windows of the run's own processes.
public static class RunWindows
{
	// GameOf: the Skyrim this process is, or runs under (null: none - Mod Organizer, a tool, SKSE's loader)
	sealed record Proc(DateTime? Started, bool Game, string Image, int? GameOf);

	// Is a box of this process the run's game's? Its own game flag, unless the Skyrim it is or runs under is one the run did
	// not start: a game the player started under the run's Mod Organizer is theirs with everything it starts - never
	// answered for them, and never what stops a step or ends the run's own game
	public static bool GameFlag(bool a_game, int? a_gameOf, Func<int, bool>? a_runsGame) =>
		a_game && (a_runsGame == null || a_gameOf is not { } game || a_runsGame(game));

	// Every process the run started and everything those started, kept across looks: a program the game started (Faster
	// Archives' own, which outlives the game it closed) has lost its parent by the time its window matters.
	public sealed class Family
	{
		readonly Dictionary<int, Proc> _known = [];

		static bool IsSkyrim(string a_image) => a_image.Equals(InGame.GameImage, StringComparison.OrdinalIgnoreCase);

		static bool IsGame(string a_image) => IsSkyrim(a_image) || a_image.StartsWith("skse64_loader", StringComparison.OrdinalIgnoreCase);

		// the family now: a_roots (what the run started itself) and their children, each with whether it is the game's
		// a_runsGame: is this Skyrim the one the run started? One the player starts under the run's Mod Organizer is only ever
		// named - it is never answered for them, and never what stops a step
		// a_orphans: what the run has on record as started by its own processes (StartedSet.Owned). One whose parent ended
		// between two looks was never met here as a child - Faster Archives' own program, when the game closes within
		// seconds of starting it - and is taken in from that record
		public Dictionary<int, (bool Game, string Image)> Now(IEnumerable<int> a_roots, Func<int, bool>? a_runsGame = null, IEnumerable<int>? a_orphans = null)
		{
			foreach (var dead in _known.Where(p => p.Value.Started == null || Procs.StartTime(p.Key) != p.Value.Started).Select(p => p.Key).ToList()) {
				_known.Remove(dead);
			}
			foreach (var r in a_roots) {
				if (!_known.ContainsKey(r) && Procs.StartTime(r) is { } at) {
					var image = Procs.Image(r);
					_known[r] = new Proc(at, IsGame(image), image, IsSkyrim(image) ? r : null);
				}
			}
			var parents = Procs.Snapshot();
			var grew = true;
			while (grew) {
				grew = false;
				foreach (var (pid, parent) in parents) {
					if (pid == 0 || _known.ContainsKey(pid) || !_known.TryGetValue(parent, out var p)) {
						continue;
					}
					// a child started after its parent: Windows keeps a dead parent's number in its children, and a new process can get it
					if (Procs.StartTime(pid) is not { } at || p.Started is { } pat && at < pat) {
						continue;
					}
					var image = Procs.Image(pid);
					_known[pid] = new Proc(at, p.Game || IsGame(image), image, IsSkyrim(image) ? pid : p.GameOf);
					grew = true;
				}
			}
			foreach (var o in a_orphans ?? []) {
				if (!_known.ContainsKey(o) && Procs.StartTime(o) is { } at) {
					var image = Procs.Image(o);
					_known[o] = new Proc(at, IsGame(image), image, IsSkyrim(image) ? o : null);
				}
			}
			return _known.ToDictionary(p => p.Key, p => (GameFlag(p.Value.Game, p.Value.GameOf, a_runsGame), p.Value.Image));
		}
	}

	// The open boxes of those processes. Only a window that is a box by its class becomes a UI Automation element, and a Qt
	// window another window owns once, when it is new (a Qt message box carries a plain Qt window class): .NET's UI Automation
	// client keeps a little memory for every element it ever makes, and this look repeats every few seconds for hours.
	public sealed class Reader
	{
		readonly Dictionary<nint, bool> _qtIsBox = [];
		readonly Dictionary<nint, (DateTime At, BoxWatch.Open Box)> _read = [];

		// a box that stays open is read again only this often: its words do not change, and each read makes new elements
		static readonly TimeSpan ReadAgain = TimeSpan.FromSeconds(30);

		public List<BoxWatch.Open> Boxes(IReadOnlyDictionary<int, (bool Game, string Image)> a_family)
		{
			var found = new List<BoxWatch.Open>();
			if (a_family.Count == 0) {
				_qtIsBox.Clear();
				_read.Clear();
				return found;
			}
			var windows = Native.TopWindowsOf(a_family.Keys.ToHashSet());
			foreach (var closed in _qtIsBox.Keys.Except(windows).ToList()) {
				_qtIsBox.Remove(closed);
			}
			foreach (var closed in _read.Keys.Except(windows).ToList()) {
				_read.Remove(closed);
			}
			foreach (var h in windows) {
				if (_read.TryGetValue(h, out var last) && DateTime.UtcNow - last.At < ReadAgain) {
					found.Add(last.Box);
					continue;
				}
				var cls = Native.ClassName(h);
				var box = Uia.BoxClass(cls);
				if (!box && cls.StartsWith("Qt", StringComparison.Ordinal) && Native.IsOwned(h)) {
					if (!_qtIsBox.TryGetValue(h, out box)) {
						box = Uia.FromHandle(h) is { } q && Uia.ClassName(q) == "QMessageBox";
						_qtIsBox[h] = box;
					}
				}
				if (!box || Native.GetWindowThreadProcessId(h, out var pid) == 0 || !a_family.TryGetValue((int)pid, out var whose)) {
					continue;
				}
				if (Uia.FromHandle(h) is not { } e) {
					continue;
				}
				var buttons = Uia.RealButtons(e);
				var names = Uia.BoxNames(buttons);
				var open = new BoxWatch.Open(h, new BoxSeen(whose.Image, whose.Game, Uia.Name(e), Uia.TextOf(e, 800), names),
					label => {
						if (names.IndexOf(label) is >= 0 and var at) {
							Uia.Press(buttons[at]);
						}
					});
				_read[h] = (DateTime.UtcNow, open);
				found.Add(open);
			}
			return found;
		}
	}
}
