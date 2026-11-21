using System.Diagnostics;
using System.Text.Json.Nodes;

namespace ToolRunner.Steps;

// His checklist 436 and 534: the three tools that rewrite files inside the player's own mods - xEdit's cleaning, SSE NIF
// Optimizer, Cathedral Assets Optimizer. Each is off in a new setup, says what it is about to do before every run, and never
// starts its tool before a copy of every file it may change is in the run's backup (Rewrites.cs): a step that does not finish
// puts them all back, and Undo the last run puts back what a finished one changed.
public static class RewriteKit
{
	// shown with the step's own settings (a setup's "_..." keys are read out as they are)
	public const string Warning = "This step lets a tool rewrite files inside your own mods, where they stand - every profile that uses those mods gets the changed files. " +
		"A copy of each file it changes is kept in the run's backup first, and Undo the last run puts them back. Once that backup is removed they cannot come back, " +
		"so keep the mod's own download.";

	// every mod the setup's steps make, replace or fill: a run remakes those, so no tool is let into them
	public static HashSet<string> OwnMods(Setup a_setup)
	{
		var own = new HashSet<string>(a_setup.LodOutputs, StringComparer.OrdinalIgnoreCase);
		foreach (var s in a_setup.Steps.Select(s => new StepSettings(s.Settings))) {
			own.UnionWith(new[] { s.OutputMod, s.Str("tool_writes") }.OfType<string>());
			own.UnionWith(s.List("replaces"));
			own.UnionWith((s.Raw["caches"] as JsonArray)?.OfType<JsonObject>().Select(c => Setup.Str(c, "output_mod")).OfType<string>() ?? []);
		}
		return own;
	}

	// the mods a step may let its tool into: named, there, and the player's own - and what the step is about to do, said each time
	public static IEnumerable<Issue> CheckMods(CheckContext a_c, string a_step, StepSettings a_s, string? a_toolExe)
	{
		var mods = a_s.List("mods");
		if (mods.Count == 0) {
			yield return new Issue(a_step, true, "no mod is named for it to work on");
			yield break;
		}
		var own = OwnMods(a_c.Setup);
		foreach (var m in mods) {
			var dir = Setup.PlainName(m) ? a_c.Mo2.ModDir(m) : null;
			if (dir == null || !Directory.Exists(dir)) {
				yield return new Issue(a_step, true, $"\"{m}\" is not a mod of this Mod Organizer");
			} else if (own.Contains(m)) {
				yield return new Issue(a_step, true, $"\"{m}\" is made by this setup's own steps - name mods of your own, a run remakes its outputs anyway");
			} else if (a_toolExe is { Length: > 0 } && Files.IsUnder(a_toolExe, dir)) {
				yield return new Issue(a_step, true, $"\"{m}\" holds the tool itself - name the mods it should work on");
			}
		}
		yield return new Issue(a_step, false, $"{StepCatalog.Name(a_step)} rewrites files inside {mods.Count} mod(s) of yours where they stand ({string.Join(", ", mods.Take(4))}{(mods.Count > 4 ? " ..." : "")}): " +
			"a copy of each file it changes is kept in the run's backup first, and Undo the last run puts them back");
	}

	// what a run copies before these steps' tools start, for the forecast of the room it needs: (step, mod, bytes). xEdit's
	// cleaning is left out - which plugins it takes is known only once LOOT has looked, and plugins are small
	public static IEnumerable<(string Step, string Mod, long Bytes)> Copies(Mo2Instance a_mo2, IEnumerable<Setup.StepEntry> a_todo)
	{
		foreach (var step in a_todo) {
			if (StepRegistry.For(step.Key) is not RewriteToolStep tool) {
				continue;
			}
			foreach (var m in new StepSettings(step.Settings).List("mods").Where(Setup.PlainName)) {
				yield return (step.Key, m, Rewrites.BytesOf(a_mo2.ModDir(m), tool.Takes));
			}
		}
	}

	// a mod's name as a file's: the characters Windows refuses in one replaced
	public static string AsFileName(string a_name) => string.Concat(a_name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? ' ' : c)).Trim();
}

// xEdit's Quick Auto Clean on the plugins LOOT calls dirty (read off xEdit's own source, nothing of it copied): started
// through Mod Organizer once per plugin, it undeletes and disables references, removes records identical to their master,
// saves, and with -autoexit closes by itself. It swaps its cleaned file in for the plugin - the original is renamed away into
// its own backup folder, which through Mod Organizer is the overwrite folder - so the step keeps its own copy first and looks
// in both places after. The game's own plugins are never cleaned where they stand: that would take the original out of the
// game's folder. With "clean_game_plugins" on, each is copied into the step's output mod and that copy is cleaned.
public sealed class XEditCleanStep : IStep
{
	public const string StepKey = "xeditclean";
	const string Done = "Quick Clean mode finished";

	public string Key => StepKey;

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (!File.Exists(LootStep.Program(a_c.Mo2))) {
			yield return new Issue(Key, true, $"Mod Organizer's LOOT is not there ({LootStep.Program(a_c.Mo2)}) - it names the plugins to clean; reinstall Mod Organizer, or switch this step off");
		}
		if (a_s.Bool("clean_game_plugins", false) && a_s.OutputMod == null) {
			yield return new Issue(Key, true, "no output mod is set for the cleaned copies of the game's own plugins");
		}
		yield return new Issue(Key, false, "xEdit cleaning rewrites the plugins LOOT calls dirty inside your own mods, where they stand: a copy of each is kept in the run's backup first, " +
			"and Undo the last run puts them back");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		// LOOT's look of this very run when its sort ran: the plugins have not changed since
		var sortedNow = Path.Combine(a_ctx.Journal.Folder, LootStep.ReportFile);
		var report = a_ctx.Journal.Sorted && File.Exists(sortedNow) ? sortedNow :
			LootStep.Report(a_ctx, LootStep.Starter(a_ctx, Key, "LOOT is looking at your plugins", TimeSpan.FromMinutes(a_s.Int("loot_min", 10))));
		var title = a_s.Req("executable");
		var image = a_s.Str("image") ?? (a_ctx.Mo2.Executable(title)?.Binary is { Length: > 0 } bin ? Path.GetFileName(bin) : "SSEEdit.exe");
		Clean(a_ctx, a_s, LootStep.Dirty(report), (plugin, log) => {
			try {
				using var proc = a_ctx.Control.RunExecutable(title, image, Arguments(plugin, log, a_s.Str("args")), a_ctx.Cancel);
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(15), () => proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait);
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		});
	}

	// its game named, the plugin's selection window passed over, its log where the run keeps it, and closed by itself when done
	public static string Arguments(string a_plugin, string a_log, string? a_extra) =>
		$"-SSE -quickautoclean -autoexit -autoload -R:\"{a_log}\"{(string.IsNullOrWhiteSpace(a_extra) ? "" : " " + a_extra.Trim())} \"{a_plugin}\"";

	// a_clean: xEdit run on one plugin to its end (the plugin's file name, the file its log goes to)
	public static void Clean(RunContext a_ctx, StepSettings a_s, IReadOnlyList<string> a_dirty, Action<string, string> a_clean)
	{
		var skip = a_s.List("skip");
		var gameData = Path.Combine(a_ctx.Mo2.GameDir, "Data");
		var overwrite = a_ctx.Mo2.OverwriteDir;
		var places = a_ctx.Profile.PluginPaths();
		var theirs = new List<(string Plugin, string At)>();
		var games = new List<(string Plugin, string At)>();
		foreach (var p in a_dirty.Distinct(StringComparer.OrdinalIgnoreCase)) {
			if (skip.Contains(p, StringComparer.OrdinalIgnoreCase)) {
				a_ctx.Log.Detail($"{p}: left out by this step's own list");
				continue;
			}
			// overwrite wins over every mod
			var at = File.Exists(Path.Combine(overwrite, p)) ? Path.Combine(overwrite, p) : places.GetValueOrDefault(p);
			if (at == null) {
				a_ctx.Log.Detail($"{p}: LOOT calls it dirty, but it is in no switched-on mod - left alone");
			} else if (Files.IsUnder(at, gameData)) {
				games.Add((p, at));
			} else {
				theirs.Add((p, at));
			}
		}
		var mod = a_s.OutputMod;
		var copies = a_s.Bool("clean_game_plugins", false) && mod != null;
		if (games.Count > 0 && !copies) {
			var left = $"{games.Count} of the game's own plugins are dirty by LOOT's list and were left alone ({string.Join(", ", games.Select(g => g.Plugin))}): " +
				"the game's folder is never changed. Switch \"Also clean the game's own plugins\" on for this step to clean copies of them in an output mod";
			a_ctx.Note(left);
			a_ctx.Log.Simple("Note: " + left);
			games.Clear();
		}
		if (theirs.Count + games.Count == 0) {
			// its output mod, when it has one, is not this run's: left as it stands
			a_ctx.OutputUntouched = true;
			a_ctx.Log.Say("LOOT calls none of your plugins dirty - nothing to clean");
			return;
		}
		// the files are copied and the mods switched with Mod Organizer closed
		a_ctx.Control.Close(null, a_ctx.Cancel);
		var before = Runner.OverwriteFiles(a_ctx);
		var plan = new List<(string Plugin, string At)>(theirs);
		if (games.Count > 0) {
			// the old cleaned copies to the backup, then each of the game's plugins as it is now: the copy in the mod is the one
			// Mod Organizer shows the tool, so the game's folder is only read
			StepKit.ParkMod(a_ctx, StepKey, mod!);
			foreach (var (plugin, at) in games) {
				var copy = Path.Combine(a_ctx.Mo2.ModDir(mod!), plugin);
				Files.Copy(at, copy, true);
				plan.Add((plugin, copy));
			}
		} else if (mod != null) {
			a_ctx.OutputUntouched = true;
		}
		// one record per folder: a copy of each plugin as it is, before xEdit is started on any
		var records = new List<RewriteRecord>();
		foreach (var folder in theirs.GroupBy(t => FolderOf(a_ctx.Mo2, t.At), StringComparer.OrdinalIgnoreCase)) {
			var label = Files.SamePath(folder.Key, overwrite) ? "overwrite" : Path.GetFileName(folder.Key);
			records.Add(Rewrites.Keep(a_ctx, StepKey, label, folder.Key, folder.Select(t => Path.GetRelativePath(folder.Key, t.At)).ToList()));
		}
		var logs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		// the mod with the copies on for xEdit's runs (the run switched it off with every other output), and xEdit's own when it
		// lives in one that is off
		var on = StepKit.ToolMods(a_ctx, a_s);
		if (games.Count > 0) {
			on.Insert(0, mod!);
		}
		try {
			using (new Toggle(a_ctx, on, [], "xEdit", a_ctx.Cancel)) {
				for (var i = 0; i < plan.Count; ++i) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					var plugin = plan[i].Plugin;
					logs[plugin] = Path.Combine(a_ctx.Journal.Folder, $"xEdit {RewriteKit.AsFileName(plugin)}.txt");
					Files.Delete(logs[plugin]);
					a_ctx.Activity(StepKey, $"xEdit is cleaning {plugin} ({i + 1} of {plan.Count})", (double)i / plan.Count);
					a_ctx.Log.Detail($"xEdit: cleaning {plugin}");
					a_clean(plugin, logs[plugin]);
				}
			}
		} finally {
			// Mod Organizer is closed again, however the cleaning went: what xEdit left in overwrite is put right before anything
			// is judged (and before a failed step's files are laid back - a cleaned plugin left there would win over them)
			Collect(a_ctx, plan, before);
		}
		foreach (var (plugin, at) in plan) {
			// the original went into xEdit's backups and nothing took its place: its copy is laid back by the step's failure
			if (!File.Exists(at)) {
				throw new StepFailedException($"xEdit took {plugin} out of its folder and wrote no cleaned file in its place (its log: {logs[plugin]})");
			}
			// its own last line: without it the cleaning was cut off, whatever it wrote
			if (File.Exists(logs[plugin]) && !Files.ReadSharedText(logs[plugin]).Contains(Done, StringComparison.OrdinalIgnoreCase)) {
				throw new StepFailedException($"xEdit ended before it had finished cleaning {plugin} (its log: {logs[plugin]})");
			}
		}
		var cleaned = 0;
		foreach (var r in records) {
			cleaned += Rewrites.Settle(a_ctx, r).Changed;
		}
		// a copy of one of the game's plugins is cleaned when it no longer holds the game's bytes
		cleaned += games.Count(g => !Rewrites.Same(g.At, Path.Combine(a_ctx.Mo2.ModDir(mod!), g.Plugin)));
		if (games.Count > 0) {
			a_ctx.SetAfter(mod!);
		}
		var asWas = plan.Count - cleaned;
		var said = $"xEdit cleaned {cleaned} plugin(s) of the {plan.Count} LOOT calls dirty: {string.Join(", ", plan.Select(p => p.Plugin).Take(12))}{(plan.Count > 12 ? " ..." : "")}" +
			(asWas > 0 ? $" - {asWas} it left as they were (its logs are in the run's folder)" : "") +
			(games.Count > 0 ? $" - the game's own as copies in {mod}" : "");
		a_ctx.Note(said);
		a_ctx.Log.Simple(said);
	}

	// What xEdit left in Mod Organizer's overwrite for these plugins: a cleaned plugin goes to where its original stood; xEdit's
	// own backups, and a save it could not swap in, go into the run's backup (the step kept its own copy before xEdit started).
	// Found by name, never by date: xEdit gives a cleaned file its plugin's old time, and an original it renamed away keeps the
	// date it had. What stood in overwrite before the step and was not written since is the player's and stays. A file that
	// cannot be moved is said and left - this runs on the way out of a failure too, and never hides it behind one of its own.
	static void Collect(RunContext a_ctx, IReadOnlyList<(string Plugin, string At)> a_plan, IReadOnlyDictionary<string, DateTime> a_before)
	{
		var overwrite = a_ctx.Mo2.OverwriteDir;
		if (!Directory.Exists(overwrite)) {
			return;
		}
		try {
			foreach (var f in Directory.EnumerateFiles(overwrite, "*", SearchOption.AllDirectories).ToList()) {
				var name = Path.GetFileName(f);
				try {
					if (a_before.TryGetValue(f, out var was) && was == File.GetLastWriteTimeUtc(f)) {
						continue;
					}
					var home = a_plan.Where(p => p.Plugin.Equals(Path.GetRelativePath(overwrite, f), StringComparison.OrdinalIgnoreCase)).Select(p => p.At).FirstOrDefault();
					if (home != null && !Files.SamePath(home, f)) {
						Files.Move(f, home, true);
						a_ctx.Log.Detail($"{name}: its cleaned file moved from Mod Organizer's overwrite to where the plugin stood");
					} else if (a_plan.Any(p => name.StartsWith(p.Plugin + ".backup.", StringComparison.OrdinalIgnoreCase) || name.StartsWith(p.Plugin + ".save.", StringComparison.OrdinalIgnoreCase))) {
						var dst = Files.FreeFile(Path.Combine(a_ctx.BackupFolder, "xedit backups", name));
						Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
						Files.Move(f, dst);
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					a_ctx.Log.Warn($"{name} could not be taken out of Mod Organizer's overwrite ({e.Message})");
				}
			}
			// xEdit's backup folder, when this left it empty
			foreach (var d in Directory.EnumerateDirectories(overwrite, "*Edit Backups").ToList()) {
				if (!Directory.EnumerateFileSystemEntries(d).Any()) {
					Files.DeleteDir(d);
				}
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_ctx.Log.Warn("Mod Organizer's overwrite could not be looked through after xEdit: " + e.Message);
		}
	}

	// the folder a plugin's files are kept by: its mod's folder, or Mod Organizer's overwrite
	static string FolderOf(Mo2Instance a_mo2, string a_plugin)
	{
		var mods = Path.GetFullPath(a_mo2.ModsDir);
		var full = Path.GetFullPath(a_plugin);
		return Files.IsUnder(full, mods) ? Path.Combine(mods, Path.GetRelativePath(mods, full).Split(Path.DirectorySeparatorChar)[0]) : Path.GetDirectoryName(full)!;
	}
}

// A tool that works on a folder it is handed, with no part for Mod Organizer: each mod the step names gets a copy of the files
// the tool can change, the tool is run on it (by itself where it has a way, else in its own window with the player), and what
// it changed is settled mod by mod - a mod it finished stays as the tool left it even when a later one fails.
public abstract class RewriteToolStep : IStep
{
	public abstract string Key { get; }
	protected abstract string Tool { get; }
	// the files of a mod the tool can change, by name (null: any of them)
	public abstract Func<string, bool>? Takes { get; }
	// what the player is asked to do with a_folder in the tool's own window
	protected abstract string Ask(string a_folder);
	// the tool run on a_folder by itself, to its end (a tool with no such way never gets here)
	protected virtual void Itself(RunContext a_ctx, StepSettings a_s, string a_exe, string a_mod, string a_folder) => throw new StepFailedException($"{Tool} cannot run by itself");
	protected virtual bool CanRunItself => false;

	// the offline tests' stand-in for the tool: called with the mod's folder in the tool's place
	public Action<RunContext, StepSettings, string>? Stand { get; set; }

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.Str("exe") is not { Length: > 0 } exe || !File.Exists(exe)) {
			yield return new Issue(Key, true, $"{Tool}'s program is not found (set \"exe\")");
		}
		foreach (var i in RewriteKit.CheckMods(a_c, Key, a_s, a_s.Str("exe"))) yield return i;
		foreach (var i in Notes(a_s)) yield return i;
	}

	protected virtual IEnumerable<Issue> Notes(StepSettings a_s) => [];

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var exe = a_s.Req("exe");
		var itself = CanRunItself && a_s.Bool("unattended", false);
		// a mod's folder is every profile's: Mod Organizer is not left looking at it while a tool rewrites it
		a_ctx.Control.Close(null, a_ctx.Cancel);
		int changed = 0, removed = 0, added = 0, worked = 0;
		foreach (var mod in a_s.List("mods")) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var folder = a_ctx.Mo2.ModDir(mod);
			var files = Takes == null ? null : Rewrites.ListOf(folder).Where(f => Takes(Path.GetFileName(f))).ToList();
			if (files is { Count: 0 }) {
				a_ctx.Log.Detail($"{mod}: holds nothing {Tool} works on - left alone");
				continue;
			}
			var record = Rewrites.Keep(a_ctx, Key, mod, folder, files);
			a_ctx.Activity(Key, $"{Tool} is working on {mod}");
			a_ctx.Log.Detail($"{Tool}: {mod}");
			if (Stand != null) {
				Stand(a_ctx, a_s, folder);
			} else if (itself) {
				Itself(a_ctx, a_s, exe, mod, folder);
			} else {
				Guided(a_ctx, exe, Ask(folder));
			}
			var did = Rewrites.Settle(a_ctx, record);
			a_ctx.Log.Detail($"{mod}: {did.Changed} file(s) changed, {did.Removed} taken away, {did.Added} new");
			changed += did.Changed;
			removed += did.Removed;
			added += did.Added;
			++worked;
		}
		var said = changed + removed + added == 0
			? $"{Tool} changed nothing in the {worked} mod(s) it was given - was it run on the folder this step named?"
			: $"{Tool}: {changed} file(s) changed, {removed} taken away, {added} new, in {worked} mod(s) - Undo the last run puts them back";
		a_ctx.Note(said);
		a_ctx.Log.Simple(said);
	}

	// the tool opened on its own (it needs nothing of Mod Organizer), the player asked, and the tool closed before the step goes on:
	// what it changed is only counted once it has let go of the files
	void Guided(RunContext a_ctx, string a_exe, string a_ask)
	{
		var image = Path.GetFileName(a_exe);
		a_ctx.Control.Ours(Mo2Control.StartDetached(Mo2Control.Quote(a_exe), Path.GetDirectoryName(a_exe)!));
		try {
			a_ctx.Guide.Ask(Key, a_ask, a_ctx.Cancel);
			var sw = Stopwatch.StartNew();
			while (StepKit.StartedRunning(a_ctx.Control, image).Count > 0) {
				if (sw.Elapsed > TimeSpan.FromMinutes(2)) {
					throw new StepFailedException($"{Tool} is still open - close it, then start the step again");
				}
				a_ctx.Pause(TimeSpan.FromSeconds(1));
			}
		} finally {
			StepKit.CloseTool(a_ctx, image);
		}
	}
}

// SSE NIF Optimizer (ousnius, GPL-3.0; its source read, nothing of it copied). It saves every mesh it can read over itself, so
// the copies are of every mesh. Its command line - a folder to work on, --recursive, --headparts, --log - starts the work at
// once and closes the program when it is done, but cannot switch its options: they stay as its window first shows them, with
// Remove Parallax on. So it runs by itself only when the setup asks; else the player sets the ticks in its own window.
public sealed class NifOptimizerStep : RewriteToolStep
{
	public const string StepKey = "nifoptimizer";
	const string Finished = "Program finished.";

	public override string Key => StepKey;
	protected override string Tool => "SSE NIF Optimizer";
	protected override bool CanRunItself => true;

	static readonly string[] Meshes = [".nif", ".btr", ".bto"];

	public override Func<string, bool>? Takes => a_name => Meshes.Any(e => a_name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

	protected override string Ask(string a_folder) =>
		$"In SSE NIF Optimizer: choose the folder {a_folder}, set its options, press Optimize and wait until it is done; then close it and press Done.";

	protected override IEnumerable<Issue> Notes(StepSettings a_s)
	{
		if (a_s.Bool("unattended", false)) {
			yield return new Issue(Key, false, "SSE NIF Optimizer runs by itself with the options its window starts with, which it cannot be told to change: " +
				"Remove Parallax is on, so parallax is taken off every mesh it saves. Switch \"Run by itself\" off to set its options yourself in its window");
		}
	}

	public static List<string> Arguments(string a_folder, string a_log, bool a_headParts)
	{
		var args = new List<string> { "--recursive" };
		if (a_headParts) {
			args.Add("--headparts");
		}
		args.Add("--log=" + a_log);
		args.Add(a_folder);
		return args;
	}

	protected override void Itself(RunContext a_ctx, StepSettings a_s, string a_exe, string a_mod, string a_folder)
	{
		var log = Path.Combine(a_ctx.Journal.Folder, $"SSE NIF Optimizer {RewriteKit.AsFileName(a_mod)}.txt");
		Files.Delete(log);
		var psi = new ProcessStartInfo(a_exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(a_exe)! };
		foreach (var a in Arguments(a_folder, log, a_s.Bool("headparts", false))) {
			psi.ArgumentList.Add(a);
		}
		a_ctx.Log.Detail(Path.GetFileName(a_exe) + " " + string.Join(" ", psi.ArgumentList.Select(Mo2Control.Quote)));
		using var p = Process.Start(psi) ?? throw new StepFailedException("could not start " + a_exe);
		a_ctx.Control.Ours(p.Id);
		try {
			Waits.Tool(a_ctx, Key, p, a_s.Deadline(60), () => p.HasExited ? Waits.Poll.Done : Waits.Poll.Wait);
		} catch {
			Procs.KillTree(p.Id);
			throw;
		}
		Verdict(a_ctx, a_mod, log);
	}

	// Its exit code is always 0: its log is the verdict. A mesh it could not save was opened for writing first and may be cut
	// short, so that fails the step (every mesh of this mod then comes back from its copy); one it could not read was left as it is
	public static void Verdict(RunContext a_ctx, string a_mod, string a_log)
	{
		if (!File.Exists(a_log)) {
			throw new StepFailedException($"SSE NIF Optimizer wrote no log for {a_mod} - it did not run on it");
		}
		var lines = Files.ReadSharedText(a_log).Split('\n');
		if (!lines.Any(l => l.Contains(Finished, StringComparison.Ordinal))) {
			throw new StepFailedException($"SSE NIF Optimizer ended before it had finished {a_mod} (its log: {a_log})");
		}
		var unsaved = lines.Count(l => l.Contains("[ERROR] Failed to save", StringComparison.Ordinal));
		if (unsaved > 0) {
			throw new StepFailedException($"SSE NIF Optimizer could not save {unsaved} mesh(es) of {a_mod} (its log: {a_log})");
		}
		var unread = lines.Count(l => l.Contains("[ERROR] Failed to load", StringComparison.Ordinal));
		if (unread > 0) {
			a_ctx.Note($"SSE NIF Optimizer could not read {unread} mesh(es) of {a_mod} and left them as they are (its log: {a_log})");
		}
	}
}

// Cathedral Assets Optimizer (G'k, MPL-2.0; its source read, nothing of it copied). The program on its page takes no command
// line - that exists only in a build without its window - so it is guided: the player runs it on the folder the step names.
// It can rewrite, convert and pack any kind of file, so the copies are of the whole mod.
public sealed class CaoStep : RewriteToolStep
{
	public const string StepKey = "cao";

	public override string Key => StepKey;
	protected override string Tool => "Cathedral Assets Optimizer";
	public override Func<string, bool>? Takes => null;

	protected override string Ask(string a_folder) =>
		$"In Cathedral Assets Optimizer: open the folder {a_folder} (One mod), pick what it should do, press Run and wait until it is done; then close it and press Done.";
}
