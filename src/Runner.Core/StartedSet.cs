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

	public void Add(int a_pid)
	{
		var at = Procs.StartTime(a_pid);
		var image = Procs.Image(a_pid);
		lock (_gate) {
			_pids[a_pid] = at;
			_ended.Remove(a_pid);
			_kin.Remove(a_pid);
			if (image != "") {
				_images.Add(image);
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
		return at != null && Procs.StartTime(a_pid) == at;
	}

	// every recorded process that is still that very process - what the run started itself
	public List<int> Live()
	{
		KeyValuePair<int, DateTime?>[] copy;
		lock (_gate) {
			copy = [.. _pids];
		}
		return copy.Where(p => p.Value != null && Procs.StartTime(p.Key) == p.Value).Select(p => p.Key).ToList();
	}

	// those, and what they started that is on record (Sweep) and still that very process - what "the run's own" means for
	// closing a tool, for a Stop, and for telling the player's programs under Mod Organizer from the run's
	public List<int> Owned()
	{
		KeyValuePair<int, DateTime>[] kin;
		lock (_gate) {
			kin = [.. _kin];
		}
		var live = Live();
		live.AddRange(kin.Where(k => !live.Contains(k.Key) && Procs.StartTime(k.Key) == k.Value).Select(k => k.Key));
		return live;
	}

	// One look at Windows' process list: the children of everything on record are put on record too. Made every few seconds
	// by the run's watch, and by whoever is about to ask Owned.
	public void Sweep()
	{
		Dictionary<int, DateTime> parents;
		lock (_gate) {
			parents = _pids.Where(p => p.Value != null && !_ended.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value!.Value);
			foreach (var k in _kin) {
				parents.TryAdd(k.Key, k.Value);
			}
		}
		if (parents.Count == 0) {
			return;
		}
		var starts = new Dictionary<int, DateTime?>();
		DateTime? StartOf(int a_pid) => starts.TryGetValue(a_pid, out var t) ? t : starts[a_pid] = Procs.StartTime(a_pid);
		var rows = Procs.Snapshot();
		// Windows would not give its list this time: nothing is learned from that, least of all that an ended parent left no child
		if (Procs.LookFailed) {
			return;
		}
		var found = Children(parents, rows, StartOf);
		var ended = parents.Where(p => StartOf(p.Key) != p.Value).ToList();
		lock (_gate) {
			foreach (var (pid, at) in found) {
				// one the run recorded itself meanwhile stays that
				if (!(_pids.TryGetValue(pid, out var own) && own == at)) {
					_kin[pid] = at;
				}
			}
			foreach (var (pid, at) in ended) {
				if (_kin.TryGetValue(pid, out var kin) && kin == at) {
					_kin.Remove(pid);
				}
				if (_pids.TryGetValue(pid, out var own) && own == at) {
					_ended.Add(pid);
				}
			}
		}
	}

	// Which of a_rows (process, its parent's number) are children of a_parents (each with its recorded start time), and of
	// those children, and so on - each with its own start time. A process counts as a child only when
	// - it started no earlier than its parent (Windows keeps a dead parent's number in its children, and hands that number
	//   out again: one that started before is a child of whoever had the number before), and
	// - the parent's number is not another process's by now, or it started before that one did (a child started since the
	//   number changed hands is the new owner's - a program of the player's, never the run's).
	public static Dictionary<int, DateTime> Children(IReadOnlyDictionary<int, DateTime> a_parents, IReadOnlyCollection<(int Pid, int Parent)> a_rows, Func<int, DateTime?> a_startOf)
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
				if (a_startOf(pid) is not { } at || at < pat) {
					continue;
				}
				// a parent on record itself, still that very process
				if (a_parents.TryGetValue(pid, out var self) && self == at) {
					continue;
				}
				if (a_startOf(parent) is { } now && now != pat && at >= now) {
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
