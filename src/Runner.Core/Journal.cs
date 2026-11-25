using System.Text.Json;
using System.Text.Json.Serialization;

namespace ToolRunner;

public enum StepState
{
	Pending,
	Running,
	Done,
	Failed,
	Skipped,
	Stopped,
}

public enum RunState
{
	Running,
	Finished,
	Failed,
	Stopped,
	Restored,
}

public sealed class StepRecord
{
	public string Key { get; set; } = "";
	public string Name { get; set; } = "";
	public StepState State { get; set; }
	public DateTime? Started { get; set; }
	public DateTime? Ended { get; set; }
	public string Note { get; set; } = "";
	public string Activity { get; set; } = "";
	public double? Progress { get; set; }
	// the mod this run's step builds into (null: a step with no output mod)
	public string? Output { get; set; }
	// how big that mod was once the step had finished (null: not measured) - the run's summary and the next run's forecast
	public long? OutputBytes { get; set; }
}

// a step a run left alone because its old output is used: the step, its name, and the mod that holds that output
public sealed class OldUse
{
	public string Step { get; set; } = "";
	public string Name { get; set; } = "";
	public string Mod { get; set; } = "";
	// the mod had no line in the mod list and the run gave it one, to switch it on: a put-back keeps that line only for a
	// use that was carried out (the folder and what it holds are the player's either way)
	public bool Listed { get; set; }
	// the old output is switched on at its step's place. Until then the record says only what the run was about to do: a
	// run cut off in between is put back to the profile as it was, the old output as the player had it
	public bool Done { get; set; }
}

// an output moved into the run's backup before a tool rewrote it; put back when its step did not finish
public sealed class ParkRecord
{
	public string Step { get; set; } = "";
	// the mod; for a tool's own folder (IsFolder) only a label, which may read like some mod's name and is never taken for one
	public string Mod { get; set; } = "";
	public string From { get; set; } = "";
	public string To { get; set; } = "";
	public bool Replaced { get; set; }
	public bool PutBack { get; set; }
	public bool IsFolder { get; set; }
	// an undo has moved what stood at From aside: pressed again after a failure, it never takes what it put back there for the run's
	public bool SetAside { get; set; }
	// a tool's folder is all in the backup: until then what stands at From is what the move left, never the run's own
	public bool Moved { get; set; }
	// a mod's old output was on its way into the backup and the move never ended (a crash, a file held): what still stands at
	// From is the rest of that old output, never what a tool half wrote
	public bool Parking { get; set; }
	// the move back out of the backup has begun: at a second try (a crash, a file held) what stands at From is the part that is
	// back already, never what a tool half wrote
	public bool Returning { get; set; }
	// its copy in the backup was gone when it was to be put back (the backup cleared by hand): nothing can bring it back, the mod is
	// left switched off, and the put-back is not held up for ever by it
	public bool Lost { get; set; }
	// its place among this run's park and file records, in the order they were made (Journal.NextSeq; 0 = a record from
	// before the order was kept): an undo must know whether a file was kept before or after its mod was replaced
	public int Seq { get; set; }
}

// a file outside the mods that a step changes for its run (a tool's settings, DynDOLOD's preset, NGIO's flag): its old bytes
// kept in the run's backup (Backup null = it did not exist), put back by the step - or by Restore after a crash
public sealed class KeptFile
{
	public string Path { get; set; } = "";
	public string? Backup { get; set; }
	public bool Done { get; set; }
	// changed in place for good (no park record covers it): an undo puts this file back on its own
	public bool Settled { get; set; }
	// its place among this run's park and file records (ParkRecord.Seq)
	public int Seq { get; set; }
}

// The run record on disk: everything needed to put the profile back after a crash, a Stop now or a power cut, and what
// the window shows. Written atomically after every change. The order files as they were before the run sit beside it in
// "profile before\", byte for byte.
public sealed class Journal
{
	public string Id { get; set; } = "";
	public string SetupFile { get; set; } = "";
	public string SetupName { get; set; } = "";
	public string Profile { get; set; } = "";
	public string Mo2Exe { get; set; } = "";
	public string? Mo2Instance { get; set; }
	public string BackupDir { get; set; } = "";
	public int Pid { get; set; }
	// the owner's start time: a PID reused by another program after a crash never passes for the run
	public DateTime? PidStarted { get; set; }
	public DateTime Started { get; set; }
	public DateTime? Ended { get; set; }
	public RunState State { get; set; }
	public string Verdict { get; set; } = "";
	public string? ResumeFrom { get; set; }
	public bool StopAfterStep { get; set; }
	public List<StepRecord> Steps { get; set; } = [];
	public List<string> Built { get; set; } = [];
	// set only by older versions, on a run that changed the plugin order: read so such a run still has an order for Undo to take
	// back (and its backup still counts as one to keep); no run of this version sets it
	public bool OrderChanged { get; set; }
	// the steps this run did not run because the setup uses their old output, each with that output's mod - on record before
	// the run switches it back on at the step's place (OldOutput), and Done once it has; a put-back switches the ones that
	// were carried out on like what the run built, and leaves the others as the profile had them before the run
	public List<OldUse> UsedOld { get; set; } = [];
	public List<string> Retire { get; set; } = [];
	public List<string> Added { get; set; } = [];
	// Output mods that stood in the mods folder with no line in the mod list: the run gave each one its line before its
	// first step. One it then neither built nor switched on as an old output loses that line again when the profile is put
	// back - the list as it was before the run. Its folder is the player's and is never touched for it
	public List<string> Listed { get; set; } = [];
	public List<ParkRecord> Parked { get; set; } = [];
	[JsonIgnore]
	public IEnumerable<string> ParkedMods => Parked.Where(p => !p.IsFolder).Select(p => p.Mod);
	public List<KeptFile> Kept { get; set; } = [];
	// the last number given to a park or file record of this run
	public int LastSeq { get; set; }

	public int NextSeq() => ++LastSeq;

	// the marker left where the player stood before the clean save's teleport (the final save takes them back to it)
	public string? ReturnMarker { get; set; }
	// How often the run changed the files the game lists (outputs switched off, a step that ended) since the game last made
	// that list anew in this run; 0 = nothing changed since. Read by the step that starts the game next (Crdw)
	public int FilesChanged { get; set; }
	// the run switched mods off before its first tool: its put-back switches them on again, which changes those files once more
	public bool SwitchedOff { get; set; }
	public List<string> Notes { get; set; } = [];

	[JsonIgnore]
	public string Folder { get; set; } = "";
	[JsonIgnore]
	public string File => Path.Combine(Folder, "journal.json");
	[JsonIgnore]
	public string SnapshotDir => Path.Combine(Folder, "profile before");

	static readonly JsonSerializerOptions Options = new() {
		WriteIndented = true,
		Converters = { new JsonStringEnumConverter() },
	};

	readonly Lock _gate = new();

	// how many records this process has written: whoever keeps what it read (JournalCache) knows by it that one changed
	static long s_writes;

	public static long Writes => Interlocked.Read(ref s_writes);

	public void Save()
	{
		lock (_gate) {
			Files.WriteAtomic(File, JsonSerializer.SerializeToUtf8Bytes(this, Options));
			Interlocked.Increment(ref s_writes);
		}
	}

	// Several fields changed as ONE, then written: a save on another thread (the run's box watch saves the record too) waits
	// for the whole change, so the record on disk never holds half of it - a crash right after such a save put back half a group
	public void SaveWith(Action a_change)
	{
		lock (_gate) {
			a_change();
			Files.WriteAtomic(File, JsonSerializer.SerializeToUtf8Bytes(this, Options));
			Interlocked.Increment(ref s_writes);
		}
	}

	// for the paths that put things back: there the work comes first, and a record the disk will not take (it is full, or gone)
	// is said by the caller - never the reason a profile stays half switched
	public bool TrySave()
	{
		try {
			Save();
			return true;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	public static Journal Load(string a_folder)
	{
		var j = JsonSerializer.Deserialize<Journal>(Files.ReadShared(Path.Combine(a_folder, "journal.json")), Options) ??
		        throw new InvalidDataException("empty run record in " + a_folder);
		j.Folder = a_folder;
		return j;
	}

	public StepRecord? Step(string a_key) => Steps.FirstOrDefault(s => s.Key == a_key);

	public void SaveSnapshot(IReadOnlyDictionary<string, byte[]> a_snap)
	{
		Directory.CreateDirectory(SnapshotDir);
		foreach (var (f, b) in a_snap) {
			Files.WriteAtomic(Path.Combine(SnapshotDir, f), b);
		}
	}

	public Dictionary<string, byte[]> LoadSnapshot()
	{
		var d = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		if (Directory.Exists(SnapshotDir)) {
			foreach (var f in Directory.GetFiles(SnapshotDir)) {
				d[Path.GetFileName(f)] = System.IO.File.ReadAllBytes(f);
			}
		}
		return d;
	}

	// set when putting the profile back failed (e.g. a file locked): the window keeps offering Restore until it worked
	public bool NeedsRestore { get; set; }
	// the player undid this run: its outputs are no longer in the profile
	public bool Undone { get; set; }
	// an undo began and has not ended: outputs and lists may be half way, so nothing new starts until it is pressed again (or the
	// window's put-back, which ends it). What the player had changed is kept here - once the old list is back, it no longer shows it
	public bool Undoing { get; set; }
	// the run kept Mod Organizer open: after a crash it writes its older lists as it closes, so they say nothing about the player
	public bool Mo2KeptOpen { get; set; }
	public Dictionary<string, char>? UndoTheirs { get; set; }
	public Dictionary<string, bool>? UndoMarks { get; set; }

	public bool Unfinished => State == RunState.Running || NeedsRestore || Undoing;

	// the final save is being made: the run's profile is already put back (it is not Unfinished - nothing to restore), but its
	// game still runs, so the window keeps it as the running run (Stop and the close guard reach it)
	public bool InGameAfter { get; set; }

	// the run has finished and its profile is back, and the engine is still at its last chores (the setup's after-run commands,
	// packing the old outputs): to the window still the run going - Stop ends the chores - with Doing as what it shows
	public bool FinishingUp { get; set; }
	public string Doing { get; set; } = "";
	// the boxes open now that the run does not answer itself (a tool's, Mod Organizer's, one of the game's it does not know), in words
	public string Box { get; set; } = "";

	// what the window and the command line follow as "the run that is going", while its owner lives
	[JsonIgnore]
	public bool Live => Unfinished || InGameAfter || FinishingUp;

	// is the program that wrote it still alive? (a run that is still going is never "unfinished")
	public bool OwnerAlive => Pid != 0 && Procs.Alive(Pid) && IsRunner(Pid) &&
		(PidStarted == null || Procs.StartTime(Pid) is { } s && Math.Abs((s - PidStarted.Value).TotalSeconds) < 2);

	public static bool IsRunner(int a_pid)
	{
		if (Procs.Name(a_pid) is not { } name) {
			return false;
		}
		if (IsRunnerName(name)) {
			return true;
		}
		try {
			// a copy the player renamed is known by the product name inside its file (the start time check above keeps a reused
			// PID out)
			using var p = System.Diagnostics.Process.GetProcessById(a_pid);
			return IsRunnerName(ProductOf(p));
		} catch (ArgumentException) {
			return false;
		} catch (InvalidOperationException) {
			return false;
		}
	}

	// the program under its name or any it had ("Tool Runner", "MO2 Tool Runner", ...), its project names when built
	// without one, and the offline tests' own process. The rename had left this on the old names only: every real
	// run then read as dead to the window - Stop now sent nothing, closing the window neither asked nor stopped the run, and a
	// running run showed as interrupted; the tests did not see it, their own process name matched
	static string ProductOf(System.Diagnostics.Process a_p)
	{
		try {
			return a_p.MainModule?.FileVersionInfo.ProductName ?? "";
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) {
			return "";
		}
	}

	public static bool IsRunnerName(string a_processName) =>
		Brand.AllNames.Any(n => string.Equals(a_processName, n, StringComparison.OrdinalIgnoreCase)) ||
		a_processName is "Runner.App" or "Runner.Tests";

	public static IEnumerable<Journal> All(string a_runsDir)
	{
		if (!Directory.Exists(a_runsDir)) {
			yield break;
		}
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending(StringComparer.Ordinal)) {
			Journal? j = null;
			try {
				if (System.IO.File.Exists(Path.Combine(d, "journal.json"))) {
					j = Load(d);
				}
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
				// (a record that does not read is passed over here - an empty one, "null", too; NewestUnreadable keeps a start from
				// going over the newest such record)
			}
			if (j != null) {
				yield return j;
			}
		}
	}

	public static Journal? Interrupted(string a_runsDir) => All(a_runsDir).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive);

	// The newest run's record when it cannot be read, with why (null: there is none, or it reads). An interrupted run is always
	// the newest one - nothing starts after it - so a newest record that does not read may be exactly the run whose outputs are
	// still off: a new run must not start over it as if there were none. (Older records that do not read are passed over.)
	public static (string Folder, Exception Why)? NewestUnreadable(string a_runsDir)
	{
		if (!Directory.Exists(a_runsDir)) {
			return null;
		}
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending(StringComparer.Ordinal)) {
			try {
				if (HasRecord(d)) {
					LoadPatiently(d);
					return null;
				}
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
				return (d, e);
			}
		}
		return null;
	}

	// Every run record, each read once, and the first that cannot be read with why (null: every one reads). What deletes on the
	// strength of the records - removing the program's data, pruning old backups - judges by this one read alone: a record
	// passed over by All may be a run that still needs its profile put back or its backup for an undo, and a second read of
	// the same record need not come out as the first did.
	public static (List<Journal> Runs, (string Folder, Exception Why)? Bad) AllOrUnreadable(string a_runsDir)
	{
		var runs = new List<Journal>();
		(string Folder, Exception Why)? bad = null;
		if (!Directory.Exists(a_runsDir)) {
			return (runs, bad);
		}
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending(StringComparer.Ordinal)) {
			try {
				if (HasRecord(d)) {
					runs.Add(LoadPatiently(d));
				}
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
				bad ??= (d, e);
			}
		}
		return (runs, bad);
	}

	// by its name in the folder's own list: a record this user may not read is still there (File.Exists says "no" for that
	// too, and a run then started over an interrupted one as if there were none)
	static bool HasRecord(string a_dir) => Directory.EnumerateFiles(a_dir, "journal.json").Any();

	// a record read; a file held for a moment by another program is looked at again before that counts
	static Journal LoadPatiently(string a_dir)
	{
		for (var i = 0; ; ++i) {
			try {
				return Load(a_dir);
			} catch (Exception e) when (i < 4 && (e is IOException or UnauthorizedAccessException)) {
				Thread.Sleep(200);
			}
		}
	}
}

// The run records for a reader that looks often (the window, once a second): a record is read again only when its file
// changed, so a hundred past runs cost a hundred file looks, not a hundred reads. The records it hands out are shared - a
// caller that changes one loads its own copy (Journal.Load).
// And where the runs folder is watched (the window), not even those looks while nothing says a record changed: a look
// asked Windows for every run folder's record each time - every three seconds for as long as the window stood open, and
// again for each button that asks whether a run is going - work and garbage that grew with every run kept (up to a hundred
// for each setup).
public sealed class JournalCache(string a_runsDir)
{
	readonly Dictionary<string, (DateTime Stamp, long Size, Journal Run)> _seen = new(StringComparer.OrdinalIgnoreCase);
	readonly Lock _lock = new();
	// the list as last read whole, and what stood then: the runs folder's own time (it changes when a run folder comes or
	// goes), this process's count of record writes, the clock
	List<Journal>? _whole;
	DateTime _folderStamp;
	long _writesThen, _wholeAt;
	int _touched = 1;

	// Something watches the runs folder and calls Touch at every change under it. Only then is what was read before trusted
	// between two whole reads; without a watcher (a command) every read is whole, as it always was
	public bool Watched { get; set; }

	// a record changed, whoever changed it (the watcher's word; from any thread)
	public void Touch() => Volatile.Write(ref _touched, 1);

	// how long a whole read is trusted at most: a watcher can lose a change, and then says nothing
	public static TimeSpan WholeEvery { get; set; } = TimeSpan.FromSeconds(15);

	// how many times every run folder was looked at (the offline tests count them)
	public int WholeReads { get; private set; }

	// the window reads it from its poll thread and from its own thread
	public List<Journal> Read()
	{
		lock (_lock) {
			return Unchanged() ?? ReadLocked();
		}
	}

	// The list as it was last read whole, when nothing says a record changed since: no change reported by the watcher, no
	// record written by this process, no run folder come or gone, the trusted time not over - and every run that is not over
	// (it writes its record as it goes) still as it was read. Null: read whole
	List<Journal>? Unchanged()
	{
		if (!Watched || _whole == null || Volatile.Read(ref _touched) != 0 || Journal.Writes != _writesThen ||
		    Environment.TickCount64 - _wholeAt > (long)WholeEvery.TotalMilliseconds) {
			return null;
		}
		try {
			if (Directory.GetLastWriteTimeUtc(a_runsDir) != _folderStamp) {
				return null;
			}
			foreach (var j in _whole) {
				if (j.Live && (!_seen.TryGetValue(j.Folder, out var c) || new FileInfo(j.File) is not { Exists: true } f || c.Stamp != f.LastWriteTimeUtc || c.Size != f.Length)) {
					return null;
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
		return [.. _whole];
	}

	List<Journal> ReadLocked()
	{
		var list = new List<Journal>();
		++WholeReads;
		_whole = null;
		if (!Directory.Exists(a_runsDir)) {
			_seen.Clear();
			return list;
		}
		// what stands BEFORE the folders are looked at: a change that comes while they are is then seen as one made since
		Volatile.Write(ref _touched, 0);
		var (writes, at) = (Journal.Writes, Environment.TickCount64);
		DateTime folder;
		try {
			folder = Directory.GetLastWriteTimeUtc(a_runsDir);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			folder = DateTime.MinValue;
			at = long.MinValue / 2;
		}
		var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var d in Directory.GetDirectories(a_runsDir).OrderDescending(StringComparer.Ordinal)) {
			var f = new FileInfo(Path.Combine(d, "journal.json"));
			if (!f.Exists) {
				continue;
			}
			live.Add(d);
			if (_seen.TryGetValue(d, out var c) && c.Stamp == f.LastWriteTimeUtc && c.Size == f.Length) {
				list.Add(c.Run);
				continue;
			}
			try {
				var j = Journal.Load(d);
				_seen[d] = (f.LastWriteTimeUtc, f.Length, j);
				list.Add(j);
			} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or InvalidDataException) {
				// (passed over, as Journal.All does)
			}
		}
		foreach (var gone in _seen.Keys.Where(k => !live.Contains(k)).ToList()) {
			_seen.Remove(gone);
		}
		(_whole, _folderStamp, _writesThen, _wholeAt) = ([.. list], folder, writes, at);
		return list;
	}
}
