using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// The program's data folder after the rename: one made under the old name is taken over by renaming it (same drive, nothing
// copied), and every path inside its setups and run records that points into the old folder is pointed into the new one.
public static class DataFolder
{
	const string Pending = ".renamed from.txt";

	// -> the folder to use. Never moves a folder an old copy of the program may still be writing (a_oldRunning), never takes
	// a non-empty folder of the new name over, and finishes a repoint an earlier start did not complete.
	public static string Adopt(string a_parent, string a_suffix, Func<bool>? a_oldRunning = null)
	{
		var now = Path.Combine(a_parent, Brand.Name + a_suffix);
		var pending = Path.Combine(now, Pending);
		if (File.Exists(pending)) {
			var source = File.ReadAllText(pending).Trim();
			// A copy made by a move that never counted (its source still holds the data, with no mark of a move): neither
			// repointed nor used - the data is still where it came from, and the move can be made again from there
			if (!Files.SamePath(source, now) && HasData(source) && MovedTo(source) == null) {
				return source;
			}
			Repoint(now, source);
		}
		var olds = Brand.OldNames.Select(n => Path.Combine(a_parent, n + a_suffix)).Where(Directory.Exists).ToList();
		if (olds.Count == 0) {
			return now;
		}
		if (Directory.Exists(now)) {
			if (Directory.EnumerateFileSystemEntries(now).Any()) {
				return now;
			}
			try {
				Directory.Delete(now);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				return now;
			}
		}
		var old = olds[0];
		if ((a_oldRunning ?? OldRunning)()) {
			return old;
		}
		// the mark goes in BEFORE the move and travels with the folder, so a stop at any moment after it leaves a repoint to finish
		try {
			Files.WriteAtomic(Path.Combine(old, Pending), old);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return old;
		}
		try {
			// one rename or nothing: moved file by file (an old folder that is a junction to another disk), a file held half way
			// left the setups and the run records split over two folders, and no later start took up the rest
			for (var again = 0; ; ++again) {
				try {
					Directory.Move(old, now);
					break;
				} catch (IOException) when (again < 4) {
					Thread.Sleep(150);
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			try {
				Files.Delete(Path.Combine(old, Pending));
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
			}
			return old;
		}
		Repoint(now, old);
		return now;
	}

	// a copy of the program under an old name is running: its program file, or the old-name run lock held without the new-name
	// one (this version holds both while it runs)
	static bool OldRunning()
	{
		foreach (var n in Brand.OldNames) {
			var ps = Procs.ByImage(n + ".exe");
			ps.ForEach(p => p.Dispose());
			if (ps.Count > 0) {
				return true;
			}
		}
		static bool Open(string a_name)
		{
			if (Mutex.TryOpenExisting(a_name, out var m)) {
				m.Dispose();
				return true;
			}
			return false;
		}
		return !Open(Runner.MutexName) && Brand.OldNames.Any(n => Open("Local\\" + n + " run"));
	}

	// a data folder under this name or an older one stands in a_parent (with a_suffix: " Data" beside the program)
	public static bool StandsIn(string a_parent, string a_suffix) =>
		Brand.AllNames.Any(n => Directory.Exists(Path.Combine(a_parent, n + a_suffix)));

	// the folder holds something (a data folder that was only made, and never used, does not count)
	public static bool HasData(string a_dir)
	{
		try {
			return Directory.Exists(a_dir) && Directory.EnumerateFileSystemEntries(a_dir).Any();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	// The file that says the player chose this folder for every copy of the program. An app-data folder can hold data without it
	// (a copy that could not write beside itself kept its data there): only the player's choice sends a new copy to it
	public const string ChosenMark = "used by every copy.txt";

	public static bool Chosen(string a_dir) => File.Exists(Path.Combine(a_dir, ChosenMark));

	public static void Choose(string a_dir)
	{
		Directory.CreateDirectory(a_dir);
		Files.WriteAtomic(Path.Combine(a_dir, ChosenMark), "Every copy of " + Brand.Name + " on this PC keeps its data here (Settings > Program).\r\n");
	}

	// The mark in an old data folder that says its data moved, and where: the one moment the move counts. From then on every
	// start goes to the new folder (AppPaths: MovedTo), and a start that finds the old folder still there removes it.
	public const string MovedMark = ".moved to.txt";

	// where the data of a_dir moved to (null: it did not move)
	public static string? MovedTo(string a_dir)
	{
		try {
			var mark = Path.Combine(a_dir, MovedMark);
			return File.Exists(mark) && File.ReadAllText(mark).Trim() is { Length: > 0 } to ? to : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// a start found an old folder whose data moved: the new folder is marked the player's choice (a move cut off right after
	// its mark), and what is left of the old one goes - a part that will not go only takes room, and is tried again next start
	public static void FinishMove(string a_old, string a_to)
	{
		if (!Chosen(a_to)) {
			Choose(a_to);
		}
		try {
			RemoveMoved(a_old);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
	}

	// two files hold the same bytes
	static bool SameBytes(string a_a, string a_b)
	{
		using var a = new FileStream(a_a, FileMode.Open, FileAccess.Read, FileShare.Read);
		using var b = new FileStream(a_b, FileMode.Open, FileAccess.Read, FileShare.Read);
		var x = new byte[1 << 16];
		var y = new byte[1 << 16];
		while (true) {
			var n = a.ReadAtLeast(x, x.Length, false);
			var m = b.ReadAtLeast(y, y.Length, false);
			if (n != m || !x.AsSpan(0, n).SequenceEqual(y.AsSpan(0, m))) {
				return false;
			}
			if (n == 0) {
				return true;
			}
		}
	}

	// The data folder moved to a_to (Windows' own app-data folder, so a new version unzipped anywhere finds the run history,
	// the setups and the learned settings). Copied whole first and every file read back byte for byte, then the paths inside its
	// files repointed - all of them, or the copy is taken away again and the data stays where it was. Only then does the move
	// count: the old folder gets its mark (MovedMark), the new one is chosen, and the old one is removed. Refused when a_to holds
	// anything but an earlier copy of this same move that never counted, or lies inside the data folder or holds it. The caller
	// holds the run lock, and the program starts again afterwards (it runs with the old folder until then). Returns what is left
	// to do by hand (null: nothing) - the old folder that could not all be removed is then removed by the next start.
	public static string? MoveTo(string a_from, string a_to)
	{
		var from = Path.GetFullPath(a_from);
		var to = Path.GetFullPath(a_to);
		// (by where each one really is: a link, a junction or another name for the same disk can make a folder that looks apart
		// from the data folder one inside it, which the copy would fill and the removal of the old folder empty again)
		if (Directory.Exists(to) && Files.IsLink(to)) {
			throw new InvalidOperationException($"{to} is a link to another folder - pick the folder itself");
		}
		// (a folder Windows will not name, or one on a network folder - another PC's share may be this data folder under
		// another name - is no place whose relation to the data can be known: refused)
		if (Physical(from) is not { } realFrom || Physical(to) is not { } realTo) {
			throw new InvalidOperationException($"where {to} really is could not be told apart from the data folder - pick a folder on this PC");
		}
		// (a network path can be a folder of this very PC - \\localhost\C$\... - under a name no comparison knows: neither side
		// may be one)
		if (realTo.StartsWith(@"\\", StringComparison.Ordinal)) {
			throw new InvalidOperationException($"{to} is a network folder - pick a folder on this PC");
		}
		if (realFrom.StartsWith(@"\\", StringComparison.Ordinal)) {
			throw new InvalidOperationException($"the data is in a network folder ({from}) - its relation to {to} cannot be told; move it by hand");
		}
		if (Files.SamePath(from, to) || Files.SamePath(realFrom, realTo)) {
			throw new InvalidOperationException("the data is in that folder already");
		}
		// a folder inside the data, or one that holds it, would be copied into itself and then removed with it
		if (Files.IsUnder(to, from) || Files.IsUnder(from, to) || Files.IsUnder(realTo, realFrom) || Files.IsUnder(realFrom, realTo)) {
			throw new InvalidOperationException($"{to} is inside the data folder, or holds it - pick a folder apart from it");
		}
		// a copy of this very move that was cut off before it counted holds nothing of the player's: it is made again
		if (HasData(to) && !Chosen(to) && File.Exists(Path.Combine(to, Pending)) && Files.SamePath(File.ReadAllText(Path.Combine(to, Pending)).Trim(), from)) {
			Files.DeleteDir(to);
		}
		if (HasData(to)) {
			throw new InvalidOperationException($"{to} holds files already - move them away, then try again");
		}
		// A folder inside the data that is a link to another folder would be copied as the folder it leads to (and that folder's
		// files would then stand twice): not moved while one is there
		var (dirs, files, links) = Tree(from);
		if (links.Count > 0) {
			throw new InvalidOperationException($"{links[0]} inside the data folder is a link to another folder - move it out of the data folder, then try again");
		}
		// (an old rename's mark of the old folder is its own business: the copy carries this move's mark in its place)
		var want = files.Where(f => !Files.SamePath(f, Path.Combine(from, Pending)))
			.ToDictionary(f => Path.GetRelativePath(from, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
		// The copy is made in a folder of the move's own beside the new place, and takes the new place's name only once it is
		// whole, read back and repointed: a stop at any moment leaves the new place as it was, and taking a copy back removes
		// only what this move made. One left by a move that was cut off is this move's too, and goes first
		var staging = to + " (being copied)";
		if (Directory.Exists(staging)) {
			// (only a folder this move made goes: one whose mark names this data folder, or one that holds nothing but that mark
			// half written - anything else in it is someone's, and the move waits for it to be moved away)
			if (Files.IsLink(staging) || !LeftByThisMove(staging, from)) {
				throw new InvalidOperationException($"{staging} is in the way - move it away, then try again");
			}
			Files.DeleteDir(staging);
		}
		[System.Diagnostics.CodeAnalysis.DoesNotReturn]
		void TakeBack(string a_copy, Exception a_why, string a_what)
		{
			try {
				Files.DeleteDir(a_copy);
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				throw new IOException($"{a_what} ({a_why.Message}), and the part already copied could not be removed again ({a_copy}) - delete that folder yourself; your data is still where it was", a_why);
			}
			throw new IOException($"{a_what} ({a_why.Message}) - it is still where it was", a_why);
		}
		try {
			Directory.CreateDirectory(staging);
			// the copy says where it came from before anything is in it: a copy cut off half way is known for what it is
			Files.WriteAtomic(Path.Combine(staging, Pending), from);
			foreach (var d in dirs) {
				Directory.CreateDirectory(Path.Combine(staging, Path.GetRelativePath(from, d)));
			}
			foreach (var rel in want.Keys) {
				Files.Copy(Path.Combine(from, rel), Path.Combine(staging, rel), false);
			}
			// each copy read back against its file, byte for byte, before anything of the old folder goes (its size alone says
			// nothing of what was written)
			foreach (var (rel, size) in want) {
				var copy = new FileInfo(Path.Combine(staging, rel));
				if (!copy.Exists || copy.Length != size || !SameBytes(Path.Combine(from, rel), copy.FullName)) {
					throw new IOException($"{rel} did not copy whole");
				}
			}
			// (repointed to where it will stand; it keeps its mark of where it came from until the move counts)
			if (!Repoint(staging, from, true, to)) {
				throw new IOException("a file of the copy could not be updated to its new place");
			}
			// its place: the new folder's name, only while nothing else is there (an empty folder of that name gives way)
			if (Directory.Exists(to)) {
				if (HasData(to)) {
					throw new IOException($"{to} was filled by something else while the data was copied");
				}
				Directory.Delete(to);
			}
			Directory.Move(staging, to);
			AfterPublish?.Invoke(to);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			TakeBack(staging, e, "the data could not be moved");
		}
		// the move counts from here: one mark in the old folder
		try {
			Files.WriteAtomic(Path.Combine(from, MovedMark), to);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// (the copy has its place by now, where another program may have put something since: only what this move put
			// there goes)
			try {
				RemoveCopy(to, from, dirs, want.Keys);
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				throw new IOException($"the old folder could not be marked as moved ({e.Message}), and the copy could not all be removed again ({to}) - delete what is left of it yourself; your data is still where it was", e);
			}
			throw new IOException($"the old folder could not be marked as moved ({e.Message}) - it is still where it was", e);
		}
		// what is left after the mark never takes the move back: a start that finds the mark finishes both
		try {
			Files.Delete(Path.Combine(to, Pending));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// (the move counts already: a mark left there only makes the next start repoint once more - harmless)
		}
		try {
			Choose(to);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"The new folder could not be marked yet ({e.Message}); the next start does it and removes {from}.";
		}
		try {
			RemoveMoved(from);
			return null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"The old folder could not all be removed ({e.Message}); the next start removes what is left of {from}.";
		}
	}

	// the offline tests only: called the moment the copy has taken its place
	public static Action<string>? AfterPublish { get; set; }

	// a staging folder a cut-off move of a_from left: its mark names a_from, or it holds nothing but that mark half written
	static bool LeftByThisMove(string a_staging, string a_from)
	{
		var mark = Path.Combine(a_staging, Pending);
		if (File.Exists(mark)) {
			return Files.SamePath(File.ReadAllText(mark).Trim(), a_from);
		}
		var half = "." + Pending + ".";
		return !Directory.EnumerateDirectories(a_staging).Any() &&
			Directory.EnumerateFiles(a_staging).All(f => Path.GetFileName(f).StartsWith(half, StringComparison.Ordinal) && f.EndsWith(".tmp", StringComparison.Ordinal));
	}

	// a copy that took its place taken away again: only the files and folders this move put there (and its mark); a folder that
	// holds anything else stays with it
	static void RemoveCopy(string a_to, string a_from, List<string> a_dirs, IEnumerable<string> a_files)
	{
		foreach (var rel in a_files) {
			Files.Delete(Path.Combine(a_to, rel));
		}
		Files.Delete(Path.Combine(a_to, Pending));
		foreach (var d in a_dirs.Select(d => Path.Combine(a_to, Path.GetRelativePath(a_from, d))).OrderByDescending(d => d.Length).Append(a_to)) {
			if (Directory.Exists(d) && !Directory.EnumerateFileSystemEntries(d).Any()) {
				Directory.Delete(d);
			}
		}
	}

	// every folder and file under a_dir, a folder that is a link (a junction, a symbolic link) named and never walked into
	static (List<string> Dirs, List<string> Files, List<string> Links) Tree(string a_dir)
	{
		var dirs = new List<string>();
		var files = new List<string>();
		var links = new List<string>();
		var todo = new Stack<string>();
		todo.Push(a_dir);
		while (todo.Count > 0) {
			var d = todo.Pop();
			files.AddRange(Directory.EnumerateFiles(d));
			foreach (var sub in Directory.EnumerateDirectories(d)) {
				if (ToolRunner.Files.IsLink(sub)) {
					links.Add(sub);
				} else {
					dirs.Add(sub);
					todo.Push(sub);
				}
			}
		}
		return (dirs, files, links);
	}

	// The old folder of a move that counts, emptied with its mark going LAST: while anything of it is left, the mark stays, and
	// the next start finishes the move instead of taking what is left for the data (a link inside goes as the link)
	static void RemoveMoved(string a_old)
	{
		if (!Directory.Exists(a_old)) {
			return;
		}
		var mark = Path.Combine(a_old, MovedMark);
		foreach (var e in Directory.EnumerateFileSystemEntries(a_old).ToList()) {
			if (Files.SamePath(e, mark)) {
				continue;
			}
			if (!Directory.Exists(e)) {
				Files.Delete(e);
			} else if (Files.IsLink(e)) {
				Directory.Delete(e);
			} else {
				Files.DeleteDir(e);
			}
		}
		// Only the mark is left. The folder leaves its data-folder name first: a stop between the mark and the folder going
		// would leave an empty folder under that name, which a start takes for the data. Under another name it is no data
		// folder of any copy, whatever happens next
		var going = a_old + " (being removed " + Guid.NewGuid().ToString("N")[..8] + ")";
		Directory.Move(a_old, going);
		Files.Delete(Path.Combine(going, MovedMark));
		Directory.Delete(going);
	}

	// Where a path really is: its deepest part that is there named as Windows names it (junctions, links, SUBST drive letters
	// and mapped network drives followed - Files.FinalPath), the parts not there yet added as written. Null when Windows will
	// not name it: where the folder is cannot then be known
	// Windows' own name for a folder that is there (Files.FinalPath); the offline tests give a network answer through it, since
	// no share of this PC can be counted on to open
	public static Func<string, string?> FinalPathOf { get; set; } = Files.FinalPath;

	static string? Physical(string a_path)
	{
		var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_path));
		var rest = new Stack<string>();
		var at = full;
		while (!Directory.Exists(at) && Path.GetDirectoryName(at) is { } up) {
			rest.Push(Path.GetFileName(at));
			at = up;
		}
		if (!Directory.Exists(at) || FinalPathOf(at) is not { } real) {
			return null;
		}
		foreach (var part in rest) {
			real = Path.Combine(real, part);
		}
		return Path.TrimEndingDirectorySeparator(real);
	}

	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// every JSON file under the folder: a string value that is the old folder, or a path inside it, is written under the new one
	// (compared as values, so escaping and letters outside English never matter; a sibling folder sharing the name is left).
	// True when every file that could hold such a path was read and written; false leaves the mark for the next start
	// a_as: where the folder will stand when that is not where it is now (a copy made beside its place)
	public static bool Repoint(string a_now, string a_old, bool a_keepMark = false, string? a_as = null)
	{
		var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_old));
		var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_as ?? a_now));
		var failed = false;
		foreach (var f in Directory.EnumerateFiles(a_now, "*.json", SearchOption.AllDirectories)) {
			try {
				var text = Files.ReadSharedText(f);
				JsonNode? node;
				try {
					// (as every setup is read: a comment or a comma after the last value is no reason to skip it)
					node = JsonNode.Parse(text, documentOptions: Setup.ReadOptions);
				} catch (JsonException) {
					// not JSON this can read: fine unless it names the old folder, which it would go on naming after the move
					if (NamesFolder(text, from)) {
						failed = true;
					}
					continue;
				}
				if (node != null && Swap(node, from, to)) {
					Files.WriteAtomic(f, node.ToJsonString(WriteOptions));
				}
			} catch (IOException) {
				// locked or unreadable now: the mark stays, the next start tries it again
				failed = true;
			} catch (UnauthorizedAccessException) {
				failed = true;
			}
		}
		if (failed || a_keepMark) {
			return !failed;
		}
		try {
			Files.Delete(Path.Combine(a_now, Pending));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// the next start repoints once more - harmless
		}
		return true;
	}

	static bool Swap(JsonNode a_node, string a_from, string a_to)
	{
		var changed = false;
		switch (a_node) {
			case JsonObject o:
				foreach (var k in o.Select(p => p.Key).ToList()) {
					if (o[k] is JsonValue v && v.TryGetValue<string>(out var s) && Moved(s, a_from, a_to) is { } n) {
						o[k] = n;
						changed = true;
					} else if (o[k] is { } child) {
						changed |= Swap(child, a_from, a_to);
					}
				}
				break;
			case JsonArray a:
				for (var i = 0; i < a.Count; i++) {
					if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && Moved(s, a_from, a_to) is { } n) {
						a[i] = n;
						changed = true;
					} else if (a[i] is { } child) {
						changed |= Swap(child, a_from, a_to);
					}
				}
				break;
		}
		return changed;
	}

	// A full path, resolved as Windows would before it is compared ("/" or "\\", and ".." taken back): a value that only
	// looks as if it lies in the folder ("<folder>/../MO2") is left as it is. Anything that is no full path is no path to move
	static string? Moved(string a_path, string a_from, string a_to)
	{
		string p;
		try {
			if (!Path.IsPathFullyQualified(a_path)) {
				return null;
			}
			p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_path));
		} catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) {
			return null;
		}
		var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_from));
		if (p.Equals(f, StringComparison.OrdinalIgnoreCase)) {
			return a_to;
		}
		return p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p[f.Length] == '\\'
			? a_to + p[f.Length..]
			: null;
	}

	// a text names the folder or a path in it, written either way and as JSON escapes it
	static bool NamesFolder(string a_text, string a_folder) =>
		a_text.Replace("\\\\", "\\").Replace('/', '\\').Contains(a_folder.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
}
