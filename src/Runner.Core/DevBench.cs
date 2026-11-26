using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

// DevBench (Nexus 181326) - the game's remote control the in-game steps use when it is installed: load, save, console,
// Papyrus calls, the message boxes. Without it the in-game steps are guided (the player does them, the window waits).
public sealed class DevBench(string a_baseUrl = "http://127.0.0.1:8920/api/tool/")
{
	static readonly HttpClient Http = new();

	public JsonObject Call(string a_tool, object a_args, int a_timeoutSeconds = 15)
	{
		using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(a_timeoutSeconds));
		using var resp = Http.PostAsJsonAsync(a_baseUrl + a_tool, a_args, cts.Token).GetAwaiter().GetResult();
		resp.EnsureSuccessStatusCode();
		var node = JsonNode.Parse(resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult()) as JsonObject ?? [];
		return node["result"] as JsonObject ?? node;
	}

	public bool Answers()
	{
		try {
			return Call("inspect", new { kind = "health" }, 5)["frame"] != null;
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or OperationCanceledException) {
			return false;
		}
	}

	public long? Frame()
	{
		try {
			return Call("inspect", new { kind = "health" }, 5)["frame"]?.GetValue<long>();
		} catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or FormatException) {
			return null;
		}
	}

	public JsonNode? Papyrus(string a_script, string a_function, params object[] a_args) =>
		Returned(Call("papyrus", new { action = "call", script = a_script, function = a_function, args = a_args }, 30), a_script, a_function);

	// a Papyrus member function called on one reference (its form id, e.g. "0x00000014" for the player)
	public JsonNode? PapyrusOn(string a_self, string a_script, string a_function, params object[] a_args) =>
		Returned(Call("papyrus", new { action = "call", script = a_script, function = a_function, args = a_args, self = new { form = a_self } }, 40), a_script, a_function);

	static JsonNode? Returned(JsonNode a_r, string a_script, string a_function)
	{
		if (a_r["called"] is not JsonValue c || !c.TryGetValue<bool>(out var called) || !called) {
			var text = a_r.ToJsonString();
			throw new StepFailedException($"the game did not run {a_script}.{a_function}: {text[..Math.Min(300, text.Length)]}");
		}
		return a_r["returned"];
	}

	// a returned reference's form id as "0xXXXXXXXX"
	public static string? FormKey(JsonNode? a_v)
	{
		var v = a_v is JsonObject o ? o["formId"] ?? o["formID"] : a_v;
		if (v is JsonValue jv && jv.TryGetValue<long>(out var n)) {
			return $"0x{n:X8}";
		}
		var s = v?.ToString().Trim();
		if (string.IsNullOrEmpty(s)) {
			return null;
		}
		// "0x...", a hex letter, or an 8-digit id padded with a leading zero are hex; any other plain number is decimal (an
		// 8-digit decimal with no leading zero stays decimal)
		var hex = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || s.Any(char.IsAsciiLetter) || s.Length == 8 && s[0] == '0';
		var digits = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s;
		return long.TryParse(digits, hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None, InvariantCulture, out var h)
			? $"0x{h:X8}" : null;
	}

	public void Console(string a_command) => Call("console", new { action = "exec", command = a_command });

	// is DevBench in this profile (its DLL in a ticked mod)?
	public static bool InProfile(Profile a_profile) =>
		a_profile.States().Where(s => s.Value == '+').Any(s =>
			File.Exists(Path.Combine(a_profile.Mo2.ModDir(s.Key), "SKSE", "Plugins", "devbench.dll")));

	// a load box answered by its TEXT and the button's LABEL: a save with missing / inactive plugins first asks "search ...?" /
	// "Activate now?" -> No (activating a plugin in game can crash it), then "... continue loading?" -> Yes; a one-button notice
	// is acknowledged; anything else is not answered (null). a_words: the game's own text for those boxes and buttons, for a game
	// that is not in English
	public static (string? Answer, int Index) DecideBox(string a_body, IReadOnlyList<string> a_labels, int? a_buttonCount, GameWords? a_words = null)
	{
		var body = a_body.ToLowerInvariant();
		var low = a_labels.Select(l => l.Trim().ToLowerInvariant()).ToList();
		string want;
		if (body.Contains("activate now") || body.Contains("currently inactive") || (body.Contains("search") && (body.Contains("creations") || body.Contains("content"))) || a_words?.AsksNo(a_body) == true) {
			want = "no";
		} else if ((body.Contains("continue") && body.Contains("load")) || a_words?.AsksYes(a_body) == true) {
			want = "yes";
		} else if (a_labels.Count == 1 || (a_labels.Count == 0 && a_buttonCount == 1)) {
			return ("acknowledged", 0);
		} else {
			return (null, -1);
		}
		var i = low.IndexOf(want);
		if (i < 0 && a_words != null) {
			i = a_words.Button(want == "yes", a_labels);
		}
		if (i >= 0) {
			return ("answered " + want.ToUpperInvariant(), i);
		}
		return a_labels.Count == 0 ? ("answered " + want.ToUpperInvariant(), want == "yes" ? 0 : 1) : (null, -1);
	}

	// the words by which a button declines, puts off or leaves - the most telling first
	static readonly string[] Declines = ["cancel", "no", "close", "not now", "later", "skip", "back", "exit", "ignore", "dismiss", "decline", "never", "no thanks", "don't", "do not"];

	// Which button closes a box the run does not know (whatever the box is, it is closed at once).
	// The one that takes nothing on: the button the game itself marks as the box's cancel; else one whose words decline or
	// leave; else the game's own word for No (a game in another language); else its only button; else its last - where the
	// game's own boxes and Windows' put the way out (Yes / No, OK / Cancel, Abort / Retry / Ignore).
	// a_labels: the buttons' words (none given: a_buttonCount says how many there are).
	public static int CloseIndex(IReadOnlyList<string> a_labels, int a_cancelIndex, int? a_buttonCount, GameWords? a_words = null)
	{
		var count = a_labels.Count > 0 ? a_labels.Count : Math.Max(1, a_buttonCount ?? 1);
		if (a_cancelIndex >= 0 && a_cancelIndex < count) {
			return a_cancelIndex;
		}
		var low = a_labels.Select(l => l.Replace("&", "", StringComparison.Ordinal).Trim().TrimEnd('.', '!').ToLowerInvariant()).ToList();
		foreach (var word in Declines) {
			if (low.IndexOf(word) is >= 0 and var said) {
				return said;
			}
		}
		if (a_words != null && a_labels.Count > 0 && a_words.Button(false, a_labels) is >= 0 and var no) {
			return no;
		}
		var begins = low.FindIndex(l => l.StartsWith("no ", StringComparison.Ordinal) || l.StartsWith("no,", StringComparison.Ordinal) || l.StartsWith("not ", StringComparison.Ordinal) ||
			l.StartsWith("don't", StringComparison.Ordinal) || l.StartsWith("do not", StringComparison.Ordinal) || l.StartsWith("cancel", StringComparison.Ordinal));
		return begins >= 0 ? begins : count - 1;
	}
}
