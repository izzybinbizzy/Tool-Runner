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
		if (File.Exists(Path.Combine(now, Pending))) {
			Repoint(now, File.ReadAllText(Path.Combine(now, Pending)).Trim());
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
			Files.DeleteDir(a_old);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
		}
	}

	// The data folder moved to a_to (Windows' own app-data folder, so a new version unzipped anywhere finds the run history,
	// the setups and the learned settings). Copied whole first and read back by every file's size, then the paths inside its
	// files repointed - all of them, or the copy is taken away again and the data stays where it was. Only then does the move
	// count: the old folder gets its mark (MovedMark), the new one is chosen, and the old one is removed. Refused when a_to holds
	// anything but an earlier copy of this same move that never counted. The caller holds the run lock, and the program starts
	// again afterwards (it runs with the old folder until then). Returns what is left to do by hand (null: nothing) - the old
	// folder that could not all be removed is then removed by the next start.
	public static string? MoveTo(string a_from, string a_to)
	{
		var from = Path.GetFullPath(a_from);
		var to = Path.GetFullPath(a_to);
		if (Files.SamePath(from, to)) {
			throw new InvalidOperationException("the data is in that folder already");
		}
		// a copy of this very move that was cut off before it counted holds nothing of the player's: it is made again
		if (HasData(to) && !Chosen(to) && File.Exists(Path.Combine(to, Pending)) && Files.SamePath(File.ReadAllText(Path.Combine(to, Pending)).Trim(), from)) {
			Files.DeleteDir(to);
		}
		if (HasData(to)) {
			throw new InvalidOperationException($"{to} holds files already - move them away, then try again");
		}
		// (an old rename's mark of the old folder is its own business: the copy carries this move's mark in its place)
		var want = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Where(f => !Files.SamePath(f, Path.Combine(from, Pending)))
			.ToDictionary(f => Path.GetRelativePath(from, f), f => new FileInfo(f).Length, StringComparer.OrdinalIgnoreCase);
		[System.Diagnostics.CodeAnalysis.DoesNotReturn]
		void TakeBack(Exception a_why, string a_what)
		{
			try {
				Files.DeleteDir(to);
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				throw new IOException($"{a_what} ({a_why.Message}), and the part already copied could not be removed again ({to}) - delete that folder yourself; your data is still where it was", a_why);
			}
			throw new IOException($"{a_what} ({a_why.Message}) - it is still where it was", a_why);
		}
		try {
			Directory.CreateDirectory(to);
			// the copy says where it came from before anything is in it: a copy cut off half way is known for what it is
			Files.WriteAtomic(Path.Combine(to, Pending), from);
			foreach (var d in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) {
				Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, d)));
			}
			foreach (var rel in want.Keys) {
				Files.Copy(Path.Combine(from, rel), Path.Combine(to, rel), false);
			}
			foreach (var (rel, size) in want) {
				var copy = new FileInfo(Path.Combine(to, rel));
				if (!copy.Exists || copy.Length != size) {
					throw new IOException($"{rel} did not copy whole");
				}
			}
			if (!Repoint(to, from)) {
				throw new IOException("a file of the copy could not be updated to its new place");
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			TakeBack(e, "the data could not be moved");
		}
		// the move counts from here: one mark in the old folder
		try {
			Files.WriteAtomic(Path.Combine(from, MovedMark), to);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			TakeBack(e, "the old folder could not be marked as moved");
		}
		// what is left after the mark never takes the move back: a start that finds the mark finishes both
		try {
			Choose(to);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"The new folder could not be marked yet ({e.Message}); the next start does it and removes {from}.";
		}
		try {
			Files.DeleteDir(from);
			return null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return $"The old folder could not all be removed ({e.Message}); the next start removes what is left of {from}.";
		}
	}

	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// every JSON file under the folder: a string value that is the old folder, or a path inside it, is written under the new one
	// (compared as values, so escaping and letters outside English never matter; a sibling folder sharing the name is left).
	// True when every file that could hold such a path was read and written; false leaves the mark for the next start
	public static bool Repoint(string a_now, string a_old)
	{
		var from = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_old));
		var to = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a_now));
		var failed = false;
		foreach (var f in Directory.EnumerateFiles(a_now, "*.json", SearchOption.AllDirectories)) {
			try {
				var node = JsonNode.Parse(Files.ReadSharedText(f));
				if (node != null && Swap(node, from, to)) {
					Files.WriteAtomic(f, node.ToJsonString(WriteOptions));
				}
			} catch (IOException) {
				// locked or unreadable now: the mark stays, the next start tries it again
				failed = true;
			} catch (UnauthorizedAccessException) {
				failed = true;
			} catch (JsonException) {
				// not JSON this can read: nothing in it to repoint, and trying again would never change that
			}
		}
		if (failed) {
			return false;
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

	static string? Moved(string a_path, string a_from, string a_to)
	{
		if (a_path.Equals(a_from, StringComparison.OrdinalIgnoreCase)) {
			return a_to;
		}
		return a_path.Length > a_from.Length && a_path.StartsWith(a_from, StringComparison.OrdinalIgnoreCase) && a_path[a_from.Length] is '\\' or '/'
			? a_to + a_path[a_from.Length..]
			: null;
	}
}
