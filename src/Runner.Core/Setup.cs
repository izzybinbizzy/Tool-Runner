using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ToolRunner;

public enum ShaderRebuild
{
	WhenChanged,
	Always,
	Never,
}

// One setup = one MO2 profile and how the runner treats it: which steps, in which order, each step's own settings, and the
// run-wide options. Stored as JSON; keys the runner does not know (and every "_note" key) are kept as they are on save.
public sealed class Setup
{
	public const int Schema = 2;

	public string File { get; private set; } = "";
	public JsonObject Root { get; private set; } = new();

	public string Name => Str(Root, "name") ?? Path.GetFileNameWithoutExtension(File);
	public string Mo2Exe => Str(Obj("mo2"), "exe") ?? "";
	public string? Mo2Instance => Str(Obj("mo2"), "instance");
	public string Profile => Str(Obj("mo2"), "profile") ?? "";
	public string BackupDir => Str(Root, "backups") ?? "";
	public int IdleMinutes => Int(Root, "idleMinutes") ?? 2;
	public int MinFreeGb => Int(Root, "minFreeGb") ?? 20;
	public bool AlertNotification => Bool(Obj("alerts"), "notification") ?? true;
	public bool AlertSound => Bool(Obj("alerts"), "sound") ?? true;
	public bool AlertFlash => Bool(Obj("alerts"), "flash") ?? false;

	public ShaderRebuild ShaderRebuild => Str(Root, "shaderRebuild")?.ToLowerInvariant() switch {
		"always" => ShaderRebuild.Always,
		"never" => ShaderRebuild.Never,
		_ => ShaderRebuild.WhenChanged,
	};

	public IReadOnlyList<string> LodOutputs => List(Root, "lodOutputs");
	public IReadOnlyList<string> BeforeRun => List(Root, "beforeRun");
	public IReadOnlyList<string> AfterRun => List(Root, "afterRun");

	// what the "What MO2 changed" view leaves out (a profile only tests use, test mods)
	public IReadOnlyList<string> ChangesHideProfiles => List(Obj("changes"), "hideProfiles");
	public IReadOnlyList<string> ChangesHideMods => List(Obj("changes"), "hideModsStartingWith");

	public IReadOnlyDictionary<string, string> PlaceAfter =>
		(Root["placeAfter"] as JsonObject)?.Where(p => p.Value is JsonValue).ToDictionary(p => p.Key, p => p.Value!.ToString(), StringComparer.OrdinalIgnoreCase) ??
		new Dictionary<string, string>();

	public IReadOnlyList<Profile.PlaceRule> Placement =>
		// (a setup file comes from anywhere: an entry whose two names are not both given is no rule - a null there threw at the
		// end of the put-back, the one place this is read; the check names such an entry)
		(Root["placement"] as JsonArray)?.OfType<JsonArray>().Where(IsPlace)
			.Select(a => new Profile.PlaceRule(a[0]!.ToString(), a[1]!.ToString(), a.Count > 2 && a[2]?.ToString() == "active")).ToList() ?? [];

	// a plugin place: two plugin names, each a string with something in it (a number, a true or "" names no plugin)
	static bool IsPlace(JsonArray a_entry) => a_entry.Count >= 2 && PluginName(a_entry[0]) && PluginName(a_entry[1]);

	static bool PluginName(JsonNode? a_n) => a_n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s);

	// UseOld: the step is not run and the output it made before is used (OldOutput) - it counts only for a step that is on
	public sealed record StepEntry(string Key, bool On, JsonObject Settings, bool UseOld = false);

	public List<StepEntry> Steps =>
		(Root["steps"] as JsonArray)?.OfType<JsonObject>().Select(o => new StepEntry(Str(o, "step") ?? "", Bool(o, "on") ?? true,
			o["settings"] as JsonObject ?? new JsonObject(), Bool(o, OldOutput.Key) ?? false)).Where(s => s.Key != "").ToList() ?? [];

	// a new game (the player starts a new character after the run): there is no save of theirs to keep clean or to bring
	// back, so the clean save and the final save are left out of every run while it is on (dyndolod.info/Help/Clean-Save)
	public bool NewGame => Bool(Root, "newGame") ?? false;

	public static readonly IReadOnlyList<string> SaveSteps = ["cleansave", "playsave"];

	public bool LeftOutForNewGame(string a_key) => NewGame && SaveSteps.Contains(a_key);

	// a step that is on, is not left out, and is set to use the output it made before: its tool does not run (OldOutput)
	public bool UsesOld(StepEntry a_s) => a_s.On && a_s.UseOld && !LeftOutForNewGame(a_s.Key) && OldOutput.CanKeep(a_s.Key, new StepSettings(a_s.Settings));

	// the steps that are switched on (and not left out for a new game), whether a run runs them or uses their old output. A step
	// this program does not have (one an older version had, or a name it never knew) is left out of every run: an older setup
	// file still loads, and the check says what it leaves out (NotKnown)
	public List<StepEntry> OnSteps => Steps.Where(s => s.On && !LeftOutForNewGame(s.Key) && (StepCatalog.Known(s.Key) || s.Key is "snowfixer" or "autoblend")).ToList();

	// the steps the setup names that this program does not have, each once
	public List<string> NotKnown => Steps.Select(s => s.Key).Where(k => !StepCatalog.Known(k) && k is not "snowfixer" and not "autoblend").Distinct().ToList();

	// the steps a run runs
	public List<StepEntry> EnabledSteps => OnSteps.Where(s => !UsesOld(s)).ToList();

	// the steps a run does not run, because the output each made before is used
	// (only of steps this version has: a removed step's old output is not managed)
	public List<StepEntry> KeptSteps => OnSteps.Where(UsesOld).ToList();

	public const string PgLiteMod = "PGLite";

	// PG Lite does while the game runs what PGPatcher builds ahead, so with PGLite ticked the PGPatcher step is switched off once
	// and runs only when the player ticks it back; that tick is kept as their choice until PGLite is unticked. Returns null when
	// nothing changed, "" for a quiet change, else the notice to show
	public string? ApplyPgLiteRule(bool a_pgLiteTicked)
	{
		if ((Root["steps"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(o => Str(o, "step") == "pgpatcher") is not { } step) {
			return null;
		}
		var settings = step["settings"] as JsonObject;
		if (settings == null) {
			settings = new JsonObject();
			step["settings"] = settings;
		}
		var on = Bool(step, "on") ?? true;
		if (!a_pgLiteTicked) {
			// switched off by this rule and not touched since: its reason is gone, so the step is on again, and said - left off,
			// a player who took PGLite out would have lost PGPatcher without ever having unticked it
			if (Bool(settings, "pglite_unticked") == true && !on) {
				settings.Remove("pglite_unticked");
				step["on"] = true;
				return "PGLite is no longer ticked in this profile, so the PGPatcher step is switched on again.";
			}
			var had = settings.Remove("pglite_unticked") | settings.Remove("with_pglite");
			return had ? "" : null;
		}
		if (Bool(settings, "with_pglite") == true || !on) {
			return null;
		}
		if (Bool(settings, "pglite_unticked") == true) {
			settings.Remove("pglite_unticked");
			settings["with_pglite"] = true;
			return "";
		}
		step["on"] = false;
		settings["pglite_unticked"] = true;
		return "PGLite is ticked in this profile, so the PGPatcher step was switched off (PG Lite does that work while you play). Tick PGPatcher again to run it anyway.";
	}

	public JsonObject? StepSettings(string a_key) => Steps.FirstOrDefault(s => s.Key == a_key)?.Settings;

	JsonObject Obj(string a_key) => Root[a_key] as JsonObject ?? new JsonObject();

	// an emptied setting counts as not set (its default applies) - "" never runs PGPatcher without --autostart
	public static string? Str(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

	public static int? Int(JsonObject a_o, string a_key) =>
		Num(a_o, a_key) is { } d && d >= int.MinValue && d <= int.MaxValue ? (int)d : null;

	// A number read from a file answers as any kind of number; one put into a setup in memory answers only as the kind it
	// was made with. Settings keeps a whole number typed there as a long: asked for as an int or a double alone it read as
	// not set, and until the setup was loaded again the window worked with the default (5 backups kept, where 20 were asked for)
	// A setup file is also written by hand: a number or a switch in quotes ("90", "false") says what it says without them,
	// and one name where a list is read is a list of that one. Read as not set, the step ran with its default - a step
	// switched "false" ran - while Settings showed what the file said
	// (a number past what a number holds - 1e400 - reads as endless: none)
	public static double? Num(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v && (v.TryGetValue<double>(out var d) ? d : v.TryGetValue<long>(out var l) ? l : v.TryGetValue<int>(out var i) ? i :
			v.TryGetValue<string>(out var s) && double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, InvariantCulture, out var typed) ? typed : double.NaN) is var n &&
		double.IsFinite(n) ? n : null;

	public static bool? Bool(JsonObject a_o, string a_key) =>
		a_o[a_key] is JsonValue v ? (v.TryGetValue<bool>(out var b) ? b : v.TryGetValue<string>(out var s) && bool.TryParse(s, out var typed) ? typed : null) : null;

	public static List<string> List(JsonObject a_o, string a_key) => a_o[a_key] switch {
		JsonArray many => many.Select(n => n?.ToString() ?? "").Where(s => s != "").ToList(),
		JsonValue v when v.TryGetValue<string>(out var one) && !string.IsNullOrWhiteSpace(one) => [one],
		_ => [],
	};

	static readonly JsonDocumentOptions ReadOptions = JsonRead.ByHand;
	static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// what the program keeps beside the setups (the tools' versions, the outputs' sizes): files of its own in that folder, never
	// setups - a list of setups leaves them out
	public static readonly IReadOnlyList<string> StoreNames = ["tool versions.json", "output sizes.json"];

	public static bool IsStore(string a_file) => StoreNames.Contains(Path.GetFileName(a_file), StringComparer.OrdinalIgnoreCase);

	public static Setup Load(string a_file) => FromText(Files.ReadSharedText(a_file), a_file);

	// a setup file's text as its object, read as Load reads it (comments and a last comma allowed); null when it holds no object
	public static JsonObject? RootOf(string a_text) => JsonNode.Parse(a_text, documentOptions: ReadOptions) as JsonObject;

	// ... and written as Save writes it
	public static string TextOf(JsonNode a_root) => a_root.ToJsonString(WriteOptions);

	// the setup as these bytes say (the preview reads the file once and shows exactly what then runs)
	public static Setup Parse(byte[] a_bytes, string a_file)
	{
		using var r = new StreamReader(new MemoryStream(a_bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
		return FromText(r.ReadToEnd(), a_file);
	}

	static Setup FromText(string a_text, string a_file)
	{
		var node = JsonNode.Parse(a_text, documentOptions: ReadOptions) as JsonObject ??
		           throw new InvalidDataException("not a setup file (no JSON object): " + a_file);
		return new Setup { File = a_file, Root = node };
	}

	public static Setup FromJson(JsonObject a_root, string a_file = "") => new() { Root = a_root, File = a_file };

	// what is wrong with it, in plain words (empty = fine)
	public List<string> Problems()
	{
		var p = new List<string>();
		if (Mo2Exe == "") p.Add("Mod Organizer's program is not set");
		if (Profile == "") p.Add("the MO2 profile is not set");
		if (BackupDir == "") p.Add("the backup folder is not set");
		// whole local paths only: a relative one would follow whatever folder the run was started from
		foreach (var (what, dir) in Folders()) {
			if (dir != "" && (!Path.IsPathFullyQualified(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))) {
				p.Add($"{what} must be a whole path on this PC (like D:\\Modding\\...): {dir}");
			}
		}
		if (Steps.Count == 0) p.Add("the setup has no steps");
		if (EnabledSteps.Count == 0) p.Add(KeptSteps.Count > 0 ? "every step that is switched on uses its old output - nothing would run" :
			Steps.Any(s => s.On) && Steps.Where(s => s.On).All(s => NotKnown.Contains(s.Key)) ? "none of the steps it has switched on is one this version has: " + string.Join(", ", NotKnown) :
			Steps.Any(s => s.On) ? "only the save steps are switched on, and they are left out for a new game" : "every step is switched off");
		var dup = Steps.GroupBy(s => s.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
		if (dup.Count > 0) p.Add("a step is listed twice: " + string.Join(", ", dup));
		// SnowFixer and AutoBlend are no longer supported: Landscape Fixer, by the same author, replaced both
		foreach (var gone in Steps.Select(s => s.Key).Where(k => k is "snowfixer" or "autoblend").Distinct()) {
			p.Add($"{(gone == "snowfixer" ? "SnowFixer" : "AutoBlend")} is no longer supported - Landscape Fixer replaced it: add Landscape Fixer and take this step out");
		}
		// A setup file comes from anywhere (a guide, a Discord). What it gives as a mod's name is joined to Mod Organizer's mods
		// folder as it stands: with a folder part in it ("..\..\profiles\Default") or a whole path, a meta.ini was written there,
		// a line put into the mod list and everything in that folder moved into the run's backup. Only a plain folder name is a mod
		foreach (var (where, name) in ModNames().Where(m => !PlainName(m.Name)).DistinctBy(m => m.Name).Take(5)) {
			p.Add($"{where} is not a mod's name (one plain folder name: no \\ / : and no \"..\", and none Windows keeps for a device, like CON or NUL): {name}");
		}
		// its own name names the run's folder and its backup: with a folder part in it those landed outside the program's data
		if (Str(Root, "name") is { } own && !PlainName(own)) {
			p.Add($"the setup's name is not a plain name (one folder name: no \\ / : * ? \" < > | and no \"..\", and none Windows keeps for a device, like CON or NUL): {own}");
		}
		if (Root["placement"] is JsonArray places && places.Any(e => e is not JsonArray a || !IsPlace(a))) {
			p.Add("the setup's \"placement\" holds an entry that is not [plugin, place]");
		}
		// a place that is none of the three was read as "end": a slip of the hand ("afer:Skyrim.esm") moved the plugin to the very end
		foreach (var odd in Placement.Where(r => r.Where is not ("last_master" or "end") && !(r.Where.StartsWith("after:", StringComparison.OrdinalIgnoreCase) && r.Where.Length > 6)).Take(3)) {
			p.Add($"the setup's \"placement\" gives {odd.Plugin} the place \"{odd.Where}\" - a place is last_master, after:<plugin> or end");
		}
		return p;
	}

	// the settings whose value the engine takes as a mod's name (or, "settings", as a file's own name in a fixed folder)
	static readonly string[] NameKeys = ["output_mod", "then_move_to_mod", "tool_writes", "pgpatcher_mod", "shader_mod", "settings_mod", "settings",
		"replaces", "enable_during", "disable_during"];

	// every such value in the setup (a cache list's too), with where it stands
	public IEnumerable<(string Where, string Name)> ModNames()
	{
		static IEnumerable<(string, string)> Walk(JsonNode? a_node, string a_step)
		{
			if (a_node is JsonArray list) {
				foreach (var x in list.SelectMany(e => Walk(e, a_step))) {
					yield return x;
				}
			} else if (a_node is JsonObject o) {
				foreach (var (key, value) in o) {
					if (!NameKeys.Contains(key)) {
						foreach (var x in Walk(value, a_step)) {
							yield return x;
						}
						continue;
					}
					foreach (var one in value is JsonArray many ? many.ToList() : [value]) {
						if (one is JsonValue v && v.TryGetValue<string>(out var name) && name != "") {
							yield return ($"{a_step}'s \"{key}\"", name);
						}
					}
				}
			}
		}
		return List(Root, "lodOutputs").Select(m => ("\"lodOutputs\"", m)).Concat(Steps.SelectMany(s => Walk(s.Settings, s.Key)));
	}

	// one plain folder (or file) name, as Windows takes one
	public static bool PlainName(string a_name) =>
		a_name.Trim() != "" && a_name is not "." and not ".." && !a_name.EndsWith('.') && !a_name.EndsWith(' ') &&
		a_name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) < 0 && !a_name.Any(char.IsControl) && !IsDeviceName(a_name);

	// the names Windows keeps for its devices: a file or folder called one of them, with an ending too ("NUL.txt"), is that device
	static readonly string[] DeviceNames = ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
		"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

	public static bool IsDeviceName(string a_name) => DeviceNames.Contains(a_name.Split('.')[0].Trim(), StringComparer.OrdinalIgnoreCase);

	// A setup that came from somewhere else never runs a command on this PC: its own commands before and after a run are taken
	// out (the sharing side takes them out too, but a file passed around need not have come through it). Returns how many went
	public int TakeOutCommands()
	{
		var n = BeforeRun.Count + AfterRun.Count;
		Root.Remove("beforeRun");
		Root.Remove("afterRun");
		return n;
	}

	// the files Windows starts as a program
	static readonly string[] ProgramKinds = [".exe", ".bat", ".cmd", ".com", ".scr", ".pif", ".ps1", ".vbs"];

	// a step's setting that names a program: by its key ("exe", "fasterngio_exe", Synthesis's "exe_path" - whatever the
	// value ends with, since the step starts it), or by what its value ends with
	static bool NamesProgram(string a_key, string a_value) =>
		a_key is "exe" || a_key.EndsWith("_exe", StringComparison.Ordinal) || a_key.StartsWith("exe_", StringComparison.Ordinal) ||
		ProgramKinds.Any(x => a_value.Trim().Trim('"').EndsWith(x, StringComparison.OrdinalIgnoreCase));

	// What the setup starts on this PC by itself, one line each, in words: Mod Organizer, its own commands before and after
	// a run, and every program a step names - what is taken out of a setup that is brought in from elsewhere. A step that is
	// switched off counts too: switching it on is one click, and asks nothing
	public List<string> Starts()
	{
		var lines = new List<string>();
		if (Mo2Exe != "") {
			lines.Add("Mod Organizer: " + Mo2Exe);
		}
		lines.AddRange(BeforeRun.Select(c => "a command before each run: " + c));
		lines.AddRange(AfterRun.Select(c => "a command after each run: " + c));
		foreach (var s in Steps) {
			foreach (var (key, value) in s.Settings) {
				// ("image" is the name the tool's process runs under, which the step waits for: it starts nothing)
				if (key != "image" && value is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim() != "" && NamesProgram(key, text)) {
					lines.Add($"{StepCatalog.Name(s.Key)} ({key}): {text}");
				}
			}
		}
		return lines;
	}

	// a mark of what the setup starts: the same for as long as none of it changes, whatever else in the setup does
	public string StartsMark() =>
		Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", Starts()))));

	// A setup that came from somewhere else never starts a program on this PC that the player did not point it at, and never
	// allows an add-on for them: every program path it names is taken out (Mod Organizer's and each step's), and with it every
	// add-on step's approval and the setting that turns the add-on question off. Returns what went, in words
	public List<string> TakeOutWhatStartsPrograms()
	{
		var gone = new List<string>();
		if (Root["mo2"] is JsonObject mo2 && Str(mo2, "exe") is { Length: > 0 }) {
			mo2["exe"] = "";
			gone.Add("which Mod Organizer it starts (mo2.exe)");
		}
		if (Root.Remove(AddOns.NoAskSetting)) {
			gone.Add($"the setting that allows every add-on without asking ({AddOns.NoAskSetting})");
		}
		foreach (var s in Steps) {
			foreach (var (key, value) in s.Settings.ToList()) {
				if (value is JsonValue v && v.TryGetValue<string>(out var text) && text.Trim() != "" && NamesProgram(key, text)) {
					s.Settings.Remove(key);
					gone.Add($"the program {StepCatalog.Name(s.Key)} starts ({key})");
				}
			}
			if (s.Key.StartsWith(AddOns.KeyPrefix, StringComparison.Ordinal) && s.Settings.Remove("sha")) {
				gone.Add($"the approval of the add-on {s.Key[AddOns.KeyPrefix.Length..]} (allow it again in Settings > Tools > ADD-ONS)");
			}
		}
		return gone;
	}

	// the folders the setup itself names (empty ones included): its backups, its scratch, each step's output (a_running: only the
	// steps a run would run - not one switched off, nor a save step left out for a new game)
	public IEnumerable<(string What, string Dir)> Folders(bool a_running = false) =>
		new[] { ("the backup folder", BackupDir), ("the scratch folder", Str(Root, "scratch") ?? "") }
			.Concat((a_running ? EnabledSteps : Steps).Select(s => ($"{s.Key}'s output folder", Str(s.Settings, "output_dir") ?? "")));

	public void Save(string? a_file = null)
	{
		File = a_file ?? File;
		Files.WriteAtomic(File, Text());
	}

	// the setup as its file holds it
	public string Text()
	{
		Root["schema"] = Schema;
		return Root.ToJsonString(WriteOptions);
	}

	// how many copies of a setup's earlier files are kept beside the data (the newest ones)
	public const int CopiesKept = 20;

	// A setup's text written to its file, with the file as it stood copied into a_copies first (under the setup's name and
	// the time; only the newest CopiesKept of that setup stay). Disk work alone, on text made beforehand: the window calls
	// it away from its own thread, where a slow disk held up every click. Returns why it was not written, or null when it was
	// a_now: the time the copy is named after (the offline tests name one; else now)
	public static string? WriteKeepingACopy(string a_file, string a_text, string a_copies, DateTime? a_now = null)
	{
		try {
			Directory.CreateDirectory(a_copies);
			if (System.IO.File.Exists(a_file)) {
				var name = Path.GetFileNameWithoutExtension(a_file);
				var stamp = (a_now ?? DateTime.Now).ToString("yyyy-MM-dd HH-mm-ss", InvariantCulture);
				// this setup's own copies: its name, the time, and for the later ones of one second a number
				var own = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name) + @" (\d{4}-\d\d-\d\d \d\d-\d\d-\d\d)(?: (\d{1,9}))?\.json$");
				List<(FileInfo File, string Stamp, int Number)> Own() => [.. new DirectoryInfo(a_copies).GetFiles("*.json").Select(f => (File: f, Is: own.Match(f.Name))).Where(c => c.Is.Success)
					.Select(c => (c.File, c.Is.Groups[1].Value, c.Is.Groups[2].Success ? int.Parse(c.Is.Groups[2].Value, InvariantCulture) : 1))];
				// A copy is never written over: two saves within one second each keep the file as it stood before them (the
				// later ones carry a number after the time - written over, the first save's copy was lost to the second's).
				// The number is one above the highest of that second that is still there, never a free one below it: once the
				// tidy below has taken the oldest of that second away, its name read as the oldest again and the copy just
				// made under it was the next one tidied away
				var number = Own().Where(c => c.Stamp == stamp).Select(c => c.Number).DefaultIfEmpty(0).Max() + 1;
				var made = Path.Combine(a_copies, $"{name} {stamp}{(number == 1 ? "" : " " + number.ToString(InvariantCulture))}.json");
				System.IO.File.Copy(a_file, made, false);
				// The copy just made always stays, and the newest of the others with it: by their time, then by the number of
				// those made in one second. (With the PC's clock set back, the others all read newer than it - tidied by time
				// alone, the copy of the version this save replaces was the one that went)
				foreach (var old in Own().Where(c => !Files.SamePath(c.File.FullName, made)).OrderByDescending(c => c.Stamp, StringComparer.Ordinal).ThenByDescending(c => c.Number).Skip(CopiesKept - 1)) {
					old.File.Delete();
				}
			}
			Files.WriteAtomic(a_file, a_text);
			return null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return e.Message;
		}
	}

	public Setup Clone() => FromJson((JsonObject)Root.DeepClone(), File);

}
