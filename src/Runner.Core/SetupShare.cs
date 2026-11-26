using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolRunner;

// Shareable setups: a copy of a setup another player can import - its steps, their order and their options
// kept, everything that belongs to this PC taken out: the Mod Organizer it points at, the backup folder, every value that holds a
// path of this PC, and the commands it runs before and after a run (a shared file must never run something on another PC). The
// player's Windows and PC names are masked in what is left. The copy says what the importing player sets themselves.
public static partial class SetupShare
{
	// a drive path (C:\... or a bare C:), a UNC path (\\server\... or //server/...), a %VARIABLE% path (%ProgramFiles(x86)% too) or a
	// home path (~\...), anywhere in a value - a web address (https://...) is none
	[GeneratedRegex(@"(?i)(\b[a-z]:([\\/]|$|\s)|\\\\[^\\\s]+\\|(^|[\s""'=])//[^/\s]+/|%[a-z0-9_()]+%[\\/]|(^|\s|"")~[\\/])")]
	private static partial Regex PathLike();

	public static bool HoldsPath(string a_value) => PathLike().IsMatch(a_value);

	public sealed record Result(JsonObject Setup, List<string> Removed);

	public static Result Share(Setup a_setup, string a_version, List<(string Name, string Mask)>? a_names = null)
	{
		var root = (JsonObject)a_setup.Root.DeepClone();
		var removed = new List<string>();
		if (root["mo2"] is JsonObject mo2) {
			if (mo2.ContainsKey("exe")) {
				mo2["exe"] = "";
				removed.Add("which Mod Organizer it runs (mo2.exe)");
			}
			if (mo2.Remove("instance")) {
				removed.Add("the Mod Organizer instance (mo2.instance)");
			}
		}
		if (root.Remove("backups")) {
			removed.Add("the backup folder (backups)");
		}
		foreach (var key in new[] { "beforeRun", "afterRun" }) {
			if (Setup.List(root, key).Count > 0) {
				removed.Add($"the commands run {(key == "beforeRun" ? "before" : "after")} a run ({key}) - a shared setup never runs a command on another PC");
			}
			root.Remove(key);
		}
		Scrub(root, "", removed);
		// a PGPatcher settings set is a file beside the setup, not in it: the other player makes their own of that name
		foreach (var set in a_setup.Steps.Where(s => s.Key == "pgpatcher").Select(s => new StepSettings(s.Settings).Str("settings")).OfType<string>()
			         .Where(s => s.Trim() != "").Distinct(StringComparer.OrdinalIgnoreCase)) {
			removed.Add($"the PGPatcher settings set \"{set}\" it names - its file is not in the copy: make one of that name beside the setup " +
				$"(pgpatcher settings\\{set}.json), or name your own set");
		}
		// the player's names masked in the values only - a key is the setup's own wording, and a name equal to one ("Steps") must
		// not rename it; nor is a step's own key a name
		MaskValues(root, a_names ?? BugReport.Names());
		root["shared"] = new JsonObject {
			["by"] = $"{Brand.Name} {a_version}",
			["on"] = DateTime.Now.ToString("yyyy-MM-dd", InvariantCulture),
			["setYourself"] = new JsonArray(removed.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray()),
		};
		return new Result(root, removed);
	}

	static void MaskValues(JsonNode? a_node, List<(string Name, string Mask)> a_names)
	{
		switch (a_node) {
		case JsonObject o:
			foreach (var (key, value) in o.ToList()) {
				if (value is JsonValue v && v.TryGetValue<string>(out var s)) {
					if (key != "step") {
						o[key] = BugReport.Mask(s, a_names);
					}
				} else {
					MaskValues(value, a_names);
				}
			}
			break;
		case JsonArray a:
			for (var i = 0; i < a.Count; ++i) {
				if (a[i] is JsonValue v && v.TryGetValue<string>(out var s)) {
					a[i] = BugReport.Mask(s, a_names);
				} else {
					MaskValues(a[i], a_names);
				}
			}
			break;
		}
	}

	// A setup brought in from elsewhere keeps no path of the PC it came from: a folder a step empties into the run's backup, a
	// file it writes over, a program it starts - each named there is the other PC's, and on this one it can be any folder of
	// the player's. Taken out as the sharing side takes them out; returns what went, in words
	public static List<string> TakeOutPaths(JsonObject a_root)
	{
		var removed = new List<string>();
		Scrub(a_root, "", removed, "the PC it came from");
		return removed;
	}

	// every string value that holds a path of this PC is taken out (a list keeps its other items); what went is named
	static void Scrub(JsonNode a_node, string a_where, List<string> a_removed, string a_whose = "the sharing PC")
	{
		switch (a_node) {
		case JsonObject o:
			foreach (var (key, value) in o.ToList()) {
				var where = a_where == "" ? key : a_where + "." + key;
				if (value is JsonValue v && v.TryGetValue<string>(out var s) && HoldsPath(s)) {
					o.Remove(key);
					a_removed.Add($"a path of {a_whose} ({where})");
				} else if (value != null) {
					Scrub(value, where, a_removed, a_whose);
				}
			}
			break;
		case JsonArray a:
			for (var i = a.Count - 1; i >= 0; --i) {
				if (a[i] is JsonValue v && v.TryGetValue<string>(out var s) && HoldsPath(s)) {
					a.RemoveAt(i);
					a_removed.Add($"a path of {a_whose} ({a_where}[{i}])");
				} else if (a[i] is { } child) {
					Scrub(child, a_where + $"[{i}]", a_removed, a_whose);
				}
			}
			break;
		}
	}

	// writes the shared copy into a_outDir as "<name> (shared).json" - the name masked as the copy is, and never over the setup
	// itself (a setup already named "... (shared)" shared into its own folder); returns its path and what was taken out
	public static (string File, List<string> Removed) Write(Setup a_setup, string a_outDir, string a_version, List<(string Name, string Mask)>? a_names = null)
	{
		var names = a_names ?? BugReport.Names();
		var r = Share(a_setup, a_version, names);
		Directory.CreateDirectory(a_outDir);
		var name = string.Concat(BugReport.Mask(a_setup.Name, names).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
		name = name == "" ? "setup" : name;
		var file = Path.Combine(a_outDir, $"{name} (shared).json");
		if (a_setup.File != "" && Files.SamePath(file, a_setup.File)) {
			file = Path.Combine(a_outDir, $"{name} (shared copy).json");
		}
		Files.WriteAtomic(file, Setup.TextOf(r.Setup));
		return (file, r.Removed);
	}

	// the Mod Organizer and the profile a setup that is brought in is made for
	public sealed record ForProfile(string Mo2Exe, string Instance, string Profile);

	// the settings that name an output mod of a run (its own, or another step's that is switched for it)
	static readonly string[] OutputKeys = ["output_mod", "then_move_to_mod", "replaces", "enable_during", "disable_during"];

	// Every output mod name the setup made from its pattern and the profile it was made for ("{output} - {profile}":
	// "TexGen Output - Their Profile") becomes that output's name for a_profile; a name that is not of that shape - a mod of
	// the player's - stays. Each profile keeps outputs of its own that way, as a new setup's do. Returns how many changed
	static int NameOutputsFor(Setup a_setup, string a_profile)
	{
		var pattern = Setup.Str(a_setup.Root, "outputPattern") ?? "{output} - {profile}";
		var (old, at) = (a_setup.Profile, pattern.IndexOf("{output}", StringComparison.OrdinalIgnoreCase));
		if (old == "" || at < 0 || !pattern.Contains("{profile}", StringComparison.OrdinalIgnoreCase) || string.Equals(old, a_profile, StringComparison.OrdinalIgnoreCase)) {
			return 0;
		}
		var before = pattern[..at].Replace("{profile}", old, StringComparison.OrdinalIgnoreCase);
		var after = pattern[(at + "{output}".Length)..].Replace("{profile}", old, StringComparison.OrdinalIgnoreCase);
		string? Named(string a_name) =>
			a_name.Length > before.Length + after.Length && a_name.StartsWith(before, StringComparison.OrdinalIgnoreCase) && a_name.EndsWith(after, StringComparison.OrdinalIgnoreCase)
				? SetupMaker.OutputName(pattern, a_name[before.Length..^after.Length], a_profile) : null;
		int In(JsonArray a_list)
		{
			var n = 0;
			for (var i = 0; i < a_list.Count; ++i) {
				if (a_list[i] is JsonValue v && v.TryGetValue<string>(out var name) && Named(name) is { } to) {
					a_list[i] = to;
					++n;
				}
			}
			return n;
		}
		int Under(JsonNode? a_node)
		{
			var n = 0;
			if (a_node is JsonArray list) {
				n += list.Sum(Under);
			} else if (a_node is JsonObject o) {
				foreach (var (key, value) in o.ToList()) {
					if (!OutputKeys.Contains(key)) {
						n += Under(value);
					} else if (value is JsonArray many) {
						n += In(many);
					} else if (value is JsonValue v && v.TryGetValue<string>(out var name) && Named(name) is { } to) {
						o[key] = to;
						++n;
					}
				}
			}
			return n;
		}
		// (lodOutputs is read as a list or as one name, as Setup.List reads it: either form is named after the profile)
		var lodOutputs = 0;
		if (a_setup.Root["lodOutputs"] is JsonArray lod) {
			lodOutputs = In(lod);
		} else if (a_setup.Root["lodOutputs"] is JsonValue one && one.TryGetValue<string>(out var single) && Named(single) is { } renamed) {
			a_setup.Root["lodOutputs"] = renamed;
			lodOutputs = 1;
		}
		return a_setup.Steps.Sum(s => Under(s.Settings)) + lodOutputs;
	}

	// What bringing a setup in came to. File: where it was written (null: it was not brought in); Lines: what to tell the
	// player; NoName: the name it was to take is no setup's name; NotASetup: the file itself is no setup that can be brought in
	// (what was named is wrong, not something found on this PC - the command line tells the two apart by its exit code)
	public sealed record Brought(string? File, List<string> Lines, bool NoName = false, bool NotASetup = false);

	// The name a setup from elsewhere is kept under. It becomes a file name in the Setups folder and the name of its run
	// folders: no folder parts, no characters Windows refuses, no dot or space at its end (Windows drops those from a folder's
	// name). What is left passes the test a setup's name must pass before a run; null when nothing plain is left
	public static string? PlainNameOf(string a_given)
	{
		var name = string.Concat(Path.GetFileName(a_given).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim().TrimEnd('.', ' ');
		return Setup.PlainName(name) ? name : null;
	}

	// A setup file from elsewhere written into a_setups under a_name (its own name when none is given, its file's when that
	// is no name), with everything taken out that would start something on this PC: its commands, every program it names,
	// every path of the PC it came from, every add-on approval. a_freeName: a name that is taken gets a number after it
	// ("Name (2)") - the window, which asks for no name; else that is said and nothing is written. a_data: the program's
	// data folder - the setup's backups and scratch are kept in it, where a new setup keeps them (the other PC's folders
	// were taken out, and without a backup folder no run of it starts). a_for: the Mod Organizer and profile the player
	// picked for it - the setup is made for them, and its outputs are named after that profile
	public static Brought BringIn(string a_file, string? a_name, string a_setups, bool a_freeName = false, string? a_data = null, ForProfile? a_for = null)
	{
		var s = Setup.Load(Path.GetFullPath(a_file));
		if (s.Root["steps"] is not JsonArray || s.Root["mo2"] is not JsonObject) {
			return new(null, ["not a setup file (no steps list, no Mod Organizer): " + a_file], NotASetup: true);
		}
		if ((PlainNameOf(a_name ?? s.Name) ?? (a_name == null ? PlainNameOf(Path.GetFileNameWithoutExtension(a_file)) : null)) is not { } given) {
			return new(null, ["the setup's name is not a plain name"], true);
		}
		// the name's check and the write as one: the data's write lock, which every setup save holds (in every copy of the
		// program, the command line's included), is held from here until the setup is written - two imports of one name never
		// both find it free
		using var writes = DataFolder.HoldWrites();
		var name = given;
		var dst = Path.Combine(a_setups, name + ".json");
		for (var n = 2; a_freeName && (File.Exists(dst) || Setup.IsStore(dst)); ++n) {
			name = $"{given} ({n.ToString(InvariantCulture)})";
			dst = Path.Combine(a_setups, name + ".json");
		}
		// (the program keeps files of its own beside the setups: a setup under one of their names was never listed, and was
		// written over when a step next finished)
		if (Setup.IsStore(dst)) {
			return new(null, [$"\"{name}\" is the name of one of the program's own files"], true);
		}
		if (File.Exists(dst)) {
			return new(null, ["a setup with that name exists: " + dst]);
		}
		s.Root["name"] = name;
		// a file from elsewhere: what it gives as a mod's name must be one, and it brings no command of its own to run here
		var wrong = s.ModNames().Where(m => !Setup.PlainName(m.Name)).Select(m => $"{m.Where}: {m.Name}").Distinct().ToList();
		if (wrong.Count > 0) {
			return new(null, ["not brought in - these are not mod names (a plain folder name each): " + string.Join("; ", wrong.Take(8))], NotASetup: true);
		}
		var commands = s.TakeOutCommands();
		var programs = s.TakeOutWhatStartsPrograms();
		programs.AddRange(TakeOutPaths(s.Root));
		if (a_data != null) {
			s.Root["backups"] = Path.Combine(a_data, "Backups");
			s.Root["scratch"] = Path.Combine(a_data, "Scratch");
		}
		var (was, renamed) = (s.Profile, 0);
		if (a_for != null) {
			renamed = NameOutputsFor(s, a_for.Profile);
			s.Root["mo2"] = new JsonObject { ["exe"] = a_for.Mo2Exe, ["instance"] = a_for.Instance, ["profile"] = a_for.Profile };
		}
		Directory.CreateDirectory(a_setups);
		s.Save(dst);
		var lines = new List<string> { "setup written: " + dst };
		if (a_for != null) {
			lines.Add($"it runs your Mod Organizer ({a_for.Mo2Exe}) and the profile {a_for.Profile}");
		}
		if (renamed > 0) {
			lines.Add($"{renamed} output mod name(s) made for the profile \"{was}\" are named after {a_for!.Profile} now");
		}
		if (a_data != null) {
			lines.Add("its backups and its scratch folder are kept where a new setup keeps them: " + a_data);
		}
		if (commands > 0) {
			lines.Add($"its {commands} own command(s) to run before or after a run were taken out - a setup from elsewhere runs no command on this PC; add your own in Settings");
		}
		if (programs.Count > 0) {
			// a setup from elsewhere starts no program the player did not point it at, and allows no add-on for them
			lines.Add("taken out - set your own in Settings: " + string.Join("; ", programs));
		}
		return new(dst, lines);
	}
}
