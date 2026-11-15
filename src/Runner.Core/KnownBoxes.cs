namespace ToolRunner;

// A box (a small message window) one of the run's own processes shows, as the run's watch sees it: whose it is, its title, its
// text and its buttons by what they are ("Yes", "No", "OK" - Windows' own names whatever its language - else their own words).
// Game: it belongs to the game this run started (the game itself, SKSE's loader, a program the game started).
public sealed record BoxSeen(string Image, bool Game, string Title, string Text, IReadOnlyList<string> Buttons);

public enum BoxMove
{
	// the runner knows this box and its answer: Button is pressed
	Press,
	// the player's own choice, made in that window: never pressed, never a reason to stop
	Leave,
	// only named: the step that runs that tool has its own rules for the tool's windows
	Name,
	// nobody here can answer it: the step in hand stops, naming it
	Stop,
}

public sealed record BoxVerdict(BoxMove Move, string? Button, string Note);

// The boxes the runner knows, in one place. A box of the game that is not here and has more than one button is never
// guessed at: an answer picked by its place or its words could agree to anything a mod asks.
public static class KnownBoxes
{
	static bool Has(string a_text, string a_words) => a_text.Contains(a_words, StringComparison.OrdinalIgnoreCase);

	static string? Button(BoxSeen a_b, string a_name) => a_b.Buttons.FirstOrDefault(l => l.Equals(a_name, StringComparison.OrdinalIgnoreCase));

	public static string Short(string a_text, int a_max = 300) => a_text.Length > a_max ? a_text[..a_max] + " ..." : a_text;

	// a box in words: whose it is, its title, its text, its buttons
	public static string Describe(BoxSeen a_b) =>
		$"{(a_b.Image == "" ? "a program of the run" : a_b.Image)}: \"{a_b.Title}\"{(a_b.Text.Trim() == "" ? "" : " - " + Short(a_b.Text.Trim()))}" +
		(a_b.Buttons.Count == 0 ? "" : $" [{string.Join(" / ", a_b.Buttons)}]");

	public static BoxVerdict Judge(BoxSeen a_b)
	{
		if (FasterArchives.IsTool(a_b.Image)) {
			return new(BoxMove.Leave, null, FasterArchives.Asks);
		}
		if ((Has(a_b.Title, "Recursive Directory Walk") || Has(a_b.Text, "CRDW cache")) && Button(a_b, "Yes") is { } yes) {
			return new(BoxMove.Press, yes, Crdw.Answered);
		}
		if (!a_b.Game) {
			return new(BoxMove.Name, null, "");
		}
		var (press, note) = InGame.NativeBoxVerdict(a_b.Title, a_b.Text, a_b.Buttons);
		return press != null ? new(BoxMove.Press, press, note) :
			new(BoxMove.Stop, null, $"the game shows a box this run does not know - {Describe(a_b)}. Answer it in the game once (or switch off what asks: the mod, or its own setting for that question), then run again from this step");
	}
}
