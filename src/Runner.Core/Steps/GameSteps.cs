using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner.Steps;

public sealed class CleanSaveStep : IStep
{
	public string Key => "cleansave";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (!DevBench.InProfile(a_c.Profile)) {
			yield return new Issue(Key, false, "DevBench is not in this profile - the clean save will ask you to do the in-game part by hand");
		}
		if (a_s.Str("save") is not { Length: > 0 } && InGame.NewestSave(a_c.Profile) == null) {
			yield return new Issue(Key, true, "this profile has no save to start the clean save from");
		}
	}

	// phase 1 runs before the outputs go off (Runner); this is phase 2
	public void Run(RunContext a_ctx, StepSettings a_s) => InGame.CleanSaves(a_ctx, a_s);
}

public sealed class PlaySaveStep : IStep
{
	public string Key => "playsave";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (!DevBench.InProfile(a_c.Profile)) {
			yield return new Issue(Key, false, "DevBench is not in this profile - the save to play will ask you to load and save by hand");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s) => InGame.PlaySave(a_ctx, a_s);
}

// The grass precache NGIO and DynDOLOD read. FasterNGIO builds it offline in seconds (inside MO2 through a cmd wrapper - MO2
// cannot start it straight: it ends in 2 s with code -1); the old route is the game itself with NGIO's PrecacheGrass.txt.
public sealed class GrassStep : IStep
{
	public string Key => "grass";

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		if (a_s.OutputMod == null) yield return new Issue(Key, true, "no output mod is set");
		if ((a_s.Str("by") ?? "fasterngio") == "fasterngio") {
			if (a_s.Str("fasterngio_exe") is not { Length: > 0 } exe || !File.Exists(exe)) {
				yield return new Issue(Key, true, "FasterNGIO's program is not found (set \"fasterngio_exe\")");
			} else if (WrapperRefuses(exe, a_s.Str("fasterngio_args")) is { } refused) {
				yield return new Issue(Key, true, refused);
			}
		} else if (Setup.Bool(a_c.Setup.Root, "gameFolderWrites") != true) {
			yield return new Issue(Key, true, "the game route needs NGIO's PrecacheGrass.txt in the game folder, and this setup does not allow writing there");
		}
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		if ((a_s.Str("by") ?? "fasterngio") == "fasterngio") {
			FasterNgio(a_ctx, a_s);
		} else {
			ByGame(a_ctx, a_s);
		}
	}

	static string OverwriteGrass(RunContext a_ctx) => Path.Combine(a_ctx.Mo2.OverwriteDir, "grass");

	// FasterNGIO's wrapper, written as UTF-8: its second line makes cmd read the rest that way, so a path with an accent or a
	// non-Latin letter reaches FasterNGIO as it is (written as plain ASCII such a letter became "?", cmd found no program and
	// the step failed with "exit code 1" and no cause)
	// The exit code is kept first and a line break written before it: FasterNGIO's last line may end without one (a progress
	// line), and the wrapper's own line glued to it would never be read as the end
	// The program and its arguments go into the wrapper's command line as they are: a character there that cmd reads as a
	// command of its own would run something else than FasterNGIO. Refused, never escaped - a setup file can come from anyone
	public static string? WrapperRefuses(string a_exe, string? a_args) =>
		a_exe.IndexOfAny(['&', '|', '<', '>', '^', '"', '\r', '\n']) >= 0
			? "FasterNGIO's program path holds a character that the command line it runs through would read as a command of its own (& | < > ^ \" or a line break) - move FasterNGIO to a folder whose path has none"
			: (a_args ?? "").IndexOfAny(['&', '|', '<', '>', '^', '\r', '\n']) >= 0
				? "FasterNGIO's arguments hold a character that the command line it runs through would read as a command of its own (& | < > ^ or a line break) - take it out of the step's \"fasterngio_args\""
				: null;

	public static string WrapperText(string a_exe, string a_args, string a_outFile) =>
		$"@echo off\r\nchcp 65001 >nul\r\necho started %DATE% %TIME% > \"{a_outFile}\"\r\n\"{a_exe.Replace("%", "%%", StringComparison.Ordinal)}\" {a_args} >> \"{a_outFile}\" 2>&1\r\n" +
		$"set TRCODE=%ERRORLEVEL%\r\necho.>> \"{a_outFile}\"\r\necho exit code %TRCODE% >> \"{a_outFile}\"\r\n";

	// the exit code the wrapper wrote as its own LAST line (null: it has not ended). The words "exit code" in a line of
	// FasterNGIO's own - it reports its child processes - are not the end: read as the end, they closed a FasterNGIO still at work
	public static string? WrapperExit(string a_output) =>
		Regex.Match(a_output, @"^exit code (-?\d+)\s*\z", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value : null;

	void FasterNgio(RunContext a_ctx, StepSettings a_s)
	{
		var exe = a_s.Req("fasterngio_exe");
		var mod = a_s.OutputMod!;
		var ow = OverwriteGrass(a_ctx);
		if (Directory.Exists(ow) && Directory.EnumerateFileSystemEntries(ow).Any()) {
			StepKit.ParkDir(a_ctx, ow, "overwrite grass (before)", Key);
		}
		// no space and no quote in the wrapper's path: MO2 passes it through cmd /c (the user's temp folder holds the user name,
		// which often has a space - ProgramData does not)
		var work = Path.Combine(Brand.WorkRoot, "fasterngio");
		if (work.IndexOfAny([' ', '"', '&', '^']) >= 0) {
			throw new StepFailedException("FasterNGIO's wrapper needs a folder path without spaces - this PC's ProgramData has one: " + work);
		}
		Directory.CreateDirectory(work);
		var outFile = Path.Combine(work, "output.txt");
		Files.TryDelete(outFile);
		var cmd = Path.Combine(work, "run.cmd");
		if (WrapperRefuses(exe, a_s.Str("fasterngio_args")) is { } refused) {
			throw new StepFailedException(refused);
		}
		var args = (a_s.Str("fasterngio_args") ?? "--world all").Replace("%", "%%", StringComparison.Ordinal);
		Files.WriteAtomic(cmd, WrapperText(exe, args, outFile), new System.Text.UTF8Encoding(false));
		var image = Path.GetFileName(exe);
		using (new Toggle(a_ctx, [], [mod], "the grass cache", a_ctx.Cancel)) {
			var started = a_ctx.Control.RunInside(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c " + cmd, image, a_ctx.Cancel);
			a_ctx.Activity(Key, "FasterNGIO is building the grass cache");
			var sw = Stopwatch.StartNew();
			var last = Stopwatch.StartNew();
			var size = -1L;
			string body;
			try {
				while (true) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					a_ctx.Pause(TimeSpan.FromSeconds(2));
					body = File.Exists(outFile) ? Files.ReadSharedText(outFile) : "";
					if (WrapperExit(body) != null) {
						break;
					}
					// the wrapper's first line writes the file: with no FasterNGIO seen and no file, Mod Organizer never started
					// it - said now, not as "wrote nothing for 10 minutes"
					if (started == null && !File.Exists(outFile) && sw.Elapsed > TimeSpan.FromSeconds(20)) {
						throw new StepFailedException("Mod Organizer did not start FasterNGIO (its wrapper never ran) - look at Mod Organizer for a box it shows, then run again");
					}
					if (body.Length != size) {
						size = body.Length;
						last.Restart();
						var lastLine = body.TrimEnd().Split('\n').LastOrDefault()?.Trim() ?? "";
						if (lastLine != "") {
							a_ctx.Activity(Key, lastLine.Length > 140 ? lastLine[..140] : lastLine);
						}
					}
					if (last.Elapsed > TimeSpan.FromMinutes(Math.Max(a_ctx.Setup.IdleMinutes, a_s.Int("stall_min", 10)))) {
						throw new StepFailedException($"FasterNGIO wrote nothing for {last.Elapsed.TotalMinutes:0} minutes");
					}
					if (sw.Elapsed > a_s.Deadline(30)) {
						throw new StepFailedException($"FasterNGIO passed its {a_s.Deadline(30).TotalMinutes:0} minute limit");
					}
				}
			} finally {
				// a stop or a stall: the run's own FasterNGIO ended by its PID
				StepKit.CloseTool(a_ctx, image);
			}
			var lines = body.TrimEnd().Split('\n');
			foreach (var ln in lines.Where(l => l.Contains("warning", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(8).Concat(lines.TakeLast(3))) {
				a_ctx.Log.Detail("| " + ln.TrimEnd());
			}
			var code = WrapperExit(body);
			if (code != "0") {
				throw new StepFailedException($"FasterNGIO ended with exit code {code}");
			}
			var n = Files.CountFiles(ow);
			a_ctx.Log.Detail($"FasterNGIO done in {sw.Elapsed.TotalSeconds:0} s: {n} grass file(s)");
			if (n < a_s.Int("min_files", 100)) {
				throw new StepFailedException($"the grass cache looks empty ({n} files)");
			}
			StepKit.ParkMod(a_ctx, Key, mod);
			Files.MoveContents(ow, Path.Combine(a_ctx.Mo2.ModDir(mod), "grass"));
			Files.DeleteDir(ow);
		}
		a_ctx.SetAfter(mod);
	}

	// the old route: NGIO's PrecacheGrass.txt in the game folder, the game started until the cache is complete; a crash is resumed
	// by starting again. His numbers (2026-09-30): a first start that dies within 5 minutes is a broken setup; after that short
	// crashes are normal, but more than 15 restarts, or 3 in a row that add no grass file, stop the step.
	void ByGame(RunContext a_ctx, StepSettings a_s)
	{
		var mod = a_s.OutputMod!;
		var off = a_ctx.Profile.Dependents([mod]).Keys.Concat(a_s.List("disable_during")).Append(mod).ToList();
		var flag = Path.Combine(a_ctx.Mo2.GameDir, "PrecacheGrass.txt");
		var ow = OverwriteGrass(a_ctx);
		if (Directory.Exists(ow) && Directory.EnumerateFileSystemEntries(ow).Any()) {
			StepKit.ParkDir(a_ctx, ow, "overwrite grass (before)", Key);
		}
		using (new Toggle(a_ctx, [], off, "the grass cache", a_ctx.Cancel)) {
			// the flag in the game folder is in the run record first: a crash leaves no game that precaches at every start
			StepKit.KeepFile(a_ctx, flag);
			File.WriteAllText(flag, "");
			var launches = 0;
			var idle = 0;
			var offers = 0;
			try {
				while (File.Exists(flag)) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					// (how many starts are too many is one setting, max_restarts, judged after each start that crashed)
					++launches;
					var have = Files.CountFiles(ow);
					a_ctx.Activity(Key, $"the game builds the grass cache - start {launches} ({have} files so far)");
					InGame.NoGameRunning(a_ctx);
					var sw = Stopwatch.StartNew();
					var launched = DateTime.Now;
					a_ctx.GameLaunched = launched;
					GameStart.Prepare(a_ctx);
					using var g = Mo2Control.Open(a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", InGame.FreshGamePid(), a_ctx.Cancel), "the game");
					var lastN = -1;
					var still = Stopwatch.StartNew();
					while (!g.HasExited) {
						a_ctx.Cancel.ThrowIfCancellationRequested();
						var n = Files.CountFiles(ow);
						if (n != lastN) {
							lastN = n;
							still.Restart();
						}
						if (!File.Exists(flag) && still.Elapsed > TimeSpan.FromMinutes(1)) {
							Procs.End(g);
							break;
						}
						if (File.Exists(flag) && still.Elapsed > TimeSpan.FromMinutes(20)) {
							a_ctx.Log.Detail("no new grass file for 20 minutes - the game is stuck, starting it again");
							Procs.End(g);
							break;
						}
						a_ctx.Pause(TimeSpan.FromSeconds(10));
					}
					if (a_ctx.BlockedBy is { } box) {
						throw new StepFailedException(box);
					}
					// Faster Archives closed the game for its offer: no crash, and not one of the cache's own starts
					if (File.Exists(flag) && FasterArchives.Offered(InGame.LogFolders(a_ctx.Setup, a_ctx.Mo2), launched, a_ctx.Control.Started)) {
						FasterArchives.Wait(a_ctx, Key, a_s, ++offers, launched);
						--launches;
						continue;
					}
					if (File.Exists(flag)) {
						if (launches == 1 && sw.Elapsed < TimeSpan.FromMinutes(a_s.Int("first_start_min", 5))) {
							throw new StepFailedException($"the first start crashed after {sw.Elapsed.TotalMinutes:0.0} minutes - a broken setup, not a normal grass-cache crash" +
								InGame.CrashNote(InGame.LogFolders(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, a_ctx.GameLaunched ?? a_ctx.Journal.Started));
						}
						idle = Files.CountFiles(ow) <= have ? idle + 1 : 0;
						if (launches - 1 >= a_s.Int("max_restarts", 15)) {
							throw new StepFailedException($"the game crashed {launches} times - more than is normal");
						}
						if (idle >= a_s.Int("max_idle_restarts", 3)) {
							throw new StepFailedException($"{idle} restarts in a row built no new grass file - the cache is stuck");
						}
					}
					a_ctx.Pause(TimeSpan.FromSeconds(5));
				}
			} finally {
				StepKit.PutFilesBack(a_ctx, flag);
				InGame.CloseGame(a_ctx);
			}
			var total = Files.CountFiles(ow);
			if (total < a_s.Int("min_files", 100)) {
				throw new StepFailedException($"the grass cache looks empty ({total} files)");
			}
			StepKit.ParkMod(a_ctx, Key, mod);
			Files.MoveContents(ow, Path.Combine(a_ctx.Mo2.ModDir(mod), "grass"));
			Files.DeleteDir(ow);
		}
		a_ctx.SetAfter(mod);
	}
}

// The shader caches: the game sits at its main menu while the shader mod compiles every shader into Data\ShaderCache (=
// overwrite), then the cache moves into that shader mod's own cache mod. A setup lists `caches` (one per shader mod: its
// mod, its output mod, an optional settings mod). The cache the profile itself runs is built first, with every ticked shader
// mod on as the game loads them; a shader mod that is switched off gets one only when the setup asks (Plan). A cache is rebuilt
// only when what it is built from changed since it was made (Fingerprint: the shader mod, the shader files, the features
// switched off at the game's start, the game's version) - the setup's "shaderRebuild" (his "configurable").
public sealed class ShaderStep : IStep
{
	public string Key => "shaders";

	public sealed record Cache(string ShaderMod, string OutputMod, string? SettingsMod);

	internal static List<Cache> Caches(StepSettings a_s)
	{
		var list = (a_s.Raw["caches"] as JsonArray)?.OfType<JsonObject>()
			.Select(o => new Cache(Setup.Str(o, "shader_mod") ?? "", Setup.Str(o, "output_mod") ?? "", Setup.Str(o, "settings_mod"))).Where(c => c.ShaderMod != "" && c.OutputMod != "").ToList() ?? [];
		if (list.Count == 0 && a_s.Str("shader_mod") is { } sm && a_s.OutputMod is { } om) {
			list.Add(new Cache(sm, om, a_s.Str("settings_mod")));
		}
		return list;
	}

	public IEnumerable<Issue> Check(CheckContext a_c, StepSettings a_s)
	{
		var caches = Caches(a_s);
		if (caches.Count == 0) {
			yield return new Issue(Key, true, "no shader cache is set (each needs its shader mod and its output mod)");
		}
		if (caches.Count > 0 && caches.All(c => !a_c.States.ContainsKey(c.ShaderMod))) {
			yield return new Issue(Key, true, "none of the shader mods is in this profile");
		} else if (caches.Count > 0 && !a_s.Bool("build_for_off", false)) {
			// said before every run, not only in its log: a setup made when every listed cache was built would lose one silently
			var off = caches.Where(c => a_c.States.TryGetValue(c.ShaderMod, out var f) && f == '-').Select(c => c.ShaderMod).ToList();
			if (off.Count == caches.Count(c => a_c.States.ContainsKey(c.ShaderMod))) {
				yield return new Issue(Key, false, "no shader mod is ticked in this profile - this step will build nothing (a cache is built for the shader mod the profile runs)");
			} else if (off.Count > 0) {
				yield return new Issue(Key, false, $"{string.Join(", ", off)}: switched off in this profile, so its cache is not built - to build it too (you switch between shader mods), " +
					"set \"Also build the cache of a shader mod that is switched off\" on this step");
			}
		}
		if (a_s.Str("on_crash") is { Length: > 0 } how && how.Trim().ToLowerInvariant() is not ("ask" or "restart" or "stop")) {
			yield return new Issue(Key, true, $"\"on_crash\" is \"{how}\" - it is \"ask\", \"restart\" or \"stop\"");
		}
	}

	// what the cache depends on: the shader mod's DLL(s), its version and its settings files - hashed
	// what the cache is built from: the shader mod (its DLLs, meta.ini, settings), its settings mod and - with the profile - every
	// shader file a ticked mod ships (a Community Shaders feature can be its own mod) and the game's version. Nothing a generator
	// writes (plugins, meshes, textures, LOD, grass, animations) is in it: those never make the cache stale (his question
	// 2026-10-27, "can you confirm rebuilding another tool wont cause shaders to need to be rebuilt")
	public static readonly string[] ShaderFileKinds = [".hlsl", ".hlsli", ".ini", ".json", ".fx", ".fxh", ".csh", ".vsh", ".psh"];

	// the part of a Community Shaders settings file that names the features left out when the game starts: a feature switched
	// there changes which shaders exist at all
	public const string BootKey = "Disable at Boot";

	// a_otherCaches: the mods of the setup's OTHER caches (shader, output and settings mod) - left out, so switching the tick
	// between two shader mods or updating one never makes the other's cache stale (the fourth review, M1)
	// a_layers: the other ticked shader mods this cache is built together with - part of it, hashed with it (none = the
	// fingerprint a cache had before layers were known, so an older stamp still reads)
	// a_winners: false = the fingerprint as it was before it knew which mod wins a shader file two mods ship (an older stamp)
	// a_bootOnly: false = the fingerprint as it was while a settings file counted with every value in it (an older stamp)
	public static string Fingerprint(Mo2Instance a_mo2, Cache a_c, Profile? a_profile = null, IReadOnlyCollection<string>? a_otherCaches = null,
		IReadOnlyCollection<string>? a_layers = null, bool a_winners = true, bool a_bootOnly = true)
	{
		using var sha = SHA256.Create();
		void Add(string a_file)
		{
			if (!File.Exists(a_file)) {
				return;
			}
			var name = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(a_file).ToLowerInvariant());
			sha.TransformBlock(name, 0, name.Length, null, 0);
			var data = File.ReadAllBytes(a_file);
			sha.TransformBlock(data, 0, data.Length, null, 0);
		}
		// A settings file counts by the one part of it that says what is built: the features it switches off at the game's
		// start (BootKey). Its other values are not part of the cache, and the game rewrites them by itself: in a real run
		// (2026-11-21) it changed three preset fields at a start, the file no longer matched, and the next run built the whole
		// cache again - twenty minutes for nothing, at every run. What the shader mod itself decides to compile again at a
		// start, the run waits for before it loads a save (InGame.WaitCompile) and says in its notes.
		void AddSettings(string a_file)
		{
			if (!a_bootOnly) {
				Add(a_file);
				return;
			}
			if (!File.Exists(a_file)) {
				return;
			}
			string boot;
			try {
				// (a file saved with a byte order mark reads all the same)
				var text = Files.ReadShared(a_file).AsSpan();
				if (text.StartsWith<byte>([0xEF, 0xBB, 0xBF])) {
					text = text[3..];
				}
				boot = JsonNode.Parse(text, null, new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })
					is JsonObject o && o[BootKey] is JsonObject off
					? string.Join("|", off.Select(kv => kv.Key + "=" + (kv.Value?.ToJsonString() ?? "null")).Order(StringComparer.Ordinal))
					: "";
			} catch (Exception e) when (e is System.Text.Json.JsonException or ArgumentException) {
				// a file that does not read as settings (no JSON, or a name twice in one object) counts whole, as before
				Add(a_file);
				return;
			}
			var line = System.Text.Encoding.UTF8.GetBytes(Path.GetFileName(a_file).ToLowerInvariant() + "|boot|" + boot);
			sha.TransformBlock(line, 0, line.Length, null, 0);
		}
		// each shader file by the mods that ship it: where two do, which one wins is part of what the cache is built from
		var shippedBy = new Dictionary<string, List<string>>(StringComparer.Ordinal);
		void AddShaders(string a_mod)
		{
			var shaders = Path.Combine(a_mo2.ModDir(a_mod), "Shaders");
			if (!Directory.Exists(shaders)) {
				return;
			}
			foreach (var f in Directory.EnumerateFiles(shaders, "*", SearchOption.AllDirectories)
				         .Where(f => ShaderFileKinds.Contains(Path.GetExtension(f).ToLowerInvariant())).Order(StringComparer.OrdinalIgnoreCase)) {
				var file = Path.GetRelativePath(shaders, f).ToLowerInvariant();
				if (!shippedBy.TryGetValue(file, out var by)) {
					shippedBy[file] = by = [];
				}
				if (!by.Contains(a_mod, StringComparer.OrdinalIgnoreCase)) {
					by.Add(a_mod);
				}
				var rel = System.Text.Encoding.UTF8.GetBytes(a_mod + "|" + Path.GetRelativePath(shaders, f).ToLowerInvariant());
				sha.TransformBlock(rel, 0, rel.Length, null, 0);
				var data = File.ReadAllBytes(f);
				sha.TransformBlock(data, 0, data.Length, null, 0);
			}
		}
		var dir = a_mo2.ModDir(a_c.ShaderMod);
		if (Directory.Exists(dir)) {
			foreach (var f in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				Add(f);
			}
			Add(Path.Combine(dir, "meta.ini"));
			foreach (var f in Directory.EnumerateFiles(dir, "Settings*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				AddSettings(f);
			}
		}
		if (a_c.SettingsMod != null && Directory.Exists(a_mo2.ModDir(a_c.SettingsMod))) {
			foreach (var f in Directory.EnumerateFiles(a_mo2.ModDir(a_c.SettingsMod), "Settings*.json", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) {
				AddSettings(f);
			}
		}
		var layers = (a_layers ?? []).Where(l => !l.Equals(a_c.ShaderMod, StringComparison.OrdinalIgnoreCase) && !l.Equals(a_c.SettingsMod, StringComparison.OrdinalIgnoreCase))
			.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
		foreach (var layer in layers.Where(l => Directory.Exists(a_mo2.ModDir(l)))) {
			var name = System.Text.Encoding.UTF8.GetBytes("layer " + layer.ToLowerInvariant());
			sha.TransformBlock(name, 0, name.Length, null, 0);
			foreach (var f in Directory.EnumerateFiles(a_mo2.ModDir(layer), "*.dll", SearchOption.AllDirectories)
				         .Concat(Directory.EnumerateFiles(a_mo2.ModDir(layer), "Settings*.json", SearchOption.AllDirectories)).Order(StringComparer.OrdinalIgnoreCase)) {
				if (f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) {
					AddSettings(f);
				} else {
					Add(f);
				}
			}
			Add(Path.Combine(a_mo2.ModDir(layer), "meta.ini"));
			AddShaders(layer);
		}
		if (a_profile != null) {
			// the cache's own shader mod and settings mod count ticked or not: the compile ticks both, whatever the player has ticked
			AddShaders(a_c.ShaderMod);
			if (a_c.SettingsMod != null) {
				AddShaders(a_c.SettingsMod);
			}
			var skip = new HashSet<string>(a_otherCaches ?? [], StringComparer.OrdinalIgnoreCase) { a_c.ShaderMod, a_c.OutputMod };
			if (a_c.SettingsMod != null) {
				skip.Add(a_c.SettingsMod);
			}
			skip.UnionWith(layers);
			foreach (var mod in a_profile.States().Where(kv => kv.Value == '+' && !skip.Contains(kv.Key))
				         .Select(kv => kv.Key).Order(StringComparer.OrdinalIgnoreCase)) {
				AddShaders(mod);
			}
			// Two mods that ship the same shader file: the game reads the winner's, so a change of the mod order that changes
			// the winner changes what the cache is built from. Only such files are added - an order that changes no winner
			// leaves the fingerprint as it was, and so does a profile where no two mods share a shader file
			var shared = shippedBy.Where(s => s.Value.Count > 1).OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
			if (a_winners && shared.Count > 0) {
				var place = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
				foreach (var m in a_profile.ModOrder()) {
					place.TryAdd(m, place.Count);
				}
				foreach (var (file, by) in shared) {
					var winner = by.OrderBy(m => place.GetValueOrDefault(m, int.MaxValue)).ThenBy(m => m, StringComparer.OrdinalIgnoreCase).First();
					var line = System.Text.Encoding.UTF8.GetBytes("wins|" + file + "|" + winner.ToLowerInvariant());
					sha.TransformBlock(line, 0, line.Length, null, 0);
				}
			}
			var exe = Path.Combine(a_mo2.GameDir, "SkyrimSE.exe");
			if (File.Exists(exe)) {
				var v = System.Text.Encoding.UTF8.GetBytes("game " + System.Diagnostics.FileVersionInfo.GetVersionInfo(exe).FileVersion);
				sha.TransformBlock(v, 0, v.Length, null, 0);
			}
		}
		sha.TransformFinalBlock([], 0, 0);
		return Convert.ToHexString(sha.Hash!);
	}

	static string StampFile(Mo2Instance a_mo2, Cache a_c, string a_brand = Brand.Name) => Path.Combine(a_mo2.ModDir(a_c.OutputMod), a_brand + " cache.json");

	// the newest stamp under any name the program had (an older copy may have rebuilt the cache after this one did)
	static string ReadStampFile(Mo2Instance a_mo2, Cache a_c) =>
		Brand.AllNames.Select(b => StampFile(a_mo2, a_c, b)).Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc)
			.FirstOrDefault() ?? StampFile(a_mo2, a_c);

	public static bool UpToDate(Mo2Instance a_mo2, Cache a_c, Profile? a_profile = null, IReadOnlyCollection<string>? a_otherCaches = null,
		IReadOnlyCollection<string>? a_layers = null) => WhyStale(a_mo2, a_c, a_profile, a_otherCaches, a_layers) == null;

	// why the cache is built again (null: it is current) - said in the run's log, so a rebuild is never a riddle
	public static string? WhyStale(Mo2Instance a_mo2, Cache a_c, Profile? a_profile = null, IReadOnlyCollection<string>? a_otherCaches = null,
		IReadOnlyCollection<string>? a_layers = null)
	{
		var stamp = ReadStampFile(a_mo2, a_c);
		if (!File.Exists(stamp)) {
			return "it has no record of a build by this program";
		}
		try {
			var o = JsonNode.Parse(Files.ReadSharedText(stamp)) as JsonObject;
			// The cache must still hold what it held when it was stamped (nearly all of it), judged by its own count. Judged by a
			// fixed 50 files, a small shader mod's cache that the step had accepted with fewer read as out of date at every run
			// and was built again each time. A stamp from before the count was kept is held to the old 50
			var stamped = o?["files"] is JsonValue fv && fv.TryGetValue<int>(out var count) ? count : 50;
			var holds = Files.CountFiles(Path.Combine(a_mo2.ModDir(a_c.OutputMod), "ShaderCache"));
			if (holds < Math.Max(1, stamped * 9 / 10)) {
				return $"it holds {holds} shader(s), and held {stamped} when it was built";
			}
			// a cache that ended by quiet (the log never said it was done) is built again
			if (o?["verified"]?.GetValue<bool>() == false) {
				return "its last build ended without the shader mod saying it was done";
			}
			// A stamp from before the fingerprint knew which mod wins a shared shader file ("winners"), or from while a settings
			// file counted with every value in it (no "settings"), is read by the fingerprint it was written with: the cache it
			// stands for is still that cache, and reading it the new way would have built every such cache again for nothing.
			// The next build stamps it the new way
			var winners = o?["winners"] is JsonValue wv && wv.TryGetValue<bool>(out var w) && w;
			var bootOnly = o?["settings"]?.ToString() == StampSettings;
			return o?["fingerprint"]?.ToString() == Fingerprint(a_mo2, a_c, a_profile, a_otherCaches, a_layers, winners, bootOnly) ? null :
				"what it is built from changed since its build: the shader mod or its version, a shader file of a ticked mod, " +
				(bootOnly ? "the features switched off at the game's start" : "a settings file of the shader mod") + ", or the game's version";
		} catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException) {
			return "its record of the last build does not read";
		}
	}

	// how a stamp says its fingerprint takes a settings file by its boot part only
	const string StampSettings = "boot";

	// one cache to build: its switches for the compile, the other ticked shader mods it is built together with (Layers), and
	// whether it is the cache the profile itself runs
	public sealed record Build(Cache Cache, List<string> On, List<string> Off, List<string> Layers, bool Runs);

	// What a run builds, in order. The profile's own shader setup first: every TICKED shader mod together, as the game loads
	// them, into the cache of the one that wins (the first in modlist.txt = the lowest in Mod Organizer's left pane). A shader
	// mod that is switched off gets its cache only when the setup asks ("build_for_off"), after that one, alone. (His run of
	// 2026-11-10 built the cache of a shader mod the profile does not run first, then tried a ticked layer without the shader
	// mod under it.)
	public static (List<Build> Builds, List<string> Notes) Plan(IReadOnlyList<Cache> a_all, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<string> a_order,
		bool a_buildForOff, IReadOnlyList<string> a_enable, IReadOnlyList<string> a_disable)
	{
		var builds = new List<Build>();
		var notes = new List<string>();
		var have = a_all.Where(c => a_states.ContainsKey(c.ShaderMod)).ToList();
		foreach (var c in a_all.Except(have)) {
			notes.Add($"({c.ShaderMod} is not in this profile - its cache is not built)");
		}
		int Place(Cache a_c)
		{
			var i = a_order.ToList().FindIndex(m => m.Equals(a_c.ShaderMod, StringComparison.OrdinalIgnoreCase));
			return i < 0 ? int.MaxValue : i;
		}
		static IEnumerable<string> ModsOf(IEnumerable<Cache> a_caches) => a_caches.SelectMany(c => new[] { c.ShaderMod, c.SettingsMod }).OfType<string>();
		var outputs = have.Select(c => c.OutputMod).ToList();
		var ticked = have.Where(c => a_states[c.ShaderMod] == '+').OrderBy(Place).ToList();
		var off = have.Except(ticked).ToList();
		if (ticked.Count > 0) {
			var winner = ticked[0];
			var layers = ModsOf(ticked.Skip(1)).ToList();
			if (ticked.Count > 1) {
				notes.Add($"{string.Join(" and ", ticked.Select(c => c.ShaderMod))} are ticked together: one cache for them as the profile runs them ({winner.ShaderMod} wins) -> {winner.OutputMod}");
			}
			builds.Add(new Build(winner, [.. ModsOf(ticked), .. a_enable], [.. ModsOf(off), .. outputs, .. a_disable], layers, true));
		} else if (have.Count > 0) {
			notes.Add("no shader mod is ticked in this profile - the profile itself needs no cache");
		}
		foreach (var c in off) {
			if (a_buildForOff) {
				builds.Add(new Build(c, [.. ModsOf([c]), .. a_enable], [.. ModsOf(have.Where(o => o != c)), .. outputs, .. a_disable], [], false));
			} else {
				notes.Add($"({c.ShaderMod} is switched off in this profile - its cache is not built; a setup that wants it too sets \"build_for_off\")");
			}
		}
		return (builds, notes);
	}

	public void Run(RunContext a_ctx, StepSettings a_s)
	{
		var st = a_ctx.Profile.States();
		var all = Caches(a_s);
		var (builds, notes) = Plan(all, st, a_ctx.Profile.ModOrder(), a_s.Bool("build_for_off", false), a_s.List("enable_during"), a_s.List("disable_during"));
		foreach (var n in notes) {
			a_ctx.Log.Detail(n);
		}
		var mode = a_ctx.Setup.ShaderRebuild;
		var built = 0;
		foreach (var b in builds) {
			// a Stop pressed while the last cache was put back never starts the next one
			a_ctx.Cancel.ThrowIfCancellationRequested();
			var c = b.Cache;
			if (mode == ShaderRebuild.Never) {
				a_ctx.Log.Detail($"the {c.ShaderMod} cache: not rebuilt (setup: never)");
				continue;
			}
			// the layers are part of this cache (hashed with it); every other cache's mods are left out of its fingerprint
			var theirs = OtherCacheMods(all, c).Except(b.Layers, StringComparer.OrdinalIgnoreCase).ToList();
			if (mode == ShaderRebuild.WhenChanged) {
				if (WhyStale(a_ctx.Mo2, c, a_ctx.Profile, theirs, b.Layers) is not { } why) {
					// left exactly as the player has it: a cache this run did not build is never recorded as this run's output
					a_ctx.Log.Detail($"the {c.ShaderMod} cache is current (nothing it depends on changed) - not rebuilt");
					continue;
				}
				a_ctx.Log.Say($"the {c.ShaderMod} cache is built again: {why}");
			}
			StepKit.EnsureOutput(a_ctx, c.OutputMod);
			if (c.SettingsMod != null) {
				StepKit.EnsureOutput(a_ctx, c.SettingsMod);
			}
			a_ctx.Log.Detail($"the {c.ShaderMod} cache -> {c.OutputMod}" + (b.Runs ? " (the one this profile runs)" : " (switched off in this profile; the setup asks for it too)"));
			Compile(a_ctx, a_s, b, theirs);
			if (b.Runs) {
				Keep(a_ctx, all, c, st);
			}
			++built;
		}
		if (built == 0) {
			a_ctx.Note(builds.Count == 0 ? "shader caches: nothing to build for this profile" : "shader caches: every cache was current");
		}
	}

	// the cache the profile runs stays on from here whatever comes later in the step (a finished part is never lost to a later
	// failure), and the cache mod of every other shader mod of the setup goes off: two cache mods ticked at once hand the game
	// one cache's files for the other's shader mod, and it throws the cache away
	public static void Keep(RunContext a_ctx, List<Cache> a_all, Cache a_c, Dictionary<string, char> a_states)
	{
		// the cache that stood in overwrite before the run is replaced with it: brought back by a later failure in this step, it
		// would win over the new cache's mod and the game would read the old one
		foreach (var p in a_ctx.Journal.Parked.Where(p => p.Step == "shaders" && p.IsFolder && !p.PutBack)) {
			p.Replaced = true;
		}
		a_ctx.Finished(a_c.OutputMod);
		var others = a_all.Where(o => o != a_c && !o.OutputMod.Equals(a_c.OutputMod, StringComparison.OrdinalIgnoreCase) && a_states.TryGetValue(o.OutputMod, out var f) && f == '+')
			.Select(o => o.OutputMod).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
		if (others.Count == 0) {
			return;
		}
		a_ctx.Log.Detail($"kept off from here: {string.Join(", ", others)} - the cache of a shader mod this profile does not run would be read in place of {a_c.OutputMod}");
		a_ctx.Journal.Retire.AddRange(others.Where(m => !a_ctx.Journal.Retire.Contains(m, StringComparer.OrdinalIgnoreCase)));
		a_ctx.Journal.Save();
		a_ctx.Control.Close(null, a_ctx.Cancel);
		a_ctx.Profile.SetMods([], others, a_ctx.Log);
	}

	public static List<string> OtherCacheMods(IEnumerable<Cache> a_all, Cache a_c) => a_all.Where(o => o != a_c)
		.SelectMany(o => new[] { o.ShaderMod, o.OutputMod, o.SettingsMod }).OfType<string>().ToList();

	// what one look at a compile sees. LogText: the shader mod's log of THIS start ("" = none found). Files: shaders in the cache,
	// NewThisStart of them compiled by this start. Cores: the game's processor use since the last look (null = not readable);
	// LowFor: how long it has stayed under IdleLine of its peak
	public sealed record Look(string LogText, int Files, int NewThisStart, TimeSpan SinceNewFile, TimeSpan SinceLogWrite, double? Cores, double PeakCores, TimeSpan LowFor, bool Sane);

	// a compile was seen working once the game used this many cores
	public const double BusyCores = 1.5;

	// the game is "low" under a third of its own peak and never above two cores (compiling uses every core; the main menu a
	// fraction of one or two). A PC whose main menu sits above this line is never read as quiet - the quiet fallback ends it
	public static double IdleLine(double a_peak) => Math.Max(0.75, Math.Min(2.0, a_peak * 0.35));

	// Is the compile over? In order: (1) the shader mod says so - Open Shaders' "Shader compilation completed", or a last
	// "remaining=0"; (2) the game itself went quiet - it was seen compiling (BusyCores), no shader has landed for a_idle and its
	// processor use has stayed under IdleLine of its peak that long (a long gap WITH the processor busy is a slow shader, never
	// the end); (3) the old fallback, for when neither speaks (the processor cannot be read, never showed the compile, or the
	// menu itself is busy): nothing new in the cache or the log for a_quiet - such a cache is stamped unverified and built again
	// next time
	public static (string Result, bool Verified)? Done(Look a_l, int a_minFiles, TimeSpan a_quiet, TimeSpan a_idle)
	{
		if (a_l.LogText.Split('\n').LastOrDefault(l => l.Contains("Shader compilation completed", StringComparison.Ordinal)) is { } done) {
			return (done.Trim(), true);
		}
		var enough = a_l.Files >= a_minFiles;
		// (a last count above 0 says nothing about now: Bottled Shaders writes that line only for a slow shader - InGame.RemainingIn)
		if (InGame.RemainingIn(a_l.LogText) == 0 && enough && a_l.SinceNewFile > TimeSpan.FromSeconds(30)) {
			return ($"compile finished: remaining=0 ({a_l.Files} files)", true);
		}
		if (!a_l.Sane || !enough) {
			return null;
		}
		// Open Shaders opens a compile with "started" and always closes it with "completed": while it is open, only its own line ends it
		var open = a_l.LogText.LastIndexOf("Shader compilation started", StringComparison.Ordinal) >= 0;
		// NewThisStart: a start that only loads (a restart with the earlier start's shaders already in the cache) is quiet too -
		// only shaders THIS start compiled show its compile ran and ended
		if (!open && a_l.NewThisStart > 0 && a_l.Cores is { } now && a_l.PeakCores >= BusyCores && a_l.SinceNewFile >= a_idle && a_l.LowFor >= a_idle) {
			return ($"compile finished: no new shader for {a_l.SinceNewFile.TotalSeconds:0} s and the game's processor use fell from {a_l.PeakCores:0.0} to {now:0.0} cores ({a_l.Files} files)", true);
		}
		if (a_l.SinceNewFile > a_quiet && a_l.SinceLogWrite > a_quiet) {
			return ($"compile finished by quiet: no new shader and no shader log line for {a_quiet.TotalMinutes:0} minutes ({a_l.Files} files)", false);
		}
		return null;
	}

	// The game ended before the compile did (his run of 2026-11-10: a crash was logged as "the game stopped ... start 2" and the
	// game simply started again). It is named for what it was, with the crash report the game wrote, and never restarted
	// silently: the setup's "on_crash" is "ask" (the default: the player presses Done to carry on, or Stop; nobody there for
	// "crash_wait_min" stops the step), "restart" (carry on at once, for a run nobody watches) or "stop".
	public static void GameEnded(RunContext a_ctx, StepSettings a_s, Cache a_c, int a_start, int a_files)
	{
		// the run's watch ended the game under a box nobody could answer: the step stops with that box, never as a crash
		if (a_ctx.BlockedBy is { } box) {
			throw new StepFailedException(box);
		}
		var reports = InGame.CrashReports(InGame.LogFolders(a_ctx.Setup, a_ctx.Mo2), a_ctx.Mo2.OverwriteDir, a_ctx.GameLaunched ?? a_ctx.Journal.Started);
		var what = reports.Count > 0
			? $"the game CRASHED while it compiled the {a_c.ShaderMod} shaders (start {a_start}, {a_files} shader(s) written so far) - its crash report: {string.Join(" and ", reports)}"
			: $"the game closed while it compiled the {a_c.ShaderMod} shaders (start {a_start}, {a_files} shader(s) written so far) - no crash report was written (a crash logger such as Crash Logger SSE names the cause of the next one)";
		what += InGame.LastBox(a_ctx);
		a_ctx.Log.Warn(what);
		a_ctx.Log.Simple("Note: " + what);
		a_ctx.Note("shaders: " + what);
		var max = a_s.Int("max_restarts", 3);
		if (a_start > max) {
			throw new StepFailedException($"{what} - that is {a_start} times, more than the {max} restart(s) this step allows");
		}
		var how = (a_s.Str("on_crash") ?? "ask").Trim().ToLowerInvariant();
		if (how == "stop") {
			throw new StepFailedException(what + " - the step stops here (the setup's \"on_crash\" is \"stop\")");
		}
		if (how == "restart") {
			a_ctx.Log.Detail($"starting the game again to carry on (the setup's \"on_crash\" is \"restart\") - start {a_start + 1}");
			return;
		}
		var wait = TimeSpan.FromMinutes(a_s.Num("crash_wait_min", 30));
		a_ctx.Log.Warn($"waiting up to {wait.TotalMinutes:0} minutes for your answer: Done (the window's button, or the command \"continue\") starts the game again, Stop ends the run");
		using var asked = CancellationTokenSource.CreateLinkedTokenSource(a_ctx.Cancel);
		asked.CancelAfter(wait);
		try {
			a_ctx.Guide.Ask("shaders", what + ". The shaders compiled so far are kept. Press Done to start the game again and carry on, or Stop to end the run.", asked.Token);
		} catch (OperationCanceledException) when (!a_ctx.Cancel.IsCancellationRequested) {
			throw new StepFailedException($"{what} - nobody answered for {wait.TotalMinutes:0} minutes, so the step stopped here; run again from this step");
		}
		a_ctx.Log.Detail($"carrying on, as asked - start {a_start + 1}");
	}

	static void Compile(RunContext a_ctx, StepSettings a_s, Build a_b, List<string> a_otherCaches)
	{
		a_ctx.Cancel.ThrowIfCancellationRequested();
		var c = a_b.Cache;
		var owCache = Path.Combine(a_ctx.Mo2.OverwriteDir, "ShaderCache");
		if (Directory.Exists(owCache) && Directory.EnumerateFileSystemEntries(owCache).Any()) {
			StepKit.ParkDir(a_ctx, owCache, "overwrite ShaderCache (before)", "shaders");
			Files.DeleteDir(owCache);
		}
		var logs = InGame.LogFolders(a_ctx.Setup, a_ctx.Mo2);
		string? result = null;
		var verified = true;
		var count = 0;
		var states = a_ctx.Profile.States();
		using (new Toggle(a_ctx, a_b.On, a_b.Off.Where(states.ContainsKey).ToList(), "the shader cache", a_ctx.Cancel)) {
			var restarts = 0;
			var offers = 0;
			var total = Stopwatch.StartNew();
			var minFiles = a_s.Int("min_files", 50);
			var quiet = TimeSpan.FromMinutes(a_s.Int("quiet_min", 5));
			var idle = TimeSpan.FromSeconds(a_s.Int("idle_s", 45));
			var toldNoLog = false;
			Process? g = null;
			// any failure or Stop: the game this step started is closed here, inside the Toggle, so MO2 can close for the put-back
			try {
				while (result == null) {
					a_ctx.Cancel.ThrowIfCancellationRequested();
					var started = DateTime.UtcNow;
					InGame.NoGameRunning(a_ctx);
					var sw = Stopwatch.StartNew();
					g?.Dispose();
					var launched = DateTime.Now;
					a_ctx.GameLaunched = launched;
					// shaders already in the cache came from an earlier start of this step: only new ones show THIS start works
					var atLaunch = Files.CountFiles(owCache);
					GameStart.Prepare(a_ctx);
					g = Mo2Control.Open(a_ctx.Control.LaunchGame(a_s.Str("launch_executable") ?? "SKSE", InGame.FreshGamePid(), a_ctx.Cancel), "the game");
					a_ctx.Activity("shaders", $"{c.ShaderMod}: the game compiles the shader cache at its main menu");
					var sane = false;
					var lastN = -1;
					var lastChange = Stopwatch.StartNew();
					TimeSpan? lastCpu = null;
					var lastCpuAt = Stopwatch.StartNew();
					var peak = 0.0;
					double? cores = null;
					Stopwatch? low = null;
					while (!g.HasExited) {
						a_ctx.Cancel.ThrowIfCancellationRequested();
						// (looked up again at every look: a mapped log folder may only be made during this start)
						logs = InGame.LogFolders(a_ctx.Setup, a_ctx.Mo2);
						var log = InGame.NewestShaderLog(logs);
						var logFresh = log != null && File.GetLastWriteTimeUtc(log) >= started.AddSeconds(-5);
						var text = logFresh ? Files.ReadSharedText(log!) : "";
						var n = Files.CountFiles(owCache);
						// the shader mod must show it runs within a few minutes (it stands down beside a DLL it refuses - 2026-10-09).
						// Shaders arriving in the cache ARE that sign: a log can be elsewhere (SKSE's log folder moved) or late - a
						// player's run of 2026-11-04 failed here with 101 shaders already written
						if (!sane && sw.Elapsed > TimeSpan.FromSeconds(45)) {
							var bad = Regex.Match(text, @"^.*(Incompatible DLL|Remove it to use|disabled itself|will not run).*$", RegexOptions.Multiline);
							if (bad.Success) {
								Procs.End(g);
								throw new StepFailedException("the shader mod stood down at start - nothing will compile: " + bad.Value.Trim());
							}
							if (n > atLaunch || logFresh && Regex.IsMatch(text, "Loading Settings Complete|Loaded theme|Shader compilation started|ShaderTiming")) {
								sane = true;
								if (!logFresh && !toldNoLog) {
									toldNoLog = true;
									a_ctx.Log.Detail($"no shader log of this start in {string.Join(" or ", logs)} - the compile's end is judged by the game itself (new shaders, its processor use); " +
										"if your SKSE logs are written elsewhere, name that folder in Settings > Setup > SKSE log folder");
								}
							} else if (sw.Elapsed > TimeSpan.FromMinutes(a_s.Int("sanity_min", 3))) {
								// never blamed on the shader mod while something else holds the game: what is on screen is named
								var shows = InGame.WhatTheGameShows(a_ctx, started);
								Procs.End(g);
								throw new StepFailedException(shows != "" ?
									$"the game stopped at something this step does not know - {shows}. Answer or close it in game once (or turn off the mod that shows it), then run again from this step" :
									$"the shader mod gave no sign of running in {a_s.Int("sanity_min", 3)} minutes (no log, no shaders written) - is it ticked and loading?");
							}
						}
						if (n != lastN) {
							lastN = n;
							lastChange.Restart();
							a_ctx.Activity("shaders", $"{c.ShaderMod}: {n} shader(s) compiled");
						}
						// the game's own processor use between two looks, in cores
						if (Procs.CpuOrNull(g.Id) is { } cpu) {
							if (lastCpu is { } was && lastCpuAt.Elapsed.TotalSeconds >= 1) {
								cores = Math.Max(0, (cpu - was).TotalSeconds / lastCpuAt.Elapsed.TotalSeconds);
								peak = Math.Max(peak, cores.Value);
								if (cores < IdleLine(peak)) {
									low ??= Stopwatch.StartNew();
								} else {
									low = null;
								}
							}
							lastCpu = cpu;
							lastCpuAt.Restart();
						} else {
							cores = null;
							low = null;
							lastCpu = null;
						}
						var sinceLog = logFresh ? DateTime.UtcNow - File.GetLastWriteTimeUtc(log!) : TimeSpan.MaxValue;
						if (Done(new Look(text, n, n - atLaunch, lastChange.Elapsed, sinceLog, cores, peak, low?.Elapsed ?? TimeSpan.Zero, sane), minFiles, quiet, idle) is { } end) {
							(result, verified) = end;
							break;
						}
						if (total.Elapsed > a_s.Deadline(180)) {
							Procs.End(g);
							throw new StepFailedException($"the shader cache passed its {a_s.Deadline(180).TotalMinutes:0} minute limit");
						}
						// a Stop ends the wait at once (the game is ended by the stop itself)
						a_ctx.Cancel.WaitHandle.WaitOne(5000);
					}
					if (result != null) {
						break;
					}
					// a game ended by Stop is never started again
					a_ctx.Cancel.ThrowIfCancellationRequested();
					// the time spent waiting for the player's answer is not the compile's: it never counts against the step's limit
					total.Stop();
					// Faster Archives closed the game for its offer: no crash, and not one of the step's restarts
					if (a_ctx.BlockedBy == null && FasterArchives.Offered(logs, launched, a_ctx.Control.Started)) {
						FasterArchives.Wait(a_ctx, "shaders", a_s, ++offers, launched);
					} else {
						GameEnded(a_ctx, a_s, c, ++restarts, Files.CountFiles(owCache));
					}
					total.Start();
					a_ctx.Pause(TimeSpan.FromSeconds(5));
				}
				a_ctx.Log.Detail(result);
				// (a Stop in this pause leaves through the catch below, which closes the game before Mod Organizer is asked to close)
				a_ctx.Pause(TimeSpan.FromSeconds(a_s.Int("settle_s", 30)));
			} catch {
				InGame.CloseGame(a_ctx);
				throw;
			} finally {
				g?.Dispose();
			}
			InGame.CloseGame(a_ctx);
			count = Files.CountFiles(owCache);
			if (count < minFiles) {
				throw new StepFailedException($"the shader cache looks empty ({count} files)");
			}
			StepKit.ParkMod(a_ctx, "shaders", c.OutputMod);
			Files.MoveContents(owCache, Path.Combine(a_ctx.Mo2.ModDir(c.OutputMod), "ShaderCache"));
			Files.DeleteDir(owCache);
			StepKit.MarkReplaced(a_ctx, c.OutputMod);
		}
		// stamped against the profile as it is put back, the state the next freshness check reads - never the compile's own switches
		// the cache is built and kept by now: a stamp that cannot be written only means it is built again next time
		try {
			Files.WriteAtomic(StampFile(a_ctx.Mo2, c), new JsonObject { ["fingerprint"] = Fingerprint(a_ctx.Mo2, c, a_ctx.Profile, a_otherCaches, a_b.Layers), ["built"] = DateTime.Now.ToString("O"), ["files"] = count,
				["verified"] = verified, ["winners"] = true, ["settings"] = StampSettings }.ToJsonString());
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			a_ctx.Log.Warn($"the {c.ShaderMod} cache is built, but its record could not be written ({e.Message}) - the next run builds it again");
		}
		a_ctx.Note($"shaders ({c.OutputMod}): {result}");
	}
}
