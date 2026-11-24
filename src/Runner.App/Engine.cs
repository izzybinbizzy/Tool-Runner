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
			File.WriteAllText(probe, "");
			File.Delete(probe);
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
			return File.Exists(RefusalFile) && File.GetLastWriteTime(RefusalFile) >= a_since.AddSeconds(-5) ? Files.ReadSharedText(RefusalFile).Trim() : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// every run record, newest first (read again only where a record changed)
	public static List<Journal> Runs() => Cache.Read();

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
				(all.TryGetValue(s.Key, out var l) ? l : all[s.Key] = []).Add((s.Ended!.Value - s.Started!.Value).TotalSeconds);
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
		if (AttachConsole(-1)) {
			// the console owns this writer for the rest of the program's life (a command ends the process right after)
#pragma warning disable CA2000
			var o = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
#pragma warning restore CA2000
			Console.SetOut(o);
			Console.SetError(o);
		}
	}
}
