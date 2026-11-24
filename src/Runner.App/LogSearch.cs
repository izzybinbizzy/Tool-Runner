namespace ToolRunner.App;

// The log window's search and "first problem": where in the log's text the next match, or the first line
// the run marked as a problem, starts. The run marks a warning "! " and a failure "!!! " after the time (RunLog.Say).
public static class LogSearch
{
	// the next place a_q stands at or after a_from, from the top again past the end; -1 = nowhere
	public static int Next(string a_text, string a_q, int a_from)
	{
		if (a_q.Length == 0 || a_text.Length == 0) {
			return -1;
		}
		var from = Math.Clamp(a_from, 0, a_text.Length);
		var at = a_text.IndexOf(a_q, from, StringComparison.OrdinalIgnoreCase);
		return at >= 0 ? at : a_text.IndexOf(a_q, 0, StringComparison.OrdinalIgnoreCase);
	}

	// the start of the first failure line, else of the first warning line; -1 = the log holds neither
	public static int FirstProblem(string a_text)
	{
		var warn = -1;
		var start = 0;
		while (start < a_text.Length) {
			var end = a_text.IndexOf('\n', start);
			var line = a_text.AsSpan(start, (end < 0 ? a_text.Length : end) - start);
			var mark = Mark(line);
			if (mark == 2) {
				return start;
			}
			if (mark == 1 && warn < 0) {
				warn = start;
			}
			if (end < 0) {
				break;
			}
			start = end + 1;
		}
		return warn;
	}

	// 2 = a failure, 1 = a warning, 0 = neither: the words after the line's time ("21:40:03  ! ..." / "21:40:03  !!! ...")
	static int Mark(ReadOnlySpan<char> a_line)
	{
		var gap = a_line.IndexOf("  ", StringComparison.Ordinal);
		if (gap < 0 || gap > 12) {
			return 0;
		}
		var words = a_line[gap..].TrimStart();
		return words.StartsWith("!!! ", StringComparison.Ordinal) ? 2 : words.StartsWith("! ", StringComparison.Ordinal) ? 1 : 0;
	}
}
