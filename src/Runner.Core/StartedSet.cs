using System.Collections;

namespace ToolRunner;

// The processes a run started, each with the time it started. Thread-safe: Stop reads it on the command watcher's thread while
// a step adds to it (CodeRabbit on PR #24 - a HashSet copied mid-add throws); enumerating gives a copy. A PID Windows gave to
// another process since is told apart by its start time (SameProcess), so Stop never ends a stranger that got a reused PID.
//
// And what those processes started themselves (Sweep). A program that hands its work to a child and then ends - a packed
// tool's launcher, the game that starts Faster Archives' own program and closes - leaves that child with no living parent,
// and nothing reached through the living ones finds it again: the step's CloseTool and a Stop passed it over. So children
// are put on record while their parent lives, or at the first look that finds it ended, each with its own start time, and
// stay the run's own after it.
public sealed class StartedSet : IEnumerable<int>
{
	readonly Lock _gate = new();
	readonly Dictionary<int, DateTime?> _pids = [];
	readonly Dictionary<int, DateTime> _kin = [];
	// recorded ones found ended: their children were looked for at that look and never again (the number is anyone's after it)
	readonly HashSet<int> _ended = [];
	// the program names of what the run started itself ("DynDOLODx64.exe")
	readonly HashSet<string> _images = new(StringComparer.OrdinalIgnoreCase);
	// when the process under a number started (null: gone, or closed to this user)
	readonly Func<int, DateTime?> _startOf;
	// Every process on record whose children are still looked for is held open (Procs.Pin): Windows gives its number to
	// nobody else while it is held, so a row that names that number as its parent names that very process - also after it
	// has ended. Without the hold, a number that changed hands after its process ended, and whose new holder had ended
	// again by the next look, made that stranger's children read as the run's own (the new holder leaves no trace).
	readonly Dictionary<int, IDisposable> _pins = [];
	// One that could not be held: the last reading of the clock by which it was seen alive. Only a child that had started
	// by then is certainly its own; a later one may be the child of whoever had the number next.
	readonly Dictionary<int, DateTime> _seen = [];
	readonly Func<int, DateTime, IDisposable?>? _pin;
	// One look at a time, and no hold is given up while a look is out. A look takes "this number is held" at its start and
	// relies on it to its end (Sure): a second look on another thread - the watch's beat and a Stop's - that found the
	// parent ended in between gave its hold up under the first, and from that moment the number could change hands while
	// the first look still read every child under it as the run's own. Taken before _gate, never inside it.
	readonly Lock _sweeping = new();

	// (the offline tests hand in start times of their own - and then a hold of their own, or none)
	public StartedSet(Func<int, DateTime?>? a_startOf = null, Func<int, DateTime, IDisposable?>? a_pin = null)
	{
		_startOf = a_startOf ?? Procs.StartTime;
		_pin = a_pin ?? (a_startOf == null ? Procs.Pin : null);
	}

	public void Add(int a_pid)
	{
		var at = _startOf(a_pid);
		var image = Procs.Image(a_pid);
		var pin = at is { } started ? _pin?.Invoke(a_pid, started) : null;
		IDisposable? old;
		lock (_gate) {
			_pids[a_pid] = at;
			_ended.Remove(a_pid);
			_kin.Remove(a_pid);
			_pins.Remove(a_pid, out old);
			_seen.Remove(a_pid);
			if (pin != null) {
				_pins[a_pid] = pin;
			} else if (at is { } began) {
				// not held: it was alive when it started, and no more is known yet
				_seen[a_pid] = began;
			}
			if (image != "") {
				_images.Add(image);
			}
		}
		if (old != null) {
			// (never under a look that still counts the number as held)
			lock (_sweeping) {
				old.Dispose();
			}
		}
	}

	// Every hold given up, when the run this set belongs to is over. What is on record stays on record; from here a number
	// may change hands again, so nothing more is put on record under a process that has ended. A look that is still out
	// (the watch's last beat) is waited for: its holds are not given up under it.
	public void Release()
	{
		lock (_sweeping) {
			List<IDisposable> all;
			lock (_gate) {
				all = [.. _pins.Values];
				_pins.Clear();
			}
			all.ForEach(p => p.Dispose());
		}
	}

	// how many processes are held open now (the offline tests: a hold is given up with its record)
	public int Held
	{
		get {
			lock (_gate) {
				return _pins.Count;
			}
		}
	}

	// Is this the name of a program the run started itself? A child on record under such a name is the tool under another
	// number (a launcher handed over to it and ended). Under any other name it is something a tool merely opened - the
	// player's browser on a help page, an editor on a log - which stays theirs: a Stop never ends it.
	public bool StartedImage(string a_image)
	{
		lock (_gate) {
			return _images.Contains(a_image);
		}
	}

	public bool Contains(int a_pid)
	{
		lock (_gate) {
			return _pids.ContainsKey(a_pid);
		}
	}

	public int Count
	{
		get {
			lock (_gate) {
				return _pids.Count;
			}
		}
	}

	// still the very process that was recorded (same PID, same start time); one whose start time could not be read is not
	public bool SameProcess(int a_pid)
	{
		DateTime? at;
		lock (_gate) {
			if (!_pids.TryGetValue(a_pid, out at)) {
				return false;
			}
		}
		return at != null && _startOf(a_pid) == at;
	}

	// every recorded process that is still that very process - what the run started itself
	public List<int> Live()
	{
		KeyValuePair<int, DateTime?>[] copy;
		lock (_gate) {
			copy = [.. _pids];
		}
		return copy.Where(p => p.Value != null && _startOf(p.Key) == p.Value).Select(p => p.Key).ToList();
	}

	// those, and what they started that is on record (Sweep) and still that very process - what "the run's own" means for
	// closing a tool, for a Stop, and for telling the player's programs under Mod Organizer from the run's
	public List<int> Owned() => [.. OwnedAt().Keys];

	// the same, each with the start time on its record: who ends one of them names it by both (Procs.KillTree), so a number
	// that changes hands after this look is never taken for it
	public Dictionary<int, DateTime> OwnedAt()
	{
		KeyValuePair<int, DateTime?>[] roots;
		KeyValuePair<int, DateTime>[] kin;
		lock (_gate) {
			roots = [.. _pids];
			kin = [.. _kin];
		}
		var live = new Dictionary<int, DateTime>();
		foreach (var (pid, at) in roots) {
			if (at is { } t && _startOf(pid) == t) {
				live[pid] = t;
			}
		}
		foreach (var (pid, at) in kin) {
			if (!live.ContainsKey(pid) && _startOf(pid) == at) {
				live[pid] = at;
			}
		}
		return live;
	}

	// One look at Windows' process list: the children of everything on record are put on record too. Made every few seconds
	// by the run's watch, and by whoever is about to ask Owned.
	public void Sweep() => Sweep(static () => {
		var rows = Procs.Snapshot();
		return Procs.LookFailed ? null : rows;
	}, Procs.StartClock);

	// a_list: Windows' process list as (process, its parent's number) rows, null when Windows would not give it.
	// a_clock: the clock process start times are stamped with (the offline tests hand in both)
	public void Sweep(Func<IReadOnlyCollection<(int Pid, int Parent)>?> a_list, Func<DateTime> a_clock)
	{
		// from the moment it reads which numbers are held to the moment it gives a hold up itself, no other look runs and
		// no hold is given up (_sweeping)
		lock (_sweeping) {
			SweepAlone(a_list, a_clock);
		}
	}

	void SweepAlone(Func<IReadOnlyCollection<(int Pid, int Parent)>?> a_list, Func<DateTime> a_clock)
	{
		Dictionary<int, DateTime> parents;
		Dictionary<int, DateTime> seen;
		HashSet<int> held;
		lock (_gate) {
			parents = _pids.Where(p => p.Value != null && !_ended.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value!.Value);
			foreach (var k in _kin) {
				parents.TryAdd(k.Key, k.Value);
			}
			seen = new Dictionary<int, DateTime>(_seen);
			held = [.. _pins.Keys];
		}
		if (parents.Count == 0) {
			return;
		}
		// until when a parent's number was certainly its own: for good while it is held open, else up to the last reading of
		// the clock by which it was seen alive
		DateTime Sure(int a_pid) => held.Contains(a_pid) ? DateTime.MaxValue : seen.GetValueOrDefault(a_pid, DateTime.MinValue);
		var starts = new Dictionary<int, DateTime?>();
		DateTime? StartOf(int a_pid) => starts.TryGetValue(a_pid, out var t) ? t : starts[a_pid] = _startOf(a_pid);
		// The clock is read BEFORE each list is taken, and a row counts only for a process that had started before that
		// reading (Children). A row names whoever had the number when the list was taken, and a number can change hands the
		// moment after: read after the list, the clock let through a newcomer that took an ended child's number in between.
		// A child that started in the instant between the reading and the list is found by the next look.
		var before = a_clock();
		var rows = a_list();
		// Windows would not give its list this time: nothing is learned from that, least of all that an ended parent left no child
		if (rows == null) {
			return;
		}
		var found = Children(parents, rows, StartOf, before, Sure);
		var ended = parents.Where(p => StartOf(p.Key) != p.Value).ToList();
		// one that is not held and is alive at this look was alive at this look's reading (its start time is read after it)
		var alive = parents.Where(p => !held.Contains(p.Key) && StartOf(p.Key) == p.Value).Select(p => p.Key).ToList();
		foreach (var pid in alive) {
			seen[pid] = before;
		}
		if (ended.Count > 0) {
			// One that has ended may have started a child after the list above was taken (it was still alive then). It starts
			// nothing more now, so one more list - read fresh - holds everything it ever started: only then is it retired.
			// Without that list nothing is retired; the next look tries again. (It had ended before this second reading of
			// the clock, so everything it ever started had started before it too.)
			var beforeLast = a_clock();
			var last = a_list();
			if (last == null) {
				ended.Clear();
			} else {
				starts.Clear();
				var all = new Dictionary<int, DateTime>(parents);
				foreach (var (pid, at) in found) {
					all.TryAdd(pid, at);
				}
				// one found a moment ago is not held yet: it was alive when its start time was read, after the first reading
				DateTime SureNow(int a_pid) => parents.ContainsKey(a_pid) ? Sure(a_pid) : before;
				foreach (var (pid, at) in Children(all, last, StartOf, beforeLast, SureNow)) {
					found.TryAdd(pid, at);
				}
				// A row under an ended parent whose process started at or after this reading was not taken (it may be a real
				// child that young - the clock has ticks, and both readings can fall in one). Its parent is kept for the
				// next look, whose later reading can take it; retired now, that child would never be found again. Only for a
				// child that a later look can still take: one that started after the parent's number stopped being certainly
				// the parent's never is.
				if (last.Any(r => ended.Any(e => e.Key == r.Parent) && StartOf(r.Pid) is { } began && began >= beforeLast && began <= Sure(r.Parent))) {
					ended.Clear();
				}
			}
		}
		// each one found is held open before it goes on record (asked of Windows outside the lock); one that cannot be held
		// - it has ended since, or its number is another's already - is on record all the same, with no child of its own
		// taken past this look's first reading
		var pins = found.ToDictionary(c => c.Key, c => _pin?.Invoke(c.Key, c.Value));
		var spare = new List<IDisposable?>();
		lock (_gate) {
			foreach (var (pid, at) in found) {
				// one the run recorded itself meanwhile stays that, with the hold it took itself
				if (_pids.TryGetValue(pid, out var own) && own == at) {
					spare.Add(pins[pid]);
					continue;
				}
				_kin[pid] = at;
				if (_pins.Remove(pid, out var earlier)) {
					spare.Add(earlier);
				}
				if (pins[pid] is { } pin) {
					_pins[pid] = pin;
					_seen.Remove(pid);
				} else {
					_seen[pid] = before;
				}
			}
			foreach (var (pid, at) in ended) {
				var retired = false;
				if (_kin.TryGetValue(pid, out var kin) && kin == at) {
					_kin.Remove(pid);
					retired = true;
				}
				if (_pids.TryGetValue(pid, out var own) && own == at) {
					_ended.Add(pid);
					retired = true;
				}
				// its children were looked for one last time: the hold has done its work
				if (retired) {
					if (_pins.Remove(pid, out var done)) {
						spare.Add(done);
					}
					_seen.Remove(pid);
				}
			}
			foreach (var pid in alive) {
				// still the record this look read, and still not held
				var same = (_pids.TryGetValue(pid, out var own) && own == parents[pid]) || (_kin.TryGetValue(pid, out var kin) && kin == parents[pid]);
				if (same && !_pins.ContainsKey(pid) && _seen.TryGetValue(pid, out var was) && was < before) {
					_seen[pid] = before;
				}
			}
		}
		foreach (var p in spare) {
			p?.Dispose();
		}
	}

	// Which of a_rows (process, its parent's number) are children of a_parents (each with its recorded start time), and of
	// those children, and so on - each with its own start time. A process counts as a child only when
	// - it started no earlier than its parent (Windows keeps a dead parent's number in its children, and hands that number
	//   out again: one that started before is a child of whoever had the number before), and
	// - the parent's number is not another process's by now, or it started before that one did (a child started since the
	//   number changed hands is the new owner's - a program of the player's, never the run's), and
	// - when the parent has ended: it started no later than a_sureUntil says the number was certainly the parent's (for
	//   good while the parent is held open; else the last time it was seen alive). Past that the number may have been
	//   another process's in between, and one that has ended again leaves nothing to tell it by, and
	// - it had started before a_beforeListUtc, a reading of the clock made BEFORE the list was taken: a row names the process
	//   that had the number when the list was taken. One that started at or after the reading may only have the number of a
	//   child that has ended since, and is taken for nobody's child (a real child that young is found by the next look).
	public static Dictionary<int, DateTime> Children(IReadOnlyDictionary<int, DateTime> a_parents, IReadOnlyCollection<(int Pid, int Parent)> a_rows, Func<int, DateTime?> a_startOf,
		DateTime a_beforeListUtc, Func<int, DateTime> a_sureUntil)
	{
		var found = new Dictionary<int, DateTime>();
		var grew = true;
		while (grew) {
			grew = false;
			foreach (var (pid, parent) in a_rows) {
				if (pid == 0 || pid == parent || found.ContainsKey(pid)) {
					continue;
				}
				if (!found.TryGetValue(parent, out var pat) && !a_parents.TryGetValue(parent, out pat)) {
					continue;
				}
				if (a_startOf(pid) is not { } at || at < pat || at >= a_beforeListUtc) {
					continue;
				}
				// a parent on record itself, still that very process
				if (a_parents.TryGetValue(pid, out var self) && self == at) {
					continue;
				}
				// (a parent found in this very pass was read alive in it: it never comes to the second question)
				var now = a_startOf(parent);
				if (now != pat && ((now is { } other && at >= other) || at > a_sureUntil(parent))) {
					continue;
				}
				found[pid] = at;
				grew = true;
			}
		}
		return found;
	}

	public IEnumerator<int> GetEnumerator()
	{
		List<int> copy;
		lock (_gate) {
			copy = [.. _pids.Keys];
		}
		return copy.GetEnumerator();
	}

	IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
