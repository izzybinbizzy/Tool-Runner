using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// A setup copied or renamed from the window's setup list. A copy is a new setup beside the first with the
// tools' settings it had learned; nothing of the first one changes. A rename takes everything of the setup along: every run
// record that names its file (History, the last runs, the usual times, "Run again from", Undo all find a setup by its file) and
// the settings learned from its tools' own windows.
public static class SetupFiles
{
	// What is wrong with a name a setup's file would take, in words; null = it can be used. a_self: the setup being renamed (its
	// own name in other letters is no clash)
	public static string? BadName(string a_setups, string a_name, string? a_self = null)
	{
		var n = a_name.Trim();
		if (n.Length == 0) {
			return "Type a name.";
		}
		if (n.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || n.EndsWith('.')) {
			return "A name cannot hold \\ / : * ? \" < > | or end with a dot.";
		}
		if (Setup.IsDeviceName(n)) {
			return $"Windows keeps the name \"{n}\" for itself.";
		}
		var file = Path.Combine(a_setups, n + ".json");
		if (Setup.IsStore(file)) {
			return "That name is one of the program's own files.";
		}
		if (File.Exists(file) && (a_self == null || !Files.SamePath(file, a_self))) {
			return $"A setup named \"{n}\" is there already.";
		}
		return null;
	}

	// a name for a copy that no setup has yet: "<name> (copy)", then "(copy 2)" ...
	public static string FreeCopyName(string a_file)
	{
		var dir = Path.GetDirectoryName(a_file) ?? "";
		var stem = Path.GetFileNameWithoutExtension(a_file);
		for (var i = 1; ; ++i) {
			var n = i == 1 ? $"{stem} (copy)" : $"{stem} (copy {i})";
			if (BadName(dir, n) == null) {
				return n;
			}
		}
	}

	static string Folder(string a_file) => Path.GetDirectoryName(a_file) ?? throw new ArgumentException("no folder: " + a_file);

	static string LearnedDir(string a_file) => Path.Combine(Folder(a_file), "learned settings", Path.GetFileNameWithoutExtension(a_file));

	// A copy of the setup under a_name, beside it, with the settings its tools had learned; returns the new file. Never writes
	// over a file that is there (a name taken a moment ago is refused)
	public static string Duplicate(string a_file, string a_name)
	{
		var dir = Folder(a_file);
		if (BadName(dir, a_name) is { } why) {
			throw new ArgumentException(why);
		}
		var to = Path.Combine(dir, a_name.Trim() + ".json");
		var learned = LearnedDir(a_file);
		var learnedTo = LearnedDir(to);
		// learned settings left under that name by a setup that is gone would be the copy's (they are found by the setup's file
		// name alone), whether or not the first setup has learned any
		if (Directory.Exists(learnedTo)) {
			throw new ArgumentException($"learned tool settings for \"{a_name.Trim()}\" are there already ({learnedTo}) - pick another name");
		}
		File.Copy(a_file, to, false);
		try {
			NameInside(to, a_file, a_name.Trim());
			if (Directory.Exists(learned)) {
				CopyTree(learned, learnedTo);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// a copy that did not come out whole is not left as a setup of its own - nor the part of its learned settings already
			// copied (that folder was not there before this copy: refused above)
			Files.TryDelete(to);
			try {
				if (Directory.Exists(learnedTo)) {
					Directory.Delete(learnedTo, true);
				}
			} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) {
				throw new IOException($"{e.Message} - and the part of its learned settings already copied could not be removed ({learnedTo}): delete that folder, then copy again", e);
			}
			throw;
		}
		return to;
	}

	// A setup file that carries a name of its own ("name") and called itself after its file keeps doing so under the new name;
	// true when that name was changed
	static bool NameInside(string a_file, string a_was, string a_name)
	{
		// (a "name" that is no text is left as it is: it was never the file's name)
		// (read and written as the setup itself is: a file with a comment or a last comma in it is a setup too)
		if (Setup.RootOf(Files.ReadSharedText(a_file)) is not { } root || root["name"] is not JsonValue v || !v.TryGetValue<string>(out var inside) ||
		    !string.Equals(inside, Path.GetFileNameWithoutExtension(a_was), StringComparison.OrdinalIgnoreCase)) {
			return false;
		}
		root["name"] = a_name;
		Files.WriteAtomic(a_file, Setup.TextOf(root));
		return true;
	}

	// a folder moved to another name, also to the same name in other letters (Windows moves a folder onto itself only by way of
	// another name)
	static void MoveFolder(string a_from, string a_to)
	{
		if (Files.SamePath(a_from, a_to)) {
			var between = a_from + " (renaming)";
			Directory.Move(a_from, between);
			a_from = between;
		}
		Directory.Move(a_from, a_to);
	}

	static void CopyTree(string a_from, string a_to)
	{
		Directory.CreateDirectory(a_to);
		foreach (var f in Directory.GetFiles(a_from)) {
			File.Copy(f, Path.Combine(a_to, Path.GetFileName(f)), false);
		}
		foreach (var d in Directory.GetDirectories(a_from)) {
			CopyTree(d, Path.Combine(a_to, Path.GetFileName(d)));
		}
	}

	public sealed record Renamed(string File, int Records, List<string> NotRepointed);

	// The setup renamed to a_name, everything of it along. a_runsDir: the program's runs folder. Refused (nothing changed) while a
	// run goes or one did not finish - a put-back still to be made reads the setup by the file its record names - and while
	// another program of ours holds the run lock. A record that could not be written is named in the answer (its run then
	// shows under no setup in History; nothing of the profile depends on it)
	public static Renamed Rename(string a_file, string a_name, string a_runsDir)
	{
		var dir = Folder(a_file);
		if (BadName(dir, a_name, a_file) is { } why) {
			throw new ArgumentException(why);
		}
		using var held = Runner.HoldIfFree() ?? throw new InvalidOperationException("a run is going - rename the setup when it has ended");
		var runs = new List<Journal>();
		if (Directory.Exists(a_runsDir)) {
			foreach (var d in Directory.GetDirectories(a_runsDir)) {
				try {
					// a folder without a record is no run; a folder that cannot be looked into is not taken for one without
					// (File.Exists says false for both)
					if (!Directory.EnumerateFiles(d, "journal.json", SearchOption.TopDirectoryOnly).Any()) {
						continue;
					}
					runs.Add(Journal.Load(d));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
					// A record that does not read may be an unfinished run's, whose put-back reads the setup by the file it names:
					// nothing moves until it reads
					throw new InvalidOperationException($"the run record in \"{d}\" could not be read ({e.Message}), so it is not known whether that run still needs this setup - close what holds it, then rename again", e);
				}
			}
		}
		if (runs.FirstOrDefault(j => j.Unfinished) is { } open) {
			throw new InvalidOperationException($"the run of {open.Started:MMM d HH:mm} did not finish - put the profile back first (the window's banner), then rename the setup");
		}
		var to = Path.Combine(dir, a_name.Trim() + ".json");
		var learned = LearnedDir(a_file);
		var learnedTo = LearnedDir(to);
		// (also when this setup has learned none: under the new name those leftovers would be taken for its own)
		if (!Files.SamePath(learned, learnedTo) && Directory.Exists(learnedTo)) {
			throw new InvalidOperationException($"a folder of learned tool settings for \"{a_name.Trim()}\" is there already ({learnedTo})");
		}
		File.Move(a_file, to);
		// its learned settings go with it and its own name inside follows, or the file and the folder go back to their old names:
		// the setup never stands half renamed (a name inside that did not write left both moved)
		var origin = LearnedDir(a_file);
		var folderMoved = false;
		var nameChanged = false;
		try {
			if (Directory.Exists(learned) && !string.Equals(learned, learnedTo, StringComparison.Ordinal)) {
				// the same name in other letters: Windows moves a folder onto itself only by way of another name
				if (Files.SamePath(learned, learnedTo)) {
					var between = learned + " (renaming)";
					Directory.Move(learned, between);
					learned = between;
				}
				Directory.Move(learned, learnedTo);
				folderMoved = true;
			}
			nameChanged = NameInside(to, a_file, a_name.Trim());
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
			Exception? stuck = null;
			try {
				// (a folder half way through a change of letters goes back to its old name too)
				if (folderMoved && Directory.Exists(learnedTo) && !Directory.Exists(origin)) {
					MoveFolder(learnedTo, origin);
				} else if (!string.Equals(learned, origin, StringComparison.Ordinal) && Directory.Exists(learned) && !Directory.Exists(origin)) {
					Directory.Move(learned, origin);
				}
				File.Move(to, a_file);
			} catch (Exception b) when (b is IOException or UnauthorizedAccessException) {
				stuck = b;
			}
			if (stuck == null) {
				throw new IOException($"it could not be renamed whole ({e.Message}) - the setup keeps its old name", e);
			}
			// The way back failed too: the setup stands under its new name. Its learned settings go with it and the run records
			// follow below, so the three are never left apart; what cannot follow is named with where each one is
			// (wherever the folder stands now: under its old name, or half way through a change of letters under its " (renaming)" one)
			var standing = new[] { origin, learned }.FirstOrDefault(d => Directory.Exists(d) && !Files.SamePath(d, learnedTo));
			try {
				if (standing != null && !Directory.Exists(learnedTo)) {
					Directory.Move(standing, learnedTo);
					// (on record as moved: a later way back for the run records takes it back with the setup)
					folderMoved = true;
				}
			} catch (Exception e3) when (e3 is IOException or UnauthorizedAccessException) {
				var records = runs.Where(j => Files.SamePath(j.SetupFile, a_file)).Select(j => Path.GetFileName(j.Folder)).ToList();
				throw new IOException($"it could not be renamed whole ({e.Message}), and could not take its old name back ({stuck.Message}): the setup is now \"{to}\" and its " +
					$"learned settings are still in \"{standing}\" - move that folder to \"{learnedTo}\"" +
					(records.Count == 0 ? "" : $"; these run records still name \"{a_file}\": {string.Join(", ", records)}"), e);
			}
		}
		// Every run record that names the setup follows it. One that cannot be written stops the rename: every record names the
		// old file again and the setup goes back to its old name, so no run loses its setup in History. Where that cannot be
		// done whole, the rename stands instead, every record is written with the new name once more, and each one that still
		// does not name it - read back off the disk, not taken from memory - is named in the answer
		var matching = runs.Where(j => Files.SamePath(j.SetupFile, a_file)).ToList();
		Journal? failed = null;
		foreach (var j in matching) {
			j.SetupFile = to;
			if (!j.TrySave()) {
				failed = j;
				break;
			}
		}
		if (failed == null) {
			return new Renamed(to, matching.Count, []);
		}
		var back = true;
		foreach (var j in matching) {
			j.SetupFile = a_file;
			// (a record never written with the new name needs nothing; one whose write failed may hold either: read back)
			if (!j.TrySave() && !Names(j.Folder, a_file)) {
				back = false;
			}
		}
		// the setup and its learned settings, where they ended up, when they could not be put together again (null: together)
		string? apart = null;
		if (back) {
			var wentBack = false;
			try {
				if (nameChanged) {
					NameInside(to, to, Path.GetFileNameWithoutExtension(a_file));
				}
				if (folderMoved && Directory.Exists(learnedTo) && !Directory.Exists(origin)) {
					MoveFolder(learnedTo, origin);
				}
				File.Move(to, a_file);
				wentBack = true;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
				// the setup could not go back: the rename stands, its learned settings and its own name with it, and the records
				// follow it below
				try {
					if (folderMoved && Directory.Exists(origin) && !Directory.Exists(learnedTo)) {
						MoveFolder(origin, learnedTo);
					}
					if (nameChanged) {
						NameInside(to, a_file, a_name.Trim());
					}
				} catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) {
					// never a rename said to have worked: the setup and its learned settings are named where they are
					var setupNow = File.Exists(to) ? to : a_file;
					var learnedNow = Directory.Exists(learnedTo) ? learnedTo : origin;
					apart = $"the setup is at \"{setupNow}\" and its learned settings are in \"{learnedNow}\" ({e2.Message}) - close what holds them, then rename the setup again";
				}
			}
			if (wentBack) {
				throw new IOException($"the run record in \"{failed.Folder}\" could not be written (is it open in another program?) - the setup keeps its old name; close what holds it, then rename again");
			}
		}
		var notRepointed = new List<string>();
		foreach (var j in matching) {
			j.SetupFile = to;
			if (!j.TrySave() && !Names(j.Folder, to)) {
				notRepointed.Add(j.Folder);
			}
		}
		if (apart != null) {
			throw new IOException("the rename could not be finished, nor taken back whole: " + apart +
				(notRepointed.Count == 0 ? "" : $"; the run records in {string.Join(", ", notRepointed.Select(f => "\"" + Path.GetFileName(f) + "\""))} still name its old file"));
		}
		return new Renamed(to, matching.Count - notRepointed.Count, notRepointed);
	}

	// the run record on disk in a_folder names a_setup (false: it names another, or does not read)
	static bool Names(string a_folder, string a_setup)
	{
		try {
			return Files.SamePath(Journal.Load(a_folder).SetupFile, a_setup);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
			return false;
		}
	}
}
