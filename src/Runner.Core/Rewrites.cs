namespace ToolRunner;

// Files of the player's own that a step lets a tool rewrite where they stand (a plugin xEdit cleans, the meshes an optimizer
// saves again): before the tool starts, a copy of each goes into the run's backup under the path it has in its folder. A
// step that does not finish gets every one back. A step that finished keeps only the copies of what the tool changed or took
// away, and an undo lays exactly those back. One record per folder however many files it holds: a record per file made the
// run's record as big as the mod, and that record is written at every change and read by the window whenever it changes.
public sealed class RewriteRecord
{
	public string Step { get; set; } = "";
	// the mod's name, for the log and for its folder in the backup
	public string Label { get; set; } = "";
	// the folder the files stand in
	public string Root { get; set; } = "";
	// their copies, each under the path it has under Root
	public string Backup { get; set; } = "";
	// the copies are still being made: no tool has started, nothing under Root has changed
	public bool Keeping { get; set; }
	// the step is done with this folder: Backup holds exactly the files the tool changed or took away, as they were
	public bool Settled { get; set; }
	// how many copies that is (0: the tool left everything as it was)
	public int Kept { get; set; }
	// how many files the tool made new under Root: an undo takes those out again. Their names stand in a list beside the copies
	// (Rewrites.AddedOf) - a tool that unpacks an archive makes thousands, and this record is written at every change
	public int Added { get; set; }
	// laid back: by a step that did not finish, a put-back, or an undo
	public bool PutBack { get; set; }
	// its copies were gone from the backup when they were to be laid back: nothing can bring the files back
	public bool Lost { get; set; }
	// its place among this run's park and file records (ParkRecord.Seq)
	public int Seq { get; set; }
}

// A program a step started that writes inside the player's own mods (an optimizer, run by itself or in its own window), by its
// number and its start time: nothing of those mods is laid back while it may still write
public sealed class RewriteWriter
{
	public string Step { get; set; } = "";
	public int Pid { get; set; }
	public DateTime StartedUtc { get; set; }
}

public static class Rewrites
{
	public const string Folder = "rewritten";

	// room left on the backup's disk once the copies are made: the run's record and log are written there too (the forecast
	// asks for the same room)
	public const long Spare = 64L << 20;

	// what stood under Root before the tool started, one path a line, beside the copies (never among them: a file of that name in
	// the mod would be taken for it)
	static string BeforeFile(RewriteRecord a_r) => a_r.Backup + " files before.txt";

	// what the tool made new under Root, written when the folder is settled
	static string AddedFile(RewriteRecord a_r) => a_r.Backup + " files added.txt";

	// the copies a settled folder holds, written with it: an undo checks each is still there before it begins
	static string KeptFile(RewriteRecord a_r) => a_r.Backup + " files kept.txt";

	// The list of what the tool made new. One that is missing or does not read, or that names another number of files than the
	// record counts, is no "nothing new": that throws, and an undo stays to be done until it reads again
	public static List<string> AddedOf(RewriteRecord a_r)
	{
		if (a_r.Added == 0 && !File.Exists(AddedFile(a_r))) {
			return [];
		}
		List<string> added = [.. Files.ReadSharedText(AddedFile(a_r)).Split('\n', StringSplitOptions.RemoveEmptyEntries)];
		if (added.Count != a_r.Added) {
			throw new IOException($"{a_r.Label}: the list of the files the tool made new names {added.Count}, its record counts {a_r.Added}");
		}
		return added;
	}

	// every file under a folder, as paths under it
	public static List<string> ListOf(string a_root) =>
		Directory.Exists(a_root) ? Directory.EnumerateFiles(a_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(a_root, f)).ToList() : [];

	// how much a step would copy: the files of a_root that a_which takes (null: every one)
	public static long BytesOf(string a_root, Func<string, bool>? a_which = null) =>
		!Directory.Exists(a_root) ? 0 : new DirectoryInfo(a_root).EnumerateFiles("*", SearchOption.AllDirectories)
			.Where(f => a_which == null || a_which(f.Name)).Sum(f => f.Length);

	// Copies of a_files (paths under a_root; null = every file there) into the run's backup, on record before the first one is
	// made. The tool is started only after this returns: until then nothing under a_root has changed, and a step cut off here
	// has nothing to put back.
	public static RewriteRecord Keep(RunContext a_ctx, string a_step, string a_label, string a_root, IReadOnlyCollection<string>? a_files = null)
	{
		var root = Path.GetFullPath(a_root);
		var all = ListOf(root);
		var keep = a_files == null ? all : a_files.Where(f => File.Exists(Path.Combine(root, f))).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		var bytes = keep.Sum(f => new FileInfo(Path.Combine(root, f)).Length);
		// never begun without the room for all of it: a tool let into a folder whose copies did not all fit would leave some
		// files with no way back (asked through the forecast's own probe, which the offline tests answer)
		if (DiskForecast.Real.Free(a_ctx.BackupFolder) is { } free && free < bytes + Spare) {
			throw new StepFailedException($"not enough room to keep a copy of {a_label}'s files first: {DiskForecast.Size(bytes)} is needed in the run's backup " +
				$"({Path.GetPathRoot(Path.GetFullPath(a_ctx.BackupFolder))}) and {DiskForecast.Size(free)} is free - nothing was changed");
		}
		var backup = Steps.StepKit.FreeFolder(Path.Combine(a_ctx.BackupFolder, Folder, a_label));
		Directory.CreateDirectory(backup);
		var r = new RewriteRecord { Step = a_step, Label = a_label, Root = root, Backup = backup, Keeping = true, Seq = a_ctx.Journal.NextSeq() };
		a_ctx.Journal.Rewritten.Add(r);
		a_ctx.Journal.Save();
		Files.WriteAtomic(BeforeFile(r), string.Join("\n", all));
		long done = 0;
		foreach (var f in keep) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var dst = Path.Combine(backup, f);
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			Files.Copy(Path.Combine(root, f), dst);
			done += new FileInfo(dst).Length;
			a_ctx.Activity(a_step, $"keeping a copy of {a_label} first", bytes == 0 ? null : (double)done / bytes);
		}
		r.Keeping = false;
		r.Kept = keep.Count;
		a_ctx.Journal.Save();
		a_ctx.Log.Detail($"{a_label}: a copy of {keep.Count} file(s) ({DiskForecast.Size(bytes)}) kept in the run's backup first");
		return r;
	}

	// a program that writes inside the player's mods, on record before it can write (its number and start time)
	public static void Writing(RunContext a_ctx, string a_step, int a_pid)
	{
		if (Procs.StartTime(a_pid) is not { } at) {
			return;
		}
		a_ctx.Journal.Writers.Add(new RewriteWriter { Step = a_step, Pid = a_pid, StartedUtc = at });
		a_ctx.Journal.Save();
	}

	// that program has ended: off the record
	public static void DoneWriting(RunContext a_ctx, int a_pid)
	{
		if (a_ctx.Journal.Writers.RemoveAll(w => w.Pid == a_pid) > 0) {
			a_ctx.Journal.TrySave();
		}
	}

	// Every program on record that may write into the player's mods (of a_step, or all) is gone: one that still runs as the very
	// process the run started (number and start time) is ended through one handle and waited for - up to a minute each. False:
	// one is still there, and nothing may be laid back yet
	static bool WritersGone(RunContext a_ctx, string? a_step)
	{
		var gone = true;
		foreach (var w in a_ctx.Journal.Writers.Where(w => a_step == null || w.Step == a_step).ToList()) {
			if (Procs.StartTime(w.Pid) == w.StartedUtc) {
				Procs.KillTree(w.Pid, w.StartedUtc);
				for (var sw = System.Diagnostics.Stopwatch.StartNew(); Procs.StartTime(w.Pid) == w.StartedUtc && sw.Elapsed < TimeSpan.FromMinutes(1);) {
					Thread.Sleep(250);
				}
			}
			if (Procs.StartTime(w.Pid) == w.StartedUtc) {
				gone = false;
				a_ctx.Log.Error($"a program this run started (process {w.Pid}) still runs and may write into the mods it was let into - nothing of them is laid back while it does");
			} else {
				a_ctx.Journal.Writers.Remove(w);
			}
		}
		a_ctx.Journal.TrySave();
		return gone;
	}

	// What the tool did to one folder, worked out without changing the run's record: the copies of files it left as they were
	// (the same bytes and the same time) are taken out, what it made new and what is kept is listed beside the copies. A_r is
	// then committed (Commit) - alone, or together with the other folders of one step
	public static (int Changed, int Removed, int Added) Prepare(RunContext a_ctx, RewriteRecord a_r)
	{
		int changed = 0, removed = 0;
		var kept = new List<string>();
		foreach (var copy in Directory.EnumerateFiles(a_r.Backup, "*", SearchOption.AllDirectories).ToList()) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var rel = Path.GetRelativePath(a_r.Backup, copy);
			var live = Path.Combine(a_r.Root, rel);
			if (!File.Exists(live)) {
				++removed;
				kept.Add(rel);
			} else if (AsItWas(copy, live)) {
				Files.Delete(copy);
			} else {
				++changed;
				kept.Add(rel);
			}
		}
		EmptyFoldersOut(a_r.Backup);
		// told apart by the list of before: one that does not read throws, and the step fails with every folder put back whole
		var before = Before(a_r);
		var added = ListOf(a_r.Root).Where(f => !before.Contains(f)).ToList();
		Files.WriteAtomic(AddedFile(a_r), string.Join("\n", added));
		Files.WriteAtomic(KeptFile(a_r), string.Join("\n", kept));
		return (changed, removed, added.Count);
	}

	// the folders prepared, settled in ONE write of the run's record: a crash or a Stop before it leaves every one of them to be
	// put back whole, never some settled and some not
	public static void Commit(RunContext a_ctx, IEnumerable<(RewriteRecord Record, (int Changed, int Removed, int Added) Did)> a_done)
	{
		a_ctx.Journal.SaveWith(() => {
			foreach (var (r, did) in a_done) {
				r.Added = did.Added;
				r.Kept = did.Changed + did.Removed;
				r.Settled = true;
			}
		});
	}

	// one folder prepared and settled (an optimizer keeps each mod it finished)
	public static (int Changed, int Removed, int Added) Settle(RunContext a_ctx, RewriteRecord a_r)
	{
		var did = Prepare(a_ctx, a_r);
		Commit(a_ctx, [(a_r, did)]);
		return did;
	}

	// A step that did not finish, or a run that was cut off: every folder a tool was let into back as it was - each copy over what
	// stands there now, what the tool made new moved out into the run's backup. a_settledToo: the folders of a failed step that
	// were settled already (a step whose folders go back as one). Returns the folders that could not be put back whole
	public static List<string> PutBack(RunContext a_ctx, string? a_step, bool a_settledToo = false)
	{
		var failed = new List<string>();
		var todo = a_ctx.Journal.Rewritten.Where(r => (a_step == null || r.Step == a_step) && (!r.Settled || a_settledToo) && !r.PutBack && !r.Lost).Reverse().ToList();
		if (todo.Count == 0) {
			return failed;
		}
		// never while a program the run started may still write into them: laid back now, its next save would undo it
		if (!WritersGone(a_ctx, a_step)) {
			return [.. todo.Select(r => r.Label)];
		}
		foreach (var r in todo) {
			try {
				if (r.Keeping) {
					r.PutBack = true;
					a_ctx.Journal.TrySave();
					continue;
				}
				if (BackupState(r.Backup) is false) {
					r.Lost = true;
					var lost = $"{r.Label}: the copies of its files are no longer in this run's backup ({r.Backup}), so what the tool changed could not be put back";
					a_ctx.Journal.Notes.Add(lost);
					a_ctx.Journal.TrySave();
					a_ctx.Log.Error(lost);
					a_ctx.Log.Simple("Note: " + lost);
					continue;
				}
				// (null: whether it is there cannot be told - the drive is away, or the folder will not be read: asked again later)
				if (BackupState(r.Backup) is null) {
					throw new IOException($"the backup {r.Backup} cannot be read just now");
				}
				var before = Before(r);
				var added = ListOf(r.Root).Where(f => !before.Contains(f)).ToList();
				LayBack(r, added, Path.Combine(a_ctx.BackupFolder, "unfinished"));
				r.PutBack = true;
				// the files are back: a record the disk refuses right now is not a put-back that failed
				a_ctx.Journal.TrySave();
				a_ctx.Log.Detail($"{r.Label}: its files put back as they were");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				failed.Add(r.Label);
				a_ctx.Log.Error($"{r.Label}: its files could NOT all be put back ({e.Message}) - their copies are in {r.Backup}");
			}
		}
		return failed;
	}

	// An undo: what a finished step let a tool change goes back to what it was before the run, the newest record first (a second
	// tool in the same folder kept the first one's result as its "before"). Each folder's copies and lists are checked first:
	// one that misses a copy or a list throws, and the undo stays to be done (pressed again once it is back)
	public static void Undo(Journal a_j, RunLog a_log, string a_aside)
	{
		foreach (var r in a_j.Rewritten.Where(r => r.Settled && !r.PutBack).Reverse().ToList()) {
			if (MissingCopy(r) is { } missing) {
				throw new IOException($"{r.Label}: its copy of {missing} is no longer in the run's backup - nothing of it was laid back");
			}
			var added = AddedOf(r);
			if (r.Kept > 0 || added.Count > 0) {
				LayBack(r, added, a_aside);
				a_log.Detail($"{r.Label}: its files back as they were before this run");
			}
			r.PutBack = true;
			a_j.Save();
		}
	}

	// did this run leave a tool's changes standing in a folder? (an undo has them to take back)
	public static bool Left(Journal a_j) => a_j.Rewritten.Any(r => r.Settled && !r.PutBack && r.Kept + r.Added > 0);

	// The folders whose files a run changed and still holds the old ones of - also one a run was cut off in (a tool may have
	// changed it, and its put-back is still to come), never one whose copies were still being made (no tool had started)
	public static IEnumerable<string> FoldersOf(Journal a_j) =>
		a_j.Rewritten.Where(r => !r.PutBack && !r.Lost && !r.Keeping && r.Root != "").Select(r => Path.GetFullPath(r.Root).TrimEnd('\\'));

	// copies an undo needs that are no longer there: each copy a settled folder kept, checked by name
	public static bool BackupGone(Journal a_j) => a_j.Rewritten.Any(r => r.Settled && !r.PutBack && MissingCopy(r) != null);

	// the first copy a settled folder kept that is not in the backup (null: all there, or none kept)
	static string? MissingCopy(RewriteRecord a_r)
	{
		if (a_r.Kept == 0) {
			return null;
		}
		if (!File.Exists(KeptFile(a_r))) {
			// (a folder settled before its copies were listed: its backup folder is all there is to check)
			return Directory.Exists(a_r.Backup) ? null : "any file";
		}
		try {
			return Files.ReadSharedText(KeptFile(a_r)).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(f => !File.Exists(Path.Combine(a_r.Backup, f)));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return "its list of copies";
		}
	}

	// whether a backup folder is there: true, false (surely gone - its drive is there and says so), or null (it cannot be told:
	// the drive is away, or the folder is there and will not be read - asked again later)
	static bool? BackupState(string a_dir)
	{
		try {
			return File.GetAttributes(a_dir).HasFlag(FileAttributes.Directory) ? true : false;
		} catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) {
			return Path.GetPathRoot(Path.GetFullPath(a_dir)) is { Length: > 0 } drive && Directory.Exists(drive) ? false : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// The copies stay where they are (a second try finds them again); a file that already holds its copy's bytes and time is left
	// alone. What the tool made new is moved out, never erased - first what stands in a copy's way (a file where a folder of the
	// copy's path must be, or a folder where the copy's file goes), then the rest
	static void LayBack(RewriteRecord a_r, IReadOnlyCollection<string> a_added, string a_aside)
	{
		var copies = Directory.EnumerateFiles(a_r.Backup, "*", SearchOption.AllDirectories).Select(c => (Copy: c, Rel: Path.GetRelativePath(a_r.Backup, c))).ToList();
		foreach (var (_, rel) in copies) {
			ClearWay(a_r, rel, a_aside);
		}
		foreach (var (copy, rel) in copies) {
			var live = Path.Combine(a_r.Root, rel);
			if (!File.Exists(live) || !AsItWas(copy, live)) {
				Files.CopyAtomic(copy, live);
			}
		}
		foreach (var rel in a_added) {
			var live = Path.Combine(a_r.Root, rel);
			if (!File.Exists(live)) {
				continue;
			}
			MoveAside(a_r, live, rel, a_aside);
		}
	}

	// what the tool put in the way of a copy's path moved aside: a file standing where a folder of the path must be, a folder
	// standing where the copy's own file goes
	static void ClearWay(RewriteRecord a_r, string a_rel, string a_aside)
	{
		var parts = a_rel.Split(Path.DirectorySeparatorChar);
		var at = a_r.Root;
		for (var i = 0; i < parts.Length - 1; ++i) {
			at = Path.Combine(at, parts[i]);
			if (File.Exists(at)) {
				MoveAside(a_r, at, Path.GetRelativePath(a_r.Root, at), a_aside);
				return;
			}
		}
		var live = Path.Combine(a_r.Root, a_rel);
		if (Directory.Exists(live)) {
			var dst = Files.FreeFile(Path.Combine(a_aside, a_r.Label, a_rel));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			Directory.Move(live, dst);
		}
	}

	static void MoveAside(RewriteRecord a_r, string a_live, string a_rel, string a_aside)
	{
		var dst = Files.FreeFile(Path.Combine(a_aside, a_r.Label, a_rel));
		Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
		Files.Move(a_live, dst);
	}

	// What stood under Root before the tool started. A list that is missing or does not read throws: read as "nothing stood
	// there", every file of the mod would count as the tool's and be moved out; read as "all of it", what the tool made new would
	// stay - either way a put-back that says it is done when it is not
	static HashSet<string> Before(RewriteRecord a_r) =>
		Files.ReadSharedText(BeforeFile(a_r)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);

	// the file as its copy holds it: the same bytes and the same time (a tool that saved the same bytes changed its date, which
	// the player's own tools may go by)
	static bool AsItWas(string a_copy, string a_live) => File.GetLastWriteTimeUtc(a_copy) == File.GetLastWriteTimeUtc(a_live) && Same(a_copy, a_live);

	// the same bytes? (the length first: most changed files differ there, and no byte is read)
	public static bool Same(string a_a, string a_b)
	{
		if (new FileInfo(a_a).Length != new FileInfo(a_b).Length) {
			return false;
		}
		const int Block = 1 << 16;
		using var fa = new FileStream(a_a, FileMode.Open, FileAccess.Read, FileShare.Read, Block, FileOptions.SequentialScan);
		using var fb = new FileStream(a_b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Block, FileOptions.SequentialScan);
		var ba = new byte[Block];
		var bb = new byte[Block];
		while (true) {
			var na = fa.ReadAtLeast(ba, Block, false);
			var nb = fb.ReadAtLeast(bb, Block, false);
			if (na != nb || !ba.AsSpan(0, na).SequenceEqual(bb.AsSpan(0, nb))) {
				return false;
			}
			if (na == 0) {
				return true;
			}
		}
	}

	// the folders under a_dir that hold nothing any more, deepest first; a_dir itself stays (it is how a record's backup is found)
	static void EmptyFoldersOut(string a_dir)
	{
		foreach (var d in Directory.EnumerateDirectories(a_dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length).ToList()) {
			try {
				if (!Directory.EnumerateFileSystemEntries(d).Any()) {
					Directory.Delete(d);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// an empty folder left in the backup takes no room
			}
		}
	}
}
