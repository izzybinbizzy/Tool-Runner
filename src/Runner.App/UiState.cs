using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner.App;

// What the window remembers about itself from one start to the next: its own size, the sizes of its pop-out windows, whether
// the log is hidden. One small file in the data folder; a file that does not read is simply not used, a value that cannot be
// kept is simply not kept, and nothing is read or written for the off-screen pictures.
public static class UiState
{
	static readonly Lock Gate = new();
	// one write at a time, each of the state as it is by then: the file never goes back to an older one
	static readonly Lock Disk = new();
	static JsonObject? s_state;
	// something is kept that the file does not hold yet / a write of it is on its way / a write is under way this moment
	static bool s_unsaved, s_queued, s_writing;
	// the closing window has made its last write, and its one more try (SaveAtClose)
	static bool s_closed;
	// a width and a height kept together are one write
	static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

	public static string File => FileForTests ?? Path.Combine(s_movedTo ?? AppPaths.Data, "window.json");

	// the data folder moved while the program runs (Settings > Keep data through updates): what the window still writes before it
	// closes goes there, never into the old folder - written there, it would stand again and the next start would take it
	static volatile string? s_movedTo;

	// The data folder moved by a_move to a_to with no write of the window's in between: what is kept goes into the old folder
	// first (it moves along), and a write that comes during the move waits and then goes to the new place. One that landed
	// between the old folder's removal and the switch would make a data folder beside the program again, and the next start
	// would take that nearly empty one
	public static void MoveWith(Action a_move, string a_to)
	{
		lock (Disk) {
			Save();
			a_move();
			s_movedTo = a_to;
		}
	}

	// the program's data was removed (Settings > Remove program data): nothing is written from then on
	static bool s_removed;

	// The program's data removed by a_remove with no write of the window's in between. When it says all of it went, nothing
	// the window remembers is written again: the window closes next, and its sizes written as it closes would make the data
	// folder anew with this one file in it. Hands on what a_remove said
	public static bool RemoveWith(Func<bool> a_remove)
	{
		lock (Disk) {
			if (!a_remove()) {
				return false;
			}
			lock (Gate) {
				s_removed = true;
				s_unsaved = false;
			}
			return true;
		}
	}

	// the offline tests keep it in a file of their own (and start from nothing kept, before and after)
	public static string? FileForTests
	{
		get;
		set {
			lock (Gate) {
				field = value;
				s_state = null;
				s_unsaved = false;
				s_closed = false;
				s_removed = false;
			}
		}
	}

	static JsonObject State()
	{
		if (s_state != null) {
			return s_state;
		}
		s_state = [];
		if (App.Headless) {
			return s_state;
		}
		try {
			if (System.IO.File.Exists(File) && JsonNode.Parse(Files.ReadShared(File)) is JsonObject read) {
				s_state = read;
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) {
		}
		return s_state;
	}

	public static double? Number(string a_key)
	{
		lock (Gate) {
			return State()[a_key] is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) ? d : null;
		}
	}

	public static bool Flag(string a_key, bool a_unlessSet = false)
	{
		lock (Gate) {
			return State()[a_key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : a_unlessSet;
		}
	}

	// a word kept ("" when none is: the version last started, for one)
	public static string Text(string a_key)
	{
		lock (Gate) {
			return State()[a_key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
		}
	}

	public static void Set(string a_key, string a_value) => Keep(a_key, JsonValue.Create(a_value));

	public static void Set(string a_key, double a_value) => Keep(a_key, JsonValue.Create(Math.Round(a_value)));

	public static void Set(string a_key, bool a_value) => Keep(a_key, JsonValue.Create(a_value));

	static void Keep(string a_key, JsonNode a_value)
	{
		lock (Gate) {
			var state = State();
			if (state[a_key]?.ToJsonString() == a_value.ToJsonString()) {
				return;
			}
			state[a_key] = a_value;
			if (App.Headless || s_removed) {
				return;
			}
			s_unsaved = true;
			if (s_queued) {
				return;
			}
			s_queued = true;
		}
		// The value is kept at once; the file is written a moment later and never on the window's own thread - a disk that
		// answers slowly holds up no click and no line of the log
		_ = Task.Run(async () => {
			await Task.Delay(Settle).ConfigureAwait(false);
			Save();
		});
	}

	// What is kept and not yet in the file goes there now: a moment after a change (Keep), and as the program closes, so the
	// last change is not lost with the process.
	public static void Save()
	{
		lock (Disk) {
			byte[] text;
			lock (Gate) {
				s_queued = false;
				if (!s_unsaved) {
					return;
				}
				s_unsaved = false;
				s_writing = true;
				text = System.Text.Encoding.UTF8.GetBytes(State().ToJsonString());
			}
			try {
				var file = File;
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				Files.WriteAtomic(file, text);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// not in the file after all: the next write - the next change, or the one as the program closes - takes it
				// along (left as "written", a disk that was full for a moment lost the window's sizes for good)
				lock (Gate) {
					s_unsaved = true;
				}
			} finally {
				lock (Gate) {
					s_writing = false;
				}
			}
		}
	}

	// The closing window's last write, made on a worker and waited for: what is kept goes into the file, and once more when
	// that write failed. From then on the program's end leaves the file alone (WrittenAtClose): its own write is made on the
	// window's thread, and a disk that refused twice a moment ago is not asked a third time there, by a program that would
	// stand frozen for the answer. An end that passes the closing window by (Windows logging off) still writes at the end.
	public static void SaveAtClose()
	{
		Save();
		if (Unsaved) {
			Save();
		}
		lock (Gate) {
			s_closed = true;
		}
	}

	public static bool WrittenAtClose
	{
		get {
			lock (Gate) {
				return s_closed;
			}
		}
	}

	// Is something kept that the file does not hold yet? Also while a write is under way: until it has ended, nobody knows
	// that the file holds it (a closing window that asked in that moment was told "nothing", and the program could end
	// under the write). Save then waits for that write, and writes again only when there is something left to write
	public static bool Unsaved
	{
		get {
			lock (Gate) {
				return s_unsaved || s_writing;
			}
		}
	}

	// a remembered size held to what the screen has room for now (a window sized on a big monitor, opened on a laptop)
	public static double Fit(double? a_kept, double a_usual, double a_least, double a_room) =>
		Math.Max(Least(a_least, a_room), Math.Min(a_kept ?? a_usual, a_room));

	// The least a window may be, held to what the screen has room for. Windows Presentation Foundation puts a window's least
	// size above the size it is given: a least height the screen does not have pushed the window past the screen's edge.
	public static double Least(double a_least, double a_room) => Math.Min(a_least, a_room);
}
