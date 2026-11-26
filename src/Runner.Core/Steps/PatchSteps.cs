using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Steps;

// Landscape Fixer's launcher (Cl3mus): its settings are saved in the tool, one button starts it, a box says "complete". It
// writes into a folder of its own (`tool_writes`) that is emptied first and moved into the output mod after.
public abstract class LauncherStep : IStep
{
	public abstract string Key { get; }
	protected abstract string TitleRx { get; }
	protected abstract string[] StartNames { get; }
	protected abstract string SettingsFile(RunContext a_ctx);

	// the tool keeps one Mod Organizer, profile and output in its own settings, whatever it was last pointed at: without this it
	// builds from that other profile and writes into that other mods folder. The file goes back after the step
	public static string PointedAt(string? a_settings, string a_instance, string a_profile, string a_game, string a_output)
	{
		var json = a_settings is { Length: > 0 } ? JsonNode.Parse(a_settings, documentOptions: JsonRead.Plain)?.AsObject() ?? [] : [];
		json["ModManager"] = 1;
		json["Mo2InstancePath"] = a_instance;
		json["Mo2ProfileName"] = a_profile;
		json["GameLocation"] = a_game;
		json["OutputLocation"] = a_output;
		return json.ToJsonString(JsonWrite.Indented);
	}

	// The tool's window and its settings file are its author's and may change in any version, with no word to this program: a
	// failure that comes from reading them is this program's to fix, and says so - its author is never sent the report
	public static string OursToFix(string a_name) =>
		$" This is Tool Runner's to fix, not {a_name}'s: a newer {a_name} may have changed its window or its settings file. Report it on Tool Runner's GitHub page, never on {a_name}'s own page.";

	// the fields that decide what the tool reads and where it writes: one renamed in its settings file would leave this
	// program's value unread, and the tool would build from whatever profile and into whatever folder it was last pointed at
	// (the game's folder too: renamed, the tool would read its own new key and build for a game folder it was given before)
	static readonly string[] Pointing = ["Mo2InstancePath", "Mo2ProfileName", "GameLocation", "OutputLocation"];

	// and the field that tells it which mod manager the mods are in (PointedAt writes it as Mod Organizer's every run)
	const string Manager = "ModManager";

	// the first of those fields a settings file the tool already wrote does not hold (null: all there, or no file yet - a new
	// one gets them all). A file that holds no object, or an empty one, holds none of them
	public static string? MissingField(string? a_settings)
	{
		if (a_settings == null) {
			return null;
		}
		if (a_settings.Trim().Length == 0) {
			return Pointing[0];
		}
		var held = JsonNode.Parse(a_settings, documentOptions: JsonRead.Plain) as JsonObject;
		return held == null ? Pointing[0] : Pointing.Append(Manager).FirstOrDefault(k => !held.ContainsKey(k));
	}

	// What stops the step in the tool's settings file before anything of it changes (null: nothing - no file yet is fine, a new
	// one gets every field): a file that is there but blank, one that does not read as settings, one that lost a field this
	// program points
	public static string? SettingsProblem(string? a_settings, string a_name)
	{
		if (a_settings != null && a_settings.Trim().Length == 0) {
			return $"{a_name}'s settings file is empty - nothing was changed and {a_name} was not started. Open {a_name} once through Mod Organizer, let it save its settings, then run the step again.";
		}
		string? gone;
		try {
			gone = MissingField(a_settings);
		} catch (JsonException) {
			return $"{a_name}'s settings file does not read as settings - nothing was changed and {a_name} was not started." + OursToFix(a_name);
		}
		var why = gone == Manager ? "tell it the mods are in Mod Organizer" : "point it at this profile and this output";
		return gone == null ? null : $"{a_name}'s settings file no longer holds \"{gone}\", which Tool Runner sets to {why} - nothing was changed and {a_name} was not started." + OursToFix(a_name);
	}

	// the tool's settings file still holds every field this program points: looked at before anything of the step is changed
	void CheckSettings(RunContext a_ctx)
	{
		var file = SettingsFile(a_ctx);
		if (SettingsProblem(File.Exists(file) ? Files.ReadSharedText(file) : null, StepCatalog.Name(Key)) is { } why) {
			throw new StepFailedException(why);
		}
	}

	void PointAtThisRun(RunContext a_ctx, string a_output)
	{
		var file = SettingsFile(a_ctx);
		// the instance is the folder that holds ModOrganizer.ini (its base folder can point elsewhere - a mods folder moved off it)
		var text = PointedAt(File.Exists(file) ? File.ReadAllText(file) : null, Path.GetDirectoryName(a_ctx.Mo2.IniPath)!, a_ctx.Profile.Name, a_ctx.Mo2.GameDir, a_output);
		StepKit.KeepFile(a_ctx, file);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		Files.WriteAtomic(file, text);
		a_ctx.Log.Detail($"{StepCatalog.Name(Key)}: pointed at this profile and {Path.GetFileName(a_output)}");
	}

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		// its own write folder is emptied before the run: never the mod the tool itself is installed in
		if (a_s.Str("tool_writes") is { Length: > 0 } tw && Directory.Exists(a_c.Mo2.ModDir(tw)) &&
		    Directory.EnumerateFiles(a_c.Mo2.ModDir(tw), "*.exe", SearchOption.AllDirectories).Any()) {
			yield return new Issue(Key, true, $"\"{tw}\" (the folder it writes into) holds a program - it must be the tool's output folder, not the tool itself");
		}
	}

	// the folder beside its program that holds its translations (each a JSON file with its button's words), or null
	protected virtual string? TranslationFolder => null;

	// the plugin a finished run writes last (null = none known)
	protected virtual string? OutputPlugin => null;

	// the names its start button may carry: its own words in every language it ships ("run" > "generate" in each translation file
	// - a tool set to another language is understood too), then the English ones
	public static List<string> StartButtonNames(string? a_translations, IEnumerable<string> a_english)
	{
		var names = new List<string>();
		if (a_translations != null && Directory.Exists(a_translations)) {
			foreach (var f in Directory.EnumerateFiles(a_translations, "*.json")) {
				try {
					// a file of another shape (a list, a plain value) is passed over - indexing it by name would throw and stop the step
					if (JsonNode.Parse(Files.ReadSharedText(f), documentOptions: JsonRead.Plain) is JsonObject root && root["run"] is JsonObject run &&
					    run["generate"] is JsonValue v && v.TryGetValue<string>(out var n) && n.Trim() != "") {
						names.Add(n.Trim());
					}
				} catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) {
				}
			}
		}
		names.AddRange(a_english);
		return names.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
	}

	static bool Exited(System.Diagnostics.Process a_p)
	{
		try {
			return a_p.HasExited;
		} catch (InvalidOperationException) {
			return true;
		}
	}

	static string Short(string a_t) => a_t.Length > 400 ? "..." + a_t[^400..] : a_t;

	// what its window says, for a failure: its text boxes (Landscape Fixer's run log) last, the labels before them
	static string WindowSays(System.Windows.Automation.AutomationElement a_w)
	{
		var log = Uia.EditTexts(a_w).OrderBy(t => t.Length).LastOrDefault() ?? "";
		return Short(Uia.TextOf(a_w, 1500) + (log.Trim() == "" ? "" : " | its log: " + log.Trim()));
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var name = StepCatalog.Name(Key);
		// the process to watch: the setup's own, else the program MO2's entry starts (its title need not be the file's name)
		var image = a_s.Str("image") ?? (a_ctx.Mo2.Executable(a_s.Req("executable"))?.Binary is { Length: > 0 } binary ? Path.GetFileName(binary) : a_s.Req("executable") + ".exe");
		CheckSettings(a_ctx);
		StepKit.ParkMod(a_ctx, Key, mod);
		var tw = a_s.Str("tool_writes");
		var toolDir = tw is { Length: > 0 } ? a_ctx.Mo2.ModDir(tw) : null;
		if (toolDir != null && Directory.Exists(toolDir)) {
			// its files to the backup (recorded, put back if the step does not finish); the folder and its meta.ini stay
			StepKit.ParkMod(a_ctx, Key, tw!);
		}
		var outDir = toolDir ?? a_ctx.Mo2.ModDir(mod);
		var t0 = DateTime.UtcNow;
		PointAtThisRun(a_ctx, outDir);
		var exeDir = a_ctx.Mo2.Executable(a_s.Req("executable"))?.Binary is { Length: > 0 } bin ? Path.GetDirectoryName(bin) : null;
		var names = StartButtonNames(exeDir != null && TranslationFolder != null ? Path.Combine(exeDir, TranslationFolder) : null, StartNames).ToArray();
		using (new Toggle(a_ctx, [mod, .. a_s.List("enable_during"), .. StepKit.ToolMods(a_ctx, a_s)], a_s.List("disable_during"), name, a_ctx.Cancel)) {
			try {
				using var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args"), a_ctx.Cancel);
				// (its window is known by its title: one renamed in a newer version is this program's to follow)
				var w = Uia.WaitWindow(StepKit.PidsOf(image), t => Regex.IsMatch(t, TitleRx), TimeSpan.FromMinutes(3), a_ctx.Cancel) ??
					throw new StepFailedException($"{name}'s window did not appear within 3 minutes." + OursToFix(name));
				// its button, once its window has finished laying itself out (usable, on screen)
				System.Windows.Automation.AutomationElement? go = null;
				for (var sw = System.Diagnostics.Stopwatch.StartNew(); go == null; a_ctx.Pause(TimeSpan.FromSeconds(1))) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					go = Uia.Button(w, names) is { } b && Uia.Usable(b) ? b : null;
					if (go == null && (Exited(proc) || sw.Elapsed > TimeSpan.FromMinutes(1))) {
						throw new StepFailedException($"{name} shows no usable {StartNames[0]} button. Its window shows: {WindowSays(w)}." + OursToFix(name));
					}
				}
				Uia.Press(go);
				// it started = its button goes grey while it works. Landscape Fixer refuses a run it cannot do (no game, no profile)
				// only on its status line - never a box - so that line is what the step names
				for (var sw = System.Diagnostics.Stopwatch.StartNew(); Uia.Usable(go); a_ctx.Pause(TimeSpan.FromMilliseconds(500))) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					UnknownBox(name, image);
					// a run so short its button went grey and back between two looks has still written its output
					if (Files.NewestWrite(outDir) > t0) {
						break;
					}
					if (Exited(proc)) {
						throw new StepFailedException($"{name} closed when {Uia.Name(go)} was pressed");
					}
					if (sw.Elapsed > TimeSpan.FromSeconds(30)) {
						throw new StepFailedException($"{name} did not start working when {Uia.Name(go)} was pressed. Its window shows: {WindowSays(w)}." + OursToFix(name));
					}
				}
				a_ctx.Activity(Key, name + " is patching");
				// done = its button usable again (its run ended) or it closed; a box during a run is none it shows on its own way, so
				// it is named and left for the player (an unknown box is never read as something known)
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(60), () => {
					UnknownBox(name, image);
					return Exited(proc) || Uia.Usable(go) ? Waits.Poll.Done : Waits.Poll.Wait;
				});
				// a run that ended without writing anything stopped on an error, which its own log box holds
				if (Files.NewestWrite(outDir) <= t0) {
					throw new StepFailedException($"{name} ended without writing anything into {Path.GetFileName(outDir)}. " +
						(Exited(proc) ? "It closed." : "Its window shows: " + WindowSays(w)));
				}
				// its plugin is the last thing it writes: meshes without a fresh plugin are a run that stopped part way (they never
				// replace the last good output)
				if (OutputPlugin is { } plugin && !(File.Exists(Path.Combine(outDir, plugin)) && File.GetLastWriteTimeUtc(Path.Combine(outDir, plugin)) > t0)) {
					throw new StepFailedException($"{name} stopped before it wrote {plugin} - its run did not finish. " +
						(Exited(proc) ? "It closed." : "Its window shows: " + WindowSays(w)));
				}
				a_ctx.Log.Detail($"{name} finished its run");
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		if (toolDir != null && Directory.Exists(toolDir)) {
			// its files into the output mod; its own folder stays (empty, its meta.ini kept) - nothing in a mod folder is erased
			Files.MoveContents(toolDir, a_ctx.Mo2.ModDir(mod), n => n.Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
			StepKit.MarkReplaced(a_ctx, tw!);
			a_ctx.Log.Detail($"its files moved into {mod}");
		}
		var n = Files.CountFiles(a_ctx.Mo2.ModDir(mod));
		a_ctx.Log.Detail($"{mod} holds {n} file(s)");
		if (n < a_s.Int("min_files", 3)) {
			throw new StepFailedException($"{mod} came out empty");
		}
		a_ctx.SetAfter(mod);
	}

	static void UnknownBox(string a_name, string a_image)
	{
		if (Uia.Boxes(StepKit.PidsOf(a_image)()).FirstOrDefault() is { } box) {
			// its whole text goes into the log (the failure line keeps it too); the tool is then closed so the profile can be put back
			throw new StepFailedException($"{a_name} showed a box this step does not know, so the step stopped without answering it (the tool is closed " +
				$"so your profile can be put back; run {a_name} yourself to see the box again). It said: {box.Text.Trim()}");
		}
	}
}

// Landscape Fixer (Cl3mus, GPL-3.0) replaced their SnowFixer and AutoBlend: one run, one output, one plugin
public sealed class LandscapeFixerStep : LauncherStep
{
	public override string Key => "landscapefixer";
	protected override string TitleRx => "^Landscape Fixer";
	protected override string[] StartNames => ["Generate", "Start"];
	protected override string? TranslationFolder => "LandscapeFixer_translations";
	protected override string? OutputPlugin => "LandscapeFixer.esp";

	// it keeps one settings file per Mod Organizer instance, named after the folder holding that instance's ModOrganizer.ini
	protected override string SettingsFile(RunContext a_ctx) =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LandscapeFixer", "instances", InstanceFile(a_ctx.Mo2.IniPath));

	public static string InstanceFile(string a_ini) =>
		string.Concat(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(a_ini)))!.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".json";
}

// PGPatcher --autostart. A setup may keep several settings sets (ENB / PBR / Vanilla) beside its file and pick one; the live
// settings file goes back as it was after the run. Done = a new PGPatcher.esp and the output quiet for a minute (it idles).
public sealed class PgPatcherStep : IStep
{
	public string Key => "pgpatcher";

	static string? SettingsSet(Setup a_setup, StepSettings a_s) =>
		a_s.Str("settings") is { Length: > 0 } set ? Path.Combine(Path.GetDirectoryName(a_setup.File) ?? "", "pgpatcher settings", set + ".json") : null;

	// PGPatcher reads <its exe folder>\cfg\settings.json (as MO2 shows it)
	static string LiveSettings(Mo2Instance a_mo2, StepSettings a_s) =>
		a_s.Str("live_settings") ??
		(a_s.Str("pgpatcher_mod") is { } mod ? Path.Combine(a_mo2.ModDir(mod), "PGPatcher", "cfg", "settings.json") : null) ??
		(a_mo2.Executable(a_s.Str("executable") ?? "")?.Binary is { Length: > 0 } bin ? Path.Combine(Path.GetDirectoryName(bin)!, "cfg", "settings.json") : "");

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		var set = SettingsSet(a_c.Setup, a_s);
		if (set != null && !File.Exists(set)) {
			yield return new Issue(Key, true, "the PGPatcher settings set is missing: " + set);
		} else if (set != null && a_s.Bool("pglite_check", false)) {
			var conf = JsonNode.Parse(Files.ReadSharedText(set), documentOptions: JsonRead.Plain) as JsonObject;
			var mode = conf?["params"]?["processing"]?["pglitemode"]?.GetValue<bool>() == true;
			var ticked = Runner.PgLiteTicked(a_c.Profile);
			if (mode != ticked) {
				yield return new Issue(Key, true, $"PGLite is {(ticked ? "ticked" : "not ticked")} in this profile but the PGPatcher settings have PGLite Mode {(mode ? "on" : "off")}");
			}
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? "PGPatcher.exe";
		var t0 = DateTime.UtcNow;
		var live = LiveSettings(a_ctx.Mo2, a_s);
		var set = SettingsSet(a_ctx.Setup, a_s);
		// its output pointed at the output mod for the run: a stored settings set (ENB / PBR ...) or PGPatcher's own file
		var source = set ?? (File.Exists(live) ? live : null);
		// the old output to the backup first (PGPatcher stopped half way would leave a half output and nothing to put back)
		StepKit.ParkMod(a_ctx, Key, mod);
		try {
			if (source != null) {
				StepKit.KeepFile(a_ctx, live);
				// (a file that is no JSON at all left the step as an error of the reader's, with no word on what to do)
				JsonObject? conf;
				try {
					conf = JsonNode.Parse(Files.ReadSharedText(source), documentOptions: JsonRead.Plain) as JsonObject;
				} catch (System.Text.Json.JsonException e) {
					throw new StepFailedException($"PGPatcher's settings do not read ({Path.GetFileName(source)}: {e.Message})");
				}
				if (conf == null) {
					throw new StepFailedException($"PGPatcher's settings do not read ({Path.GetFileName(source)} holds no settings)");
				}
				if (conf["params"]?["output"] is not JsonObject output) {
					throw new StepFailedException("PGPatcher's settings have no output section - run PGPatcher once by hand to make them");
				}
				output["dir"] = a_ctx.Mo2.ModDir(mod);
				if (conf["params"]?["game"] is JsonObject game) {
					game["dir"] = a_ctx.Mo2.GameDir;
				}
				if (conf["params"]?["modmanager"] is JsonObject manager && manager.ContainsKey("mo2instancedir")) {
					manager["mo2instancedir"] = Path.GetDirectoryName(a_ctx.Mo2.IniPath)!;
				}
				Files.WriteAtomic(live, conf.ToJsonString(JsonWrite.Indented));
				a_ctx.Log.Detail($"PGPatcher settings: {a_s.Str("settings") ?? "its own"} (output {mod})");
			}
			using (new Toggle(a_ctx, StepKit.ToolMods(a_ctx, a_s), [mod, .. a_s.List("disable_during")], "PGPatcher", a_ctx.Cancel)) {
				try {
					using var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args") ?? "--autostart", a_ctx.Cancel);
					var esp = Path.Combine(a_ctx.Mo2.ModDir(mod), "PGPatcher.esp");
					a_ctx.Activity(Key, "PGPatcher is patching");
					Waits.Tool(a_ctx, Key, proc, a_s.Deadline(60), () => {
						foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
							if (box.Text.Contains("Stop Generation", StringComparison.Ordinal)) {
								continue;
							}
							if (Regex.IsMatch(box.Text, "must be disabled prior to running")) {
								throw new StepFailedException("PGPatcher refused to run: " + box.Text);
							}
							if (Regex.IsMatch(box.Text, "has completed generating output")) {
								var m = Regex.Match(box.Text, @"Show Warnings \((\d+)\).*?Show Errors \((\d+)\)", RegexOptions.Singleline);
								a_ctx.Note($"PGPatcher: {(m.Success ? m.Groups[1].Value : "?")} warning(s), {(m.Success ? m.Groups[2].Value : "?")} error(s)");
							} else if (Regex.IsMatch(box.Text.Split("collapsiblePane")[0], "error|failed|exception", RegexOptions.IgnoreCase)) {
								throw new StepFailedException("PGPatcher: " + box.Text);
							}
							// a notice gets OK; a question it does not know (Yes / No) stops the step with its text
							if (Uia.Button(box.Element, "No") != null && !Regex.IsMatch(box.Text, "has completed generating output")) {
								throw new StepFailedException("PGPatcher asks a question this step does not answer: " + box.Text.Split("collapsiblePane")[0].Trim());
							}
							if (Uia.Button(box.Element, "OK", "Close") is { } ok) {
								Uia.Press(ok);
							}
						}
						return File.Exists(esp) && File.GetLastWriteTimeUtc(esp) > t0 && DateTime.UtcNow - Files.NewestWrite(a_ctx.Mo2.ModDir(mod)) > TimeSpan.FromSeconds(60) ?
							Waits.Poll.Done : Waits.Poll.Wait;
					}, a_onIdle: () => File.Exists(esp) && File.GetLastWriteTimeUtc(esp) > t0);
					a_ctx.Log.Detail("PGPatcher finished (a new PGPatcher.esp, its output quiet for a minute)");
				} finally {
					StepKit.CloseTool(a_ctx, image);
				}
			}
		} finally {
			if (source != null) {
				StepKit.PutFilesBack(a_ctx, live);
			}
		}
		var outEsp = Path.Combine(a_ctx.Mo2.ModDir(mod), "PGPatcher.esp");
		if (!File.Exists(outEsp) || File.GetLastWriteTimeUtc(outEsp) < t0) {
			throw new StepFailedException($"PGPatcher did not write a new PGPatcher.esp into {mod}");
		}
		a_ctx.SetAfter(mod);
	}
}

// Synthesis: it will not build its patchers inside MO2 (its own setting), so it is opened OUTSIDE MO2 first to build, then
// run inside MO2; done = a new Synthesis.esp (in overwrite or in the output mod) that stopped changing
public sealed class SynthesisStep : IStep
{
	public string Key => "synthesis";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var image = a_s.Str("image") ?? "Synthesis.exe";
		var exePath = a_s.Str("exe_path") ?? a_ctx.Mo2.Executable(a_s.Req("executable"))?.Binary ?? throw new StepFailedException("Synthesis's program is not found");
		var espMod = Path.Combine(a_ctx.Mo2.ModDir(mod), "Synthesis.esp");
		var espOw = Path.Combine(a_ctx.Mo2.OverwriteDir, "Synthesis.esp");
		var t0 = DateTime.UtcNow;
		// a Synthesis whose output is the mod itself rewrites the plugin where it stands
		StepKit.KeepInPlace(a_ctx, mod, "Synthesis.esp");
		Build(a_ctx, a_s, image, exePath);
		using (new Toggle(a_ctx, StepKit.ToolMods(a_ctx, a_s), a_s.List("disable_during"), "Synthesis", a_ctx.Cancel)) {
			try {
				var tRun = DateTime.UtcNow;
				var logs = Path.Combine(Path.GetDirectoryName(exePath)!, "logs");
				string? runLog = null;
				UseMo2Game(a_ctx, exePath);
				using var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, null, a_ctx.Cancel);
				// the first look waits for its window to come; a later one only briefly (a gone window is caught by Press's exit check,
				// never waited on for minutes)
				var found = false;
				System.Windows.Automation.AutomationElement Win()
				{
					var w = Window(a_ctx, image, found ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(10));
					found = true;
					return w;
				}
				Press(a_ctx, Win, proc);
				a_ctx.Activity(Key, "Synthesis is running its patchers");
				// judged by its window (its timers and patcher states change while it works), never by its CPU alone; a patcher
				// that failed is read off Synthesis's own log the moment its run ends (one failed, no Synthesis.esp)
				DateTime? exitedAt = null;
				var grown = new GrowingText { KeepAll = false };
				var said = new LogDigest();
				Waits.Tool(a_ctx, Key, proc, a_s.Deadline(90), () => {
					var fresh = new[] { espOw, espMod }.FirstOrDefault(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) > t0);
					if (fresh != null && DateTime.UtcNow - File.GetLastWriteTimeUtc(fresh) > TimeSpan.FromSeconds(20)) {
						return Waits.Poll.Done;
					}
					runLog ??= RunLog(logs, tRun);
					if (fresh == null && runLog != null && Judged(runLog, grown, said) is { } why) {
						throw new StepFailedException($"Synthesis: {why} - it wrote no Synthesis.esp (its log: {runLog})");
					}
					// a Synthesis that closed is given a moment for its last write, then the step ends - never the 90-minute deadline
					if (fresh == null && Exited(proc)) {
						exitedAt ??= DateTime.UtcNow;
						if (DateTime.UtcNow - exitedAt > TimeSpan.FromSeconds(30)) {
							throw new StepFailedException("Synthesis closed without writing Synthesis.esp" + (runLog != null ? $" (its log: {runLog})" : ""));
						}
					}
					return Waits.Poll.Wait;
				}, a_progress: () => Uia.WaitWindow(StepKit.PidsOf(image), t => t.Contains("Synthesis", StringComparison.Ordinal), TimeSpan.FromSeconds(2), a_ctx.Cancel) is { } open ? Uia.TextOf(open, int.MaxValue) : "");
			} finally {
				StepKit.CloseTool(a_ctx, image);
			}
		}
		if (File.Exists(espOw) && File.GetLastWriteTimeUtc(espOw) > t0) {
			StepKit.ParkMod(a_ctx, Key, mod);
			Files.Move(espOw, espMod, true);
			a_ctx.Log.Detail($"Synthesis.esp moved from overwrite into {mod}");
		} else if (!File.Exists(espMod) || File.GetLastWriteTimeUtc(espMod) < t0) {
			throw new StepFailedException("Synthesis wrote no new Synthesis.esp");
		}
		StepKit.Settled(a_ctx, mod, [espMod]);
		a_ctx.SetAfter(mod);
	}

	// the log Synthesis started for this run (it keeps one per start, plus Current.log)
	static string? RunLog(string a_dir, DateTime a_since) => Directory.Exists(a_dir)
		? new DirectoryInfo(a_dir).GetFiles("*.log").Where(f => !f.Name.Equals("Current.log", StringComparison.OrdinalIgnoreCase) && f.CreationTimeUtc >= a_since)
			.OrderByDescending(f => f.CreationTimeUtc).Select(f => f.FullName).FirstOrDefault()
		: null;

	// a finished run whose log holds an error: which patcher failed and why; null while it runs or when nothing failed
	public static string? Failure(string a_log)
	{
		var all = new LogDigest();
		all.Take(a_log);
		return all.Failure;
	}

	// The same, of a log taken as it grows: what the verdict needs is kept from each addition. The wait read and searched the
	// whole log at every look - megabytes of it, every two seconds, in a long run
	public sealed class LogDigest
	{
		bool _finished;
		// the patcher that last started, the first error, and the patcher that had started when it came
		string? _patcher, _error, _failed;

		public void Restart() => (_finished, _patcher, _error, _failed) = (false, null, null, null);

		// lines the log gained
		public void Take(string a_added)
		{
			foreach (var line in a_added.Split('\n')) {
				Line(line);
			}
		}

		void Line(string a_line)
		{
			_finished |= a_line.Contains("Finished patcher run", StringComparison.Ordinal);
			if (_error != null) {
				return;
			}
			if (a_line.Contains("[ERR] [T", StringComparison.Ordinal) && Regex.Match(a_line, @"\[ERR\] \[T\d+\] (?<m>.+)") is { Success: true } m &&
			    m.Groups["m"].Value.Trim() is { Length: > 0 } said && !said.StartsWith("Error while running", StringComparison.Ordinal)) {
				(_error, _failed) = (said, _patcher);
			} else if (a_line.Contains("Starting Patcher ", StringComparison.Ordinal) && Regex.Matches(a_line, @"Starting Patcher (?<p>.+?) Run").LastOrDefault() is { } start) {
				_patcher = start.Groups["p"].Value;
			}
		}

		public string? Failure => !_finished || _error == null ? null :
			(_failed != null ? $"its patcher \"{_failed}\" failed: " : "a patcher failed: ") + (_error.Length > 300 ? _error[..300] : _error);

		// the same, with the line the log is still writing counted in
		public string? FailureWith(string a_unfinished)
		{
			if (a_unfinished.Length == 0) {
				return Failure;
			}
			var with = (LogDigest)MemberwiseClone();
			with.Line(a_unfinished);
			return with.Failure;
		}
	}

	// what the run's log says of a failure, read by what it gained since the last look
	static string? Judged(string a_file, GrowingText a_log, LogDigest a_said)
	{
		a_log.Read(a_file);
		if (a_log.Restarted) {
			a_said.Restart();
		}
		a_said.Take(a_log.Added);
		return a_said.FailureWith(a_log.Unfinished);
	}

	public const string GoRx = @"^GoButton|\bRun\b|RunPatchers";

	// its Run button: an enabled one on screen (it has two "GoButton"s - the first one pressed nothing and the window
	// sat on its main screen), pressed, and the run proven started by its window changing; else the other one, else the step fails
	// naming what the window shows - never a window left open doing nothing
	static void Press(RunContext a_ctx, Func<System.Windows.Automation.AutomationElement> a_w, System.Diagnostics.Process a_proc)
	{
		var idle = TimeSpan.FromMinutes(Math.Max(1, a_ctx.Setup.IdleMinutes));
		var sw = System.Diagnostics.Stopwatch.StartNew();
		List<System.Windows.Automation.AutomationElement> Buttons() => Uia.All(a_w(), System.Windows.Automation.ControlType.Button)
			.Where(b => Regex.IsMatch(Uia.AutomationId(b) + " " + Uia.Name(b), GoRx, RegexOptions.IgnoreCase) && Uia.Usable(b)).ToList();
		// a window that went away reads as "no buttons, new text" - so a closed Synthesis is caught first, never taken for a start
		void StillOpen(string a_when)
		{
			if (Exited(a_proc)) {
				throw new StepFailedException($"Synthesis's window closed {a_when}");
			}
		}
		static string Short(string a_t) => a_t.Length > 300 ? a_t[..300] + " ..." : a_t;
		var go = Buttons();
		DateTime? blockedSince = null;
		while (go.Count == 0) {
			a_ctx.Cancel.ThrowIfCancellationRequested();
			StillOpen("before its Run button showed");
			// the whole window is searched; only the message is cut short
			var txt = Uia.TextOf(a_w(), int.MaxValue);
			if (StillBlocked(ref blockedSince, Regex.IsMatch(txt, "Build required|Blocking Error"), DateTime.UtcNow)) {
				throw new StepFailedException("Synthesis still wants a build inside Mod Organizer: " + Short(txt));
			}
			if (sw.Elapsed > idle) {
				throw new StepFailedException($"Synthesis showed no usable Run button within {idle.TotalMinutes:0} minutes. Its window shows: {Short(txt)}");
			}
			a_ctx.Pause(TimeSpan.FromSeconds(2));
			go = Buttons();
		}
		foreach (var b in go) {
			// the whole window, so a change far down a big pipeline still counts as the start
			var before = Uia.TextOf(a_w(), int.MaxValue);
			if (!Uia.Press(b)) {
				continue;
			}
			var sincePress = System.Diagnostics.Stopwatch.StartNew();
			while (sincePress.Elapsed < TimeSpan.FromSeconds(45)) {
				a_ctx.Pause(TimeSpan.FromMilliseconds(1500));
				StillOpen("right after Run was pressed");
				if (Uia.TextOf(a_w(), int.MaxValue) != before || Buttons().Count == 0) {
					a_ctx.Log.Detail("Synthesis: Run pressed, its patchers started");
					return;
				}
			}
			a_ctx.Log.Warn("Synthesis: a Run button pressed, nothing changed in 45 s - trying the next one");
		}
		throw new StepFailedException("Synthesis did not start its patchers when Run was pressed. Its window shows: " + Short(Uia.TextOf(a_w(), int.MaxValue)));
	}

	// Synthesis finds the game through Steam; with a game copy of Mod Organizer's own that is a folder MO2 does not map, every plugin
	// "could not be found". The file goes back after the step; a data folder the player set is left alone
	static void UseMo2Game(RunContext a_ctx, string a_exe)
	{
		var file = Path.Combine(Path.GetDirectoryName(a_exe)!, "PipelineSettings.json");
		if (!File.Exists(file)) {
			return;
		}
		var text = File.ReadAllText(file);
		var set = WithDataFolder(text, Path.Combine(a_ctx.Mo2.GameDir, "Data"));
		if (set != text) {
			StepKit.KeepFile(a_ctx, file);
			Files.WriteAtomic(file, set);
			a_ctx.Log.Detail("Synthesis: pointed at the game Mod Organizer runs for this step");
		}
	}

	// every profile whose data folder is unset (null or left out) gets the game MO2 runs; one the player set is kept
	public static string WithDataFolder(string a_settings, string a_data)
	{
		JsonNode? root;
		try {
			root = JsonNode.Parse(a_settings, documentOptions: JsonRead.Plain);
		} catch (System.Text.Json.JsonException) {
			return a_settings;
		}
		// (a file of another layout keeps the old way: every unset value named in it is set)
		if (root is not JsonObject top || top["Profiles"] is not JsonArray profiles) {
			return Regex.Replace(a_settings, @"""DataPathOverride""\s*:\s*null", m => "\"DataPathOverride\": " + System.Text.Json.JsonSerializer.Serialize(a_data));
		}
		var changed = false;
		foreach (var p in profiles.OfType<JsonObject>()) {
			if (p["DataPathOverride"] is null) {
				p["DataPathOverride"] = a_data;
				changed = true;
			}
		}
		return changed ? top.ToJsonString(JsonWrite.Indented) : a_settings;
	}

	// found again each time: the window seen first can be its start-up window, gone a moment later, and a gone window reads as empty
	static System.Windows.Automation.AutomationElement Window(RunContext a_ctx, string a_image, TimeSpan a_wait) =>
		StepKit.WaitWindow(a_ctx, a_image, t => t.Contains("Synthesis", StringComparison.Ordinal), a_wait, "Synthesis's window");

	static bool Exited(System.Diagnostics.Process a_p)
	{
		try {
			return a_p.HasExited;
		} catch (InvalidOperationException) {
			return true;
		}
	}

	// Synthesis shows a blocking error for a few seconds while it loads its settings ("no enabled groups to run",
	// then "No global error" 5 s later), so only one that stays 30 s is real
	public static bool StillBlocked(ref DateTime? a_since, bool a_blockedNow, DateTime a_now)
	{
		if (!a_blockedNow) {
			a_since = null;
			return false;
		}
		a_since ??= a_now;
		return a_now - a_since.Value >= TimeSpan.FromSeconds(30);
	}

	static void Build(RunContext a_ctx, StepSettings a_s, string a_image, string a_exe)
	{
		StepKit.CloseTool(a_ctx, a_image);
		a_ctx.Log.Detail("Synthesis: building its patchers outside Mod Organizer first");
		a_ctx.Activity("synthesis", "building Synthesis's patchers");
		// the build copy is the run's own: closed by its PID after the build (else the run inside MO2 finds Synthesis running)
		a_ctx.Control.Started.Add(Mo2Control.StartDetached(Mo2Control.Quote(a_exe), Path.GetDirectoryName(a_exe)!));
		var sw = System.Diagnostics.Stopwatch.StartNew();
		var last = "";
		try {
			// the build copy shows the same loading-time blocking error as the run: it counts only once it stays 30 s; a "failed" at once
			DateTime? blockedSince = null;
			while (sw.Elapsed < TimeSpan.FromMinutes(a_s.Int("build_min", 15))) {
				a_ctx.Cancel.ThrowIfCancellationRequested();
				var txt = last = Uia.TextOf(Window(a_ctx, a_image, TimeSpan.FromMinutes(5)));
				if (txt.Contains("Building complete", StringComparison.Ordinal)) {
					a_ctx.Log.Detail("Synthesis: building complete");
					return;
				}
				var preparing = txt.Contains("Preparing", StringComparison.Ordinal);
				var blocked = !preparing && Regex.IsMatch(txt, "Blocking Error", RegexOptions.IgnoreCase);
				if (StillBlocked(ref blockedSince, blocked, DateTime.UtcNow) || (!preparing && Regex.IsMatch(txt, "failed", RegexOptions.IgnoreCase))) {
					throw new StepFailedException("Synthesis could not build its patchers: " + (txt.Length > 300 ? txt[^300..] : txt));
				}
				a_ctx.Pause(TimeSpan.FromSeconds(3));
			}
			throw new StepFailedException($"Synthesis did not finish building within {a_s.Int("build_min", 15)} minutes. Its window shows: {(last.Length > 300 ? last[^300..] : last)}");
		} finally {
			StepKit.CloseTool(a_ctx, a_image);
		}
	}
}

// Pandora Behaviour Engine+ through MO2 with --auto_run --auto_close. Its command line's output option was never read (4.4,
// read in its source): the output folder is games.SkyrimSE.outputPath in the Settings.json MO2 shows it (the
// overwrite copy wins), so that file points at an empty folder for the run and goes back byte for byte after.
public sealed class PandoraStep : IStep
{
	public string Key => "pandora";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		foreach (var i in StepKit.CheckExecutable(a_c, Key, a_s.Str("executable"))) yield return i;
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		if (a_s.Str("output_dir") is not { Length: > 0 }) yield return new Issue(Key, true, "no output folder is set");
		foreach (var i in StepKit.CheckOutputDir(a_c, Key, a_s.Str("output_dir"))) yield return i;
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var outDir = a_s.Req("output_dir");
		var image = a_s.Str("image") ?? "Pandora Behaviour Engine+.exe";
		StepKit.ParkDir(a_ctx, outDir, "Pandora output");
		var t0 = DateTime.UtcNow;
		var settings = Path.Combine(a_ctx.Mo2.OverwriteDir, "Settings.json");
		var before = File.Exists(settings) ? Files.ReadShared(settings) : null;
		JsonObject conf;
		try {
			conf = before == null ? [] : JsonNode.Parse(Files.DecodeText(before), documentOptions: JsonRead.Plain) as JsonObject ?? [];
		} catch (JsonException) {
			throw new StepFailedException("Pandora's Settings.json in Mod Organizer's overwrite folder does not read - fix or move it first");
		}
		var games = conf["games"] as JsonObject ?? (JsonObject)(conf["games"] = new JsonObject());
		var game = games["SkyrimSE"] as JsonObject ?? (JsonObject)(games["SkyrimSE"] = new JsonObject());
		game["gameDataPath"] ??= Path.Combine(a_ctx.Mo2.GameDir, "Data");
		game["outputPath"] = Path.GetFullPath(outDir);
		var stray = Path.Combine(a_ctx.Mo2.OverwriteDir, "Engine.log");
		StepKit.KeepFile(a_ctx, settings);
		try {
			Files.WriteAtomic(settings, conf.ToJsonString(JsonWrite.Indented));
			using (new Toggle(a_ctx, [.. a_s.List("enable_during"), .. StepKit.ToolMods(a_ctx, a_s)], a_s.List("disable_during"), "Pandora", a_ctx.Cancel)) {
				try {
					using var proc = a_ctx.Control.RunExecutable(a_s.Req("executable"), image, a_s.Str("args") ?? "--auto_run --auto_close", a_ctx.Cancel);
					a_ctx.Activity(Key, "Pandora is building behaviour files");
					Waits.Tool(a_ctx, Key, proc, a_s.Deadline(20), () => {
						foreach (var box in Uia.Boxes(StepKit.PidsOf(image)())) {
							a_ctx.Log.Detail("Pandora says: " + box.Text);
							if (Regex.IsMatch(box.Text, "error|failed|exception", RegexOptions.IgnoreCase)) {
								throw new StepFailedException("Pandora: " + box.Text);
							}
							if (Uia.Button(box.Element, "OK", "Close") is { } b) {
								Uia.Press(b);
							}
						}
						return proc.HasExited ? Waits.Poll.Done : Waits.Poll.Wait;
					});
				} finally {
					StepKit.CloseTool(a_ctx, image);
				}
			}
		} finally {
			StepKit.PutFilesBack(a_ctx, settings);
		}
		if (File.Exists(stray) && File.GetLastWriteTimeUtc(stray) >= t0) {
			throw new StepFailedException("Pandora wrote into Mod Organizer's overwrite folder instead of its output folder - its settings' output path was not used; its files there are this run's");
		}
		var log = Path.Combine(outDir, "Engine.log");
		if (!File.Exists(log) || File.GetLastWriteTimeUtc(log) < t0) {
			throw new StepFailedException("Pandora wrote no new Engine.log - it did not run, or it did not read its output path");
		}
		var lines = Files.ReadSharedText(log).Split('\n');
		var errs = lines.Where(l => Regex.IsMatch(l, @"^(ERROR|FATAL)\b")).ToList();
		a_ctx.Note($"Pandora: {lines.Count(l => l.StartsWith("WARN", StringComparison.Ordinal))} warning(s), {errs.Count} error(s)");
		if (errs.Count > 0) {
			throw new StepFailedException("Pandora logged errors: " + string.Join(" | ", errs.Take(3)));
		}
		var mod = a_s.OutputMod!;
		var keepSettings = Path.Combine(a_ctx.Mo2.ModDir(mod), "Settings.json");
		var own = File.Exists(keepSettings) ? File.ReadAllBytes(keepSettings) : null;
		StepKit.IntoMod(a_ctx, Key, outDir, mod, 3);
		if (own != null && !File.Exists(keepSettings)) {
			Files.WriteAtomic(keepSettings, own);
		}
		a_ctx.SetAfter(mod);
	}
}
