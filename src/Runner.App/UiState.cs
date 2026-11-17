using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner.App;

// What the window remembers about itself from one start to the next: its own size, the sizes of its pop-out windows, whether
// the log is hidden. One small file in the data folder; a file that does not read is simply not used, a value that cannot be
// kept is simply not kept, and nothing is read or written for the off-screen pictures.
public static class UiState
{
	static readonly Lock Gate = new();
	static JsonObject? s_state;

	public static string File => Path.Combine(AppPaths.Data, "window.json");

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
			try {
				Directory.CreateDirectory(AppPaths.Data);
				Files.WriteAtomic(File, System.Text.Encoding.UTF8.GetBytes(state.ToJsonString()));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
	}

	// a remembered size held to what the screen has room for now (a window sized on a big monitor, opened on a laptop)
	public static double Fit(double? a_kept, double a_usual, double a_least, double a_room) =>
		Math.Max(Math.Min(a_least, a_room), Math.Min(a_kept ?? a_usual, a_room));
}
