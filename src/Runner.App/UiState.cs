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
	// something is kept that the file does not hold yet / a write of it is on its way
	static bool s_unsaved, s_queued;
	// a width and a height kept together are one write
	static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(150);

	public static string File => FileForTests ?? Path.Combine(AppPaths.Data, "window.json");

	// the offline tests keep it in a file of their own (and start from nothing kept, before and after)
	public static string? FileForTests
	{
		get;
		set {
			lock (Gate) {
				field = value;
				s_state = null;
				s_unsaved = false;
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
			if (App.Headless) {
				return;
			}
			s_unsaved = true;
			if (s_queued) {
				return;
			}
			s_queued = true;
		}
		// The value is kept at once; the file is written a moment later and never on the window's own thread - a disk that
		// answers slowly holds up no click and no line of the log (CodeRabbit on pull request #35)
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
				text = System.Text.Encoding.UTF8.GetBytes(State().ToJsonString());
			}
			try {
				var file = File;
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				Files.WriteAtomic(file, text);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
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
