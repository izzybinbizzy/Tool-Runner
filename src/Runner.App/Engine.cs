using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ToolRunner.App;

// Where the program keeps its setups and runs: beside the exe when that folder can be written (portable, like the tools it
// runs), else in the user's local app data.
public static class AppPaths
{
	public static string Exe => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Brand.Name + ".exe");

	public static string Data { get; } = Resolve();

	public static string Setups => Path.Combine(Data, "Setups");
	public static string Runs => Runner.RunsDir(Data);

	// Windows' own app-data folder for the program's data: where the player can keep it (Settings > Program), so a new version
	// unzipped into any other folder finds the run history, the setups and the learned settings
	public static string AppDataFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Brand.Name);

	public static bool InAppData => Files.SamePath(Data, AppDataFolder);

	// the player chose the app-data folder for every copy (Settings > Program)
	public static bool AppDataChosen => DataFolder.Chosen(AppDataFolder);

	// the folder beside the program is where the player's setups are
	static bool HoldsSetups(string a_data)
	{
		try {
			var setups = Path.Combine(a_data, "Setups");
			return Directory.Exists(setups) && Directory.EnumerateFiles(setups, "*.json").Any();
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	static string Resolve()
	{
		try {
			// a data folder beside the program whose data moved (Settings > Keep data through updates): the place it moved to,
			// whatever is left of the old one removed
			foreach (var name in Brand.AllNames) {
				var old = Path.Combine(AppContext.BaseDirectory, name + " Data");
				if (DataFolder.MovedTo(old) is { } moved) {
					DataFolder.FinishMove(old, moved);
					Directory.CreateDirectory(moved);
					return moved;
				}
			}
			// no data folder beside the program, and the player chose the app-data folder for every copy: that one
			if (!DataFolder.StandsIn(AppContext.BaseDirectory, " Data") && DataFolder.Chosen(AppDataFolder)) {
				var kept = DataFolder.Adopt(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "");
				Directory.CreateDirectory(kept);
				return kept;
			}
			if (Files.IsUnder(AppContext.BaseDirectory, Path.GetTempPath())) {
				throw new UnauthorizedAccessException("a temporary folder");
			}
			// one made under the program's old name is taken over (DataFolder.Adopt)
			var beside = DataFolder.Adopt(AppContext.BaseDirectory, " Data");
			Directory.CreateDirectory(beside);
			var probe = Path.Combine(beside, ".write test");
			try {
				File.WriteAllText(probe, "");
			} catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && HoldsSetups(beside)) {
				// the player's setups are in this folder: a file another program holds for a moment (a scanner) must not send
				// this start to an empty folder elsewhere - tried again before the folder is given up
				Files.Retry(() => File.WriteAllText(probe, ""));
			}
			// (the write proved the folder; its removal never decides where the data is)
			Files.TryDelete(probe);
			return beside;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			var local = DataFolder.Adopt(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "");
			Directory.CreateDirectory(local);
			return local;
		}
	}
}

// The window never runs a tool itself: it starts the engine as its own process ("run"), so closing the window never ends a
// run, and it follows the run through its record on disk.
public static class Engine
{
	public static (bool Ok, string Text) StartRun(string a_setupFile, string? a_only, string? a_from, string? a_until)
	{
		var args = new List<string> { "run", "--setup", a_setupFile };
		if (a_only != null) args.AddRange(["--only", a_only]);
		if (a_from != null) args.AddRange(["--from", a_from]);
		if (a_until != null) args.AddRange(["--until", a_until]);
		return StartEngine(args);
	}

	public static (bool Ok, string Text) StartQueue(IEnumerable<string> a_setupFiles, bool a_keepGoing)
	{
		var args = new List<string> { "queue" };
		foreach (var f in a_setupFiles) {
			args.AddRange(["--setup", f]);
		}
		if (a_keepGoing) {
			args.Add("--keep-going");
		}
		return StartEngine(args);
	}

	static (bool Ok, string Text) StartEngine(List<string> args)
	{
		var psi = new ProcessStartInfo(AppPaths.Exe) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppPaths.Data };
		foreach (var a in args) {
			psi.ArgumentList.Add(a);
		}
		try {
			using var p = Process.Start(psi);
			return p == null ? (false, "Windows did not start the run") : (true, p.Id.ToString(InvariantCulture));
		} catch (System.ComponentModel.Win32Exception e) {
			return (false, e.Message);
		}
	}

	static readonly JournalCache Cache = new(AppPaths.Runs);

	// a start the engine refused writes its reason here (no run record exists for it)
	public static string RefusalFile => Path.Combine(AppPaths.Runs, "last refusal.txt");

	public static string? LastRefusal(DateTime a_since)
	{
		try {
			return File.Exists(RefusalFile) && File.GetLastWriteTimeUtc(RefusalFile) >= a_since.ToUniversalTime().AddSeconds(-5) ? Files.ReadSharedText(RefusalFile).Trim() : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// every run record, newest first (read again only where a record changed)
	public static List<Journal> Runs() => Cache.Read();

	// The window watches the runs folder (RunsWatch) and says so here: what was read stands until the watcher names a
	// change, this process writes a record, a run folder comes or goes, or a quarter of a minute is over (JournalCache)
	public static void RunsWatched(bool a_on) => Cache.Watched = a_on;

	public static void RunsChanged() => Cache.Touch();

	// the run that is going: also one that has finished and is at its last chores (its game for the final save, the setup's
	// after-run commands, packing the old outputs) - Stop and the close guard reach it until its engine has ended
	public static Journal? Active(List<Journal>? a_runs = null) => (a_runs ?? Runs()).FirstOrDefault(j => j.Live && j.OwnerAlive);

	public static Journal? Interrupted(List<Journal>? a_runs = null) => (a_runs ?? Runs()).FirstOrDefault(j => j.Unfinished && !j.OwnerAlive);

	public static Journal? Latest(string? a_setupFile, List<Journal>? a_runs = null) =>
		(a_runs ?? Runs()).FirstOrDefault(j => a_setupFile == null || string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase));

	// each step's usual time: the median of its finished runs (for the time-left estimate)
	public static Dictionary<string, TimeSpan> UsualTimes(string a_setupFile, List<Journal>? a_runs = null)
	{
		var all = new Dictionary<string, List<double>>();
		foreach (var j in (a_runs ?? Runs()).Where(j => string.Equals(j.SetupFile, a_setupFile, StringComparison.OrdinalIgnoreCase)).Take(30)) {
			foreach (var s in j.Steps.Where(s => s.State == StepState.Done && s.Started != null && s.Ended != null)) {
				(all.TryGetValue(s.Key, out var l) ? l : all[s.Key] = []).Add(Times.Between(s.Started!.Value, s.Ended!.Value).TotalSeconds);
			}
		}
		return all.ToDictionary(p => p.Key, p => TimeSpan.FromSeconds(p.Value.Order().ElementAt(p.Value.Count / 2)));
	}
}

// a console for the command line when started from one (the exe is a window program)
public static partial class ConsoleAttach
{
	[LibraryImport("kernel32.dll")]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool AttachConsole(int a_pid);

	public static void ToParent()
	{
		// the console owns these writers for the rest of the program's life (a command ends the process right after)
#pragma warning disable CA2000
		if (AttachConsole(-1)) {
			// A console shows the bytes written to it in its own code page: UTF-8 written there came out as wrong letters for
			// every name with an accent or a non-Latin letter in it (a mod, a folder). A file or a pipe - a script - gets
			// UTF-8, as before
			var encoding = Console.IsOutputRedirected ? new System.Text.UTF8Encoding(false) : Console.OutputEncoding;
			Console.SetOut(new ReaderGone(new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true }));
			Console.SetError(Console.Out);
		} else {
			// (started with no console to join - a script, a scheduled task: what it reads from the error stream stays apart
			// from what it reads from the output)
			Console.SetOut(new ReaderGone(Console.Out));
			Console.SetError(new ReaderGone(Console.Error));
		}
#pragma warning restore CA2000
	}

	// What is printed goes to whoever reads it, and a write fails once that reader is gone (a pipe closed early:
	// "... | select -first 1"). Left to fail, it made a finished run end with exit 1, and from the place that says what went
	// wrong it ended the program on an error of its own. Nobody is reading any more: what is written from then on is dropped
	public sealed class ReaderGone(TextWriter a_to) : TextWriter(InvariantCulture)
	{
		bool _gone;

		public override System.Text.Encoding Encoding => a_to.Encoding;

		public override void Write(char a_value) => Try(() => a_to.Write(a_value));

		public override void Write(string? a_value) => Try(() => a_to.Write(a_value));

		public override void WriteLine(string? a_value) => Try(() => a_to.WriteLine(a_value));

		public override void Flush() => Try(a_to.Flush);

		void Try(Action a_write)
		{
			if (_gone) {
				return;
			}
			try {
				a_write();
			} catch (Exception e) when (e is IOException or ObjectDisposedException or UnauthorizedAccessException) {
				_gone = true;
			}
		}

		protected override void Dispose(bool a_disposing)
		{
			if (a_disposing) {
				a_to.Dispose();
			}
			base.Dispose(a_disposing);
		}
	}
}
