using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace ToolRunner.App;

// The run's log, each line in the color of what it says: green for a step going as planned, yellow for a warning worth a look
// ("! " after the time), red for what failed or went off plan ("!!! " - a step that failed, a box left to the player). Both logs
// mark their lines that way (RunLog). A line is one paragraph of one run, so a place in the text maps straight onto the document;
// the whole text is kept beside it for the search and Copy all.
public sealed class LogView : RichTextBox
{
	readonly StringBuilder _all = new();
	// where each line starts in the text, and its paragraph
	readonly List<(int Start, Paragraph Line)> _lines = [];

	public LogView()
	{
		IsReadOnly = true;
		IsUndoEnabled = false;
		IsDocumentEnabled = false;
		VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
		HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
		Document = new FlowDocument { PagePadding = new Thickness(2) };
		Wrap = false;
	}

	public string Text => _all.ToString();

	// how many characters it holds now (counted, never by copying the text out)
	public int Length => _all.Length;

	// where the last search found its words (-1: nowhere yet): the next search goes on after it
	public int FoundAt { get; private set; } = -1;

	// lines run past the edge unless wrapped (a page as wide as the longest line would be measured every time one is added)
	public bool Wrap
	{
		get;
		set {
			field = value;
			Document.PageWidth = value ? double.NaN : 4000;
		}
	}

	public void Clear()
	{
		_all.Clear();
		_lines.Clear();
		Document.Blocks.Clear();
		FoundAt = -1;
	}

	// whole lines added at the end ("\n" between them; a last part without one is a line too)
	public void AppendLines(string a_text)
	{
		var parts = a_text.Split('\n');
		for (var i = 0; i < parts.Length; ++i) {
			// the text after the last "\n" is empty when the text ends with one: no line of its own
			if (i == parts.Length - 1 && parts[i].Length == 0) {
				break;
			}
			var line = parts[i].TrimEnd('\r');
			var p = new Paragraph(new Run(line)) { Margin = new Thickness(0) };
			p.SetResourceReference(TextElement.ForegroundProperty, BrushKey(line));
			_lines.Add((_all.Length, p));
			Document.Blocks.Add(p);
			_all.Append(line).Append('\n');
		}
	}

	// the newest a_chars characters kept (whole lines), the rest taken out
	public void KeepLast(int a_chars)
	{
		if (_all.Length <= a_chars) {
			return;
		}
		var cut = _all.Length - a_chars;
		var first = _lines.FindIndex(l => l.Start >= cut);
		// no line begins inside the budget (the newest one alone is longer): every line before the newest goes - a line is
		// never cut in two
		if (first < 0) {
			first = _lines.Count - 1;
		}
		if (first <= 0) {
			return;
		}
		var drop = _lines[first].Start;
		for (var i = 0; i < first; ++i) {
			Document.Blocks.Remove(_lines[i].Line);
		}
		_lines.RemoveRange(0, first);
		for (var i = 0; i < _lines.Count; ++i) {
			_lines[i] = (_lines[i].Start - drop, _lines[i].Line);
		}
		_all.Remove(0, drop);
		FoundAt = FoundAt >= drop ? FoundAt - drop : -1;
	}

	// the words at a_at selected and brought into view
	public void Reveal(int a_at, int a_length)
	{
		var i = _lines.FindLastIndex(l => l.Start <= a_at);
		if (i < 0 || _lines[i].Line.Inlines.FirstInline is not Run run) {
			return;
		}
		FoundAt = a_at;
		var from = a_at - _lines[i].Start;
		var start = run.ContentStart.GetPositionAtOffset(Math.Min(from, run.Text.Length)) ?? run.ContentStart;
		var end = run.ContentStart.GetPositionAtOffset(Math.Min(from + a_length, run.Text.Length)) ?? run.ContentEnd;
		Focus();
		Selection.Select(start, end);
		_lines[i].Line.BringIntoView();
	}

	// the theme's color for a line: its mark after the time (both logs write "HH:mm" or "HH:mm:ss", two spaces, then the mark)
	public static string BrushKey(string a_line) => Mark(a_line) switch {
		"!!! " => "Bad",
		"! " => "Warn",
		_ => a_line.Trim().Length == 0 ? "Text" : "Good",
	};

	public static string Mark(string a_line) => LogSearch.Mark(a_line) switch {
		2 => "!!! ",
		1 => "! ",
		_ => "",
	};
}
