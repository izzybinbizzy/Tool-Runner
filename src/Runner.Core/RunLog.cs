using System.Text;

namespace ToolRunner;

public enum Level
{
	Info,
	Detail,
	Warn,
	Error,
}

// One run's logs. Each line is flushed at once: a run that dies leaves everything up to its death on disk. Two logs (his
// checklist 469): log.txt, the detailed one, every line; steps.txt, the simple one, step by step in plain words - each step
// started and done, a failure with what it means and what to do, the run's verdict.
//
// Writing a line never throws. Log lines stand in front of the work on every recovery path (the put-back says what it does
// before it does it): on a full disk a line that could not be written must not be what keeps the profile from being put back.
// Lines the disk refused are kept, a few hundred at most, and written when it takes them again.
public sealed class RunLog : IDisposable
{
	readonly StreamWriter? _text;
	readonly StreamWriter? _simple;
	StreamWriter? _mirror;
	readonly Lock _gate = new();
	readonly Queue<(bool Simple, string Line)> _owed = new();
	int _lost;
	const int OwedMost = 400;

	public string? TextPath { get; }
	public event Action<string, Level>? Line;

	volatile EchoQueue? _echo;

	// how long the end of a run waits for the lines still on their way to the command line
	public static TimeSpan EchoWait { get; set; } = TimeSpan.FromSeconds(5);

	// Every line of the detailed log as it is written, for a command line that shows the run while it goes. Written by a
	// thread of its own: output piped to a program that has stopped reading blocks the write that fills the pipe, and on the
	// run's own thread that held the run - it reached neither its next Stop check nor its put-back. A reader that falls
	// behind loses lines there (said in their place); the log file has every one.
	public void EchoTo(Action<string>? a_to)
	{
		_echo?.Close(TimeSpan.Zero);
		_echo = a_to == null ? null : new EchoQueue(a_to);
	}

	sealed class EchoQueue
	{
		readonly Action<string> _to;
		readonly Queue<string> _lines = new();
		readonly object _gate = new();
		readonly Thread _thread;
		bool _closing;
		int _dropped;
		const int Most = 2000;

		public EchoQueue(Action<string> a_to)
		{
			_to = a_to;
			_thread = new Thread(Pump) { IsBackground = true, Name = "log echo" };
			_thread.Start();
		}

		// never waits: a full queue drops the line and counts it
		public void Post(string a_line)
		{
			lock (_gate) {
				if (_closing) {
					return;
				}
				if (_lines.Count >= Most) {
					++_dropped;
					return;
				}
				if (_dropped > 0) {
					_lines.Enqueue($"({_dropped} line(s) are not shown here: this output was not read fast enough - the run's log has them)");
					_dropped = 0;
				}
				_lines.Enqueue(a_line);
				Monitor.Pulse(_gate);
			}
		}

		void Pump()
		{
			while (true) {
				string line;
				lock (_gate) {
					while (_lines.Count == 0 && !_closing) {
						Monitor.Wait(_gate);
					}
					if (_lines.Count == 0) {
						return;
					}
					line = _lines.Dequeue();
				}
				// whatever the callback throws stays here: an error that leaves a thread of its own ends the whole program, and a
				// run that ends that way never puts the profile back
				try {
					_to(line);
				} catch (OutOfMemoryException) {
					// no memory left for the live copy: it ends here and gives back what it held. The run goes on without it
					// (the log file has every line) and still reaches its put-back
					lock (_gate) {
						_closing = true;
						// (nothing here may ask for memory: Clear lets the lines go and allocates nothing)
						_lines.Clear();
					}
					return;
				} catch (Exception e) when (e is not OutOfMemoryException) {
				}
			}
		}

		// what is queued is still written, for a_wait at most: a reader that has stopped never holds the end of a run
		public void Close(TimeSpan a_wait)
		{
			lock (_gate) {
				_closing = true;
				Monitor.PulseAll(_gate);
			}
			_thread.Join(a_wait);
		}
	}

	public static RunLog Null { get; } = new();

	RunLog() { }

	public RunLog(string a_folder)
	{
		Directory.CreateDirectory(a_folder);
		TextPath = Path.Combine(a_folder, "log.txt");
		// a writer that opened is released at once when a later one cannot open (no handle left for the finalizer)
		try {
			_text = Open(TextPath, FileMode.Append);
			_simple = Open(Path.Combine(a_folder, SimpleName), FileMode.Append);
		} catch {
			_text?.Dispose();
			throw;
		}
	}

	// the simple log's file name in a run folder
	public const string SimpleName = "steps.txt";

	static StreamWriter Open(string a_path, FileMode a_mode) =>
		new(new FileStream(a_path, a_mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete), new UTF8Encoding(false)) { AutoFlush = true };

	// every line also into one fixed file, emptied first (one name for an outside watch, e.g. watch.py --watch); a file that
	// cannot be opened is simply not mirrored
	public void Mirror(string a_path)
	{
		try {
			_mirror = Open(a_path, FileMode.Create);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			_mirror = null;
		}
	}

	// how many lines the disk refused and are still waiting (the offline tests read it)
	public int Owed {
		get {
			lock (_gate) {
				return _owed.Count + _lost;
			}
		}
	}

	// under the gate. What is owed goes first, in order, so the log reads straight once the disk answers again
	void Write(bool a_simple, string a_line)
	{
		if ((a_simple ? _simple : _text) == null) {
			return;
		}
		try {
			while (_owed.TryPeek(out var o)) {
				(o.Simple ? _simple : _text)!.WriteLine(o.Line);
				_owed.Dequeue();
			}
			if (_lost > 0) {
				_text?.WriteLine($"{DateTime.Now.ToString("HH:mm:ss", InvariantCulture)}  ! {_lost} more line(s) could not be written while the disk did not take them");
				_lost = 0;
			}
			(a_simple ? _simple : _text)!.WriteLine(a_line);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
			if (_owed.Count < OwedMost) {
				_owed.Enqueue((a_simple, a_line));
			} else {
				++_lost;
			}
		}
	}

	public void Say(string a_text, Level a_level = Level.Info)
	{
		var line = $"{DateTime.Now.ToString("HH:mm:ss", InvariantCulture)}  {(a_level == Level.Detail ? "  " : "")}{MarkOf(a_level)}{a_text}";
		lock (_gate) {
			Write(false, line);
			try {
				_mirror?.WriteLine(line);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
			}
		}
		_echo?.Post(line);
		Line?.Invoke(a_text, a_level);
	}

	// a line of the simple log (the detailed log has its own wording of the same moment), marked as the detailed log marks a
	// warning or a failure, so the window colors both the same way
	// what a simple log's line carries after its time: the window colors it by this
	public static string MarkOf(Level a_level) => a_level switch { Level.Warn => "! ", Level.Error => "!!! ", _ => "" };

	public void Simple(string a_text, Level a_level = Level.Info)
	{
		lock (_gate) {
			Write(true, $"{DateTime.Now.ToString("HH:mm", InvariantCulture)}  {MarkOf(a_level)}{a_text}");
		}
	}

	public void Detail(string a_text) => Say(a_text, Level.Detail);
	public void Warn(string a_text) => Say(a_text, Level.Warn);
	public void Error(string a_text) => Say(a_text, Level.Error);

	public void Dispose()
	{
		// the lines on their way to the command line first, so the run's verdict follows them there
		_echo?.Close(EchoWait);
		_echo = null;
		// each writer on its own: a last flush the disk refuses for one never leaves the others open
		lock (_gate) {
			try {
				_text?.Dispose();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
			try {
				_simple?.Dispose();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
			try {
				_mirror?.Dispose();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			}
		}
	}
}
