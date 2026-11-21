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

public static class Rewrites
{
	public const string Folder = "rewritten";

	// room left on the backup's disk once the copies are made: the run's record and log are written there too
	const long Spare = 64L << 20;

	// what stood under Root before the tool started, one path a line, beside the copies (never among them: a file of that name in
	// the mod would be taken for it)
	static string BeforeFile(RewriteRecord a_r) => a_r.Backup + " files before.txt";

	// what the tool made new under Root, written when the folder is settled
	static string AddedFile(RewriteRecord a_r) => a_r.Backup + " files added.txt";

	public static List<string> AddedOf(RewriteRecord a_r)
	{
		try {
			return File.Exists(AddedFile(a_r)) ? [.. Files.ReadSharedText(AddedFile(a_r)).Split('\n', StringSplitOptions.RemoveEmptyEntries)] : [];
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a list that cannot be read names nothing: nothing is moved out on a guess
			return [];
		}
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
		// files with no way back
		if (Files.FreeOn(a_ctx.BackupFolder) is { } free && free < bytes + Spare) {
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

	// The tool is done with this folder. Of the copies only those of files it changed or took away stay - the rest are the same
	// bytes as what stands there - and what it made new goes on record. Told apart by their bytes: a tool can write a file of the
	// same length and give it back its old time (xEdit does).
	public static (int Changed, int Removed, int Added) Settle(RunContext a_ctx, RewriteRecord a_r)
	{
		int changed = 0, removed = 0;
		foreach (var copy in Directory.EnumerateFiles(a_r.Backup, "*", SearchOption.AllDirectories).ToList()) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var live = Path.Combine(a_r.Root, Path.GetRelativePath(a_r.Backup, copy));
			if (!File.Exists(live)) {
				++removed;
			} else if (Same(copy, live)) {
				Files.Delete(copy);
			} else {
				++changed;
			}
		}
		EmptyFoldersOut(a_r.Backup);
		List<string> added = Before(a_r) is { } before ? ListOf(a_r.Root).Where(f => !before.Contains(f)).ToList() : [];
		// the list first, then the record that says the folder is settled: a crash between them leaves a folder that is put back whole
		Files.WriteAtomic(AddedFile(a_r), string.Join("\n", added));
		a_r.Added = added.Count;
		a_r.Kept = changed + removed;
		a_r.Settled = true;
		a_ctx.Journal.Save();
		return (changed, removed, added.Count);
	}

	// A step that did not finish, or a run that was cut off: every folder a tool was let into back as it was - each copy over what
	// stands there now, what the tool made new moved out into the run's backup. Returns the folders that could not be put back whole
	public static List<string> PutBack(RunContext a_ctx, string? a_step)
	{
		var failed = new List<string>();
		foreach (var r in a_ctx.Journal.Rewritten.Where(r => (a_step == null || r.Step == a_step) && !r.Settled && !r.PutBack && !r.Lost).Reverse().ToList()) {
			try {
				if (r.Keeping) {
					r.PutBack = true;
					a_ctx.Journal.TrySave();
					continue;
				}
				if (!Directory.Exists(r.Backup)) {
					// on a drive that is not there the copies may come back with the drive: asked again. In a backup that is there
					// they were cleared away, and counting that as "not back yet" would keep every later put-back from ending
					if (Path.GetPathRoot(Path.GetFullPath(r.Backup)) is { Length: > 0 } drive && !Directory.Exists(drive)) {
						throw new IOException($"the backup's drive {drive} is not there");
					}
					r.Lost = true;
					var lost = $"{r.Label}: the copies of its files are no longer in this run's backup ({r.Backup}), so what the tool changed could not be put back";
					a_ctx.Journal.Notes.Add(lost);
					a_ctx.Journal.TrySave();
					a_ctx.Log.Error(lost);
					a_ctx.Log.Simple("Note: " + lost);
					continue;
				}
				var added = Before(r) is { } before ? ListOf(r.Root).Where(f => !before.Contains(f)).ToList() : [];
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
	// tool in the same folder kept the first one's result as its "before")
	public static void Undo(Journal a_j, RunLog a_log, string a_aside)
	{
		foreach (var r in a_j.Rewritten.Where(r => r.Settled && !r.PutBack).Reverse().ToList()) {
			if (Directory.Exists(r.Backup)) {
				LayBack(r, AddedOf(r), a_aside);
			}
			r.PutBack = true;
			a_j.Save();
			a_log.Detail($"{r.Label}: its files back as they were before this run");
		}
	}

	// did this run leave a tool's changes standing in a folder? (an undo has them to take back)
	public static bool Left(Journal a_j) => a_j.Rewritten.Any(r => r.Settled && !r.PutBack && r.Kept + r.Added > 0);

	// the folders whose files a run changed and still holds the old ones of
	public static IEnumerable<string> FoldersOf(Journal a_j) =>
		a_j.Rewritten.Where(r => r.Settled && !r.PutBack && r.Root != "").Select(r => Path.GetFullPath(r.Root).TrimEnd('\\'));

	// copies an undo needs that are no longer there
	public static bool BackupGone(Journal a_j) => a_j.Rewritten.Any(r => r.Settled && !r.PutBack && r.Kept > 0 && !Directory.Exists(r.Backup));

	// The copies stay where they are (a second try finds them again); a file that already holds its copy's bytes is left alone.
	// What the tool made new is moved out, never erased.
	static void LayBack(RewriteRecord a_r, IReadOnlyCollection<string> a_added, string a_aside)
	{
		foreach (var copy in Directory.EnumerateFiles(a_r.Backup, "*", SearchOption.AllDirectories)) {
			var live = Path.Combine(a_r.Root, Path.GetRelativePath(a_r.Backup, copy));
			if (!File.Exists(live) || !Same(copy, live)) {
				Files.CopyAtomic(copy, live);
			}
		}
		foreach (var rel in a_added) {
			var live = Path.Combine(a_r.Root, rel);
			if (!File.Exists(live)) {
				continue;
			}
			var dst = Files.FreeFile(Path.Combine(a_aside, a_r.Label, rel));
			Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
			Files.Move(live, dst);
		}
	}

	static HashSet<string>? Before(RewriteRecord a_r)
	{
		try {
			return File.Exists(BeforeFile(a_r)) ? Files.ReadSharedText(BeforeFile(a_r)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// a list that cannot be read names nothing as new: nothing is moved out on a guess
			return null;
		}
	}

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
