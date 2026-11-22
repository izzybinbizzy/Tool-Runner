using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// A setup copied or renamed from the window's setup list (his checklist 635). A copy is a new setup beside the first with the
// tools' settings it had learned; nothing of the first one changes. A rename takes everything of the setup along: every run
// record that names its file (History, the last runs, the usual times, "Run again from", Undo all find a setup by its file) and
// the settings learned from its tools' own windows.
public static class SetupFiles
{
	static readonly string[] Reserved = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

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
		if (Reserved.Contains(n.Split('.')[0], StringComparer.OrdinalIgnoreCase)) {
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
		// learned settings left under that name by a setup that is gone are not mixed into the copy's
		if (Directory.Exists(learned) && Directory.Exists(LearnedDir(to))) {
			throw new ArgumentException($"learned tool settings for \"{a_name.Trim()}\" are there already ({LearnedDir(to)}) - pick another name");
		}
		File.Copy(a_file, to, false);
		try {
			NameInside(to, a_file, a_name.Trim());
			if (Directory.Exists(learned)) {
				CopyTree(learned, LearnedDir(to));
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
			// a copy that did not come out whole is not left as a setup of its own
			Files.TryDelete(to);
			throw;
		}
		return to;
	}

	// A setup file that carries a name of its own ("name") and called itself after its file keeps doing so under the new name
	static void NameInside(string a_file, string a_was, string a_name)
	{
		if (JsonNode.Parse(Files.ReadShared(a_file)) is not JsonObject root || root["name"]?.GetValue<string>() is not { } inside ||
		    !string.Equals(inside, Path.GetFileNameWithoutExtension(a_was), StringComparison.OrdinalIgnoreCase)) {
			return;
		}
		root["name"] = a_name;
		Files.WriteAtomic(a_file, JsonSerializer.SerializeToUtf8Bytes(root, new JsonSerializerOptions { WriteIndented = true }));
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
				if (!File.Exists(Path.Combine(d, "journal.json"))) {
					continue;
				}
				try {
					runs.Add(Journal.Load(d));
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) {
					// a record that does not read names no setup the window could show
				}
			}
		}
		if (runs.FirstOrDefault(j => j.Unfinished) is { } open) {
			throw new InvalidOperationException($"the run of {open.Started:MMM d HH:mm} did not finish - put the profile back first (the window's banner), then rename the setup");
		}
		var to = Path.Combine(dir, a_name.Trim() + ".json");
		var learned = LearnedDir(a_file);
		var learnedTo = LearnedDir(to);
		if (!Files.SamePath(learned, learnedTo) && Directory.Exists(learned) && Directory.Exists(learnedTo)) {
			throw new InvalidOperationException($"a folder of learned tool settings for \"{a_name.Trim()}\" is there already ({learnedTo})");
		}
		File.Move(a_file, to);
		// its learned settings go with it, or the file goes back to its old name: the two never part
		try {
			if (Directory.Exists(learned) && !string.Equals(learned, learnedTo, StringComparison.Ordinal)) {
				// the same name in other letters: Windows moves a folder onto itself only by way of another name
				if (Files.SamePath(learned, learnedTo)) {
					var between = learned + " (renaming)";
					Directory.Move(learned, between);
					learned = between;
				}
				Directory.Move(learned, learnedTo);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			// (a folder half way through a change of letters goes back to its old name too)
			var origin = LearnedDir(a_file);
			if (!string.Equals(learned, origin, StringComparison.Ordinal) && Directory.Exists(learned) && !Directory.Exists(origin)) {
				Directory.Move(learned, origin);
			}
			File.Move(to, a_file);
			throw new IOException($"its learned tool settings could not be moved ({e.Message}) - the setup keeps its old name", e);
		}
		NameInside(to, a_file, a_name.Trim());
		var moved = 0;
		var notRepointed = new List<string>();
		foreach (var j in runs.Where(j => Files.SamePath(j.SetupFile, a_file))) {
			j.SetupFile = to;
			if (j.TrySave()) {
				++moved;
			} else {
				notRepointed.Add(j.Folder);
			}
		}
		return new Renamed(to, moved, notRepointed);
	}
}
