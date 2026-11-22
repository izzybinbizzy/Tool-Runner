using ToolRunner.Steps;

namespace ToolRunner;

// A player may use the output a tool made before instead of having it built again, and is told, tool by tool, exactly what that
// means (his word of 2026-11-21). A step set so ("useOld" beside its "on") does not run. Its old output goes off with every
// other output before the first tool and comes back on at the step's own place in the order - where a fresh one would be
// switched on - so no tool before it reads it and every tool after it does. What the choice means is worked out each time, never
// only printed: when the output was made, what changed in the profile since, which tools before it are built again in this run,
// and the two things that cannot work (no output there to use, a plugin of it whose master is gone).
public static class OldOutput
{
	public const string Key = "useOld";

	// nothing of their own to keep: the save steps and the sort make no output mod, and the shader caches have their own
	// setting for when they are built again (the setup's "shaderRebuild")
	static readonly HashSet<string> s_never = ["shaders", "cleansave", "playsave", "loot"];

	// a step that builds into an output mod of its own (every one of these has its own words in the table below)
	public static bool MakesOutput(string a_step) => !s_never.Contains(a_step);

	public static bool CanKeep(string a_step, StepSettings a_s) => MakesOutput(a_step) && a_s.OutputMod != null;

	// why a step has no old output to use, for the window (null = it has)
	public static string? WhyNot(string a_step, StepSettings a_s) => a_step switch {
		"shaders" => "The shader caches have their own setting for this: Settings > Rebuild shader caches (when the shader mod changed, every run, never).",
		_ when s_never.Contains(a_step) => $"{StepCatalog.Name(a_step)} makes no output of its own, so there is nothing old to use.",
		_ when a_s.OutputMod == null => $"{StepCatalog.Name(a_step)} has no output mod set (Settings > A step), so there is nothing old to use.",
		_ => null,
	};

	// What an old output of a tool holds, and what a player sees in the game once it no longer fits their mods - written from
	// what each tool's output is (StepCatalog, the tools' own pages named in the readme's credits)
	public sealed record Meaning(string Holds, string WhenOld);

	static readonly Dictionary<string, Meaning> s_meaning = new(StringComparer.Ordinal) {
		["easynpc"] = new("the NPC merge: the looks you picked then, with their face files",
			"An NPC that a mod added or changed since then is not in the merge. Where another plugin now wins that NPC, its face files and its plugin record no longer agree, which shows as a dark or mismatched face."),
		["upf"] = new("zEdit's patch (zPatch.esp), built from your plugins as they were then",
			"Mods added or changed since then were never seen by its patchers and stay unpatched, and what the old patch carries can win over a mod updated since with the values that mod had before."),
		["smash"] = new("the Smashed Patch, which settles the conflicts between your plugins as they were then",
			"Conflicts that came with mods added since are not settled, and the old patch can win over a mod updated since with the values that mod had before."),
		["wryebash"] = new("the Bashed Patch: the merged leveled lists and tweaks of your plugins as they were then",
			"Items, enemies and tweaks of mods added since are missing from the merged lists, and the lists still hold every mod's entries as they were then."),
		["synthesis"] = new("the Synthesis patch, built from your plugins as they were then",
			"A mod added, updated or moved in the load order since then is not in the patch: its records stay unpatched, or the old patch still wins over them with what they held before. A patcher added to Synthesis since then has not run."),
		["reqtificator"] = new("Requiem's own patch (Requiem for the Indifferent.esp), built from your plugins as they were then",
			"Items, creatures and leveled lists of mods added or changed since then are not brought under Requiem's rules: they keep the values their mod ships, or the ones the old patch last saw."),
		["landscapefixer"] = new("one plugin with the snow and blend fixes for the objects and land your mods had then",
			"Objects and places a mod added or changed since then have no fix, and a fix made for an object as it was can win over what a mod changed on it since."),
		["pgpatcher"] = new("the meshes (and its plugins) patched for parallax, complex material and PBR, made for the textures and meshes you had then",
			"A texture or mesh mod added or updated since is not patched, so its surfaces stay flat where parallax or PBR should show; meshes patched for textures that are gone can be shaded wrong; and because the patched copies win over every mod, a mesh a mod updated since is still the old one in the game."),
		["xlodgen"] = new("terrain LOD: the land in the distance, its shape and its ground textures, as your landscape was then",
			"Where a mod changed the land's shape or its ground textures since then, the land far away no longer matches the land up close: it changes color or shape as you walk toward it."),
		["acmos"] = new("the roads painted into the terrain LOD textures, as those textures were then",
			"Its files are whole terrain LOD textures with the roads painted in, and they win over the terrain LOD's own: where the terrain LOD is newer than this, the distance still shows the old ground under the roads."),
		["grass"] = new("the grass cache: where every tuft of grass stands, worked out from your grass mods and your landscape as they were then",
			"Where a mod changed the land or the grass since then, grass floats, sinks into the ground or is missing, and grass kinds added since do not show. The grass LOD that DynDOLOD builds from this cache is as old as the cache."),
		["treelodgen"] = new("the 3D tree LOD models, made from your tree mods as they were then",
			"Trees of a mod added or changed since then keep their old shape in the distance, or have no 3D LOD model at all."),
		["texgen"] = new("the LOD textures and the tree and grass billboards, rendered from your textures as they were then",
			"Where a texture, tree or grass mod changed since then, buildings, trees and grass in the distance still wear the old look and change as you come close."),
		["dyndolod"] = new("the object, tree and grass LOD and DynDOLOD's own plugins, built from your load order as it was then",
			"Where a mod added, moved or removed buildings, trees or objects since then, the distance shows things that are not there up close, misses things that are, or leaves LOD standing in front of the real object. DynDOLOD's plugins also name the plugins they were built from: with one of those gone the game does not start (looked for before every run)."),
		["anvillod"] = new("the object, tree and grass LOD, built from your load order as it was then",
			"Where a mod added, moved or removed buildings, trees or objects since then, the distance no longer matches what stands there up close."),
		["pandora"] = new("the behaviour files for your animation mods as they were then",
			"Animations of a mod added or updated since then do not play (the character stands stiff or uses the default move), and a mod removed since leaves the old files calling for animations that are gone."),
	};

	static readonly Meaning s_any = new("what it made from your mods as they were then", "Whatever changed in your mods since then is not in it.");

	public static Meaning Of(string a_step) => s_meaning.GetValueOrDefault(a_step, s_any);

	// One step that uses its old output, as things stand now. Made: when that output was built (ByRun: a run of this setup on
	// record built it; else read off its files). ChangedSince null = not known. BuiltAgainBefore: the tools this run builds again
	// that the old output was made after. BuiltOnIt: the tools this run runs that read it. MissingMaster: a plugin of it and the
	// master it needs that is nowhere in the load order
	public sealed record Look(string Step, string Name, string Mod, bool There, bool On, DateTime? Made, bool ByRun, IReadOnlyList<(string Mod, string What)>? ChangedSince,
		IReadOnlyList<string> BuiltAgainBefore, IReadOnlyList<string> BuiltOnIt, string? MissingMaster)
	{
		public bool Usable => There && MissingMaster == null;
	}

	// their places in the order table are about the game and its saves, not about an output a tool reads
	static readonly HashSet<string> s_noOutput = ["cleansave", "shaders", "playsave"];

	// every step of the setup that uses its old output (a_only: these ones), looked at for a run of a_todo. Reads only
	public static List<Look> LookAt(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<Setup.StepEntry> a_todo,
		IReadOnlyList<LastRuns.Section>? a_last = null, IReadOnlyList<Setup.StepEntry>? a_only = null)
	{
		var kept = a_only ?? a_setup.KeptSteps;
		var looks = new List<Look>();
		if (kept.Count == 0) {
			return looks;
		}
		var runs = a_todo.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
		HashSet<string>? plugins = null;
		foreach (var k in kept) {
			var mod = new StepSettings(k.Settings).OutputMod!;
			var dir = a_mo2.ModDir(mod);
			var there = HoldsSomething(dir);
			var last = a_last?.FirstOrDefault(s => s.Key == k.Key);
			// a run on record counts only when it built into the very mod the setup names now
			var byRun = there && last is { LastDone.Ended: not null, BuiltElsewhere: null } && (last.LastDone.Output == null || last.LastDone.Output.Equals(mod, StringComparison.OrdinalIgnoreCase));
			string? missing = null;
			if (there) {
				foreach (var p in Plugins.InMod(dir)) {
					List<string> masters;
					try {
						masters = Plugins.Masters(Path.Combine(dir, p));
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						continue;
					}
					plugins ??= PluginsThere(a_setup, a_mo2, a_states, a_todo, kept);
					if (masters.FirstOrDefault(m => !plugins.Contains(m)) is { } gone) {
						missing = $"{p} needs {gone}, which is no longer in your load order";
						break;
					}
				}
			}
			looks.Add(new Look(k.Key, StepCatalog.Name(k.Key), mod, there, a_states.TryGetValue(mod, out var f) && f == '+',
				byRun ? last!.LastDone!.Ended : there ? NewestIn(dir) : null, byRun, byRun ? last!.ChangedSince : null,
				Names(StepCatalog.MustPrecede.Where(r => r.After == k.Key && runs.Contains(r.Before) && !s_noOutput.Contains(r.Before)).Select(r => r.Before)),
				Names(StepCatalog.MustPrecede.Where(r => r.Before == k.Key && runs.Contains(r.After) && !s_noOutput.Contains(r.After)).Select(r => r.After)), missing));
		}
		return looks;
	}

	static List<string> Names(IEnumerable<string> a_keys) => a_keys.Distinct(StringComparer.Ordinal).Select(StepCatalog.Name).ToList();

	// a mod folder with more in it than Mod Organizer's own meta.ini
	static bool HoldsSomething(string a_dir)
	{
		try {
			return Directory.Exists(a_dir) && Directory.EnumerateFileSystemEntries(a_dir).Any(e => !Path.GetFileName(e).Equals("meta.ini", StringComparison.OrdinalIgnoreCase));
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return false;
		}
	}

	// about when a mod's files were last written: the newest of what stands at its top (Mod Organizer rewrites meta.ini itself)
	static DateTime? NewestIn(string a_dir)
	{
		try {
			var times = new DirectoryInfo(a_dir).EnumerateFileSystemInfos().Where(e => !e.Name.Equals("meta.ini", StringComparison.OrdinalIgnoreCase)).Select(e => e.LastWriteTime).ToList();
			return times.Count > 0 ? times.Max() : null;
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
			return null;
		}
	}

	// Every plugin file the game can find once the run has ended: the game's own Data folder, every ticked mod, the old outputs
	// that are used, and what the run builds (as it stands now, and each tool's own plugin names for an output not built yet).
	// By file name only - a master that is there is never called gone because of how it is ticked
	static HashSet<string> PluginsThere(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<Setup.StepEntry> a_todo, IReadOnlyList<Setup.StepEntry> a_kept)
	{
		var there = new HashSet<string>(Plugins.InMod(Path.Combine(a_mo2.GameDir, "Data")), StringComparer.OrdinalIgnoreCase);
		foreach (var (mod, flag) in a_states) {
			if (flag == '+') {
				there.UnionWith(Plugins.InMod(a_mo2.ModDir(mod)));
			}
		}
		foreach (var s in a_todo.Concat(a_kept)) {
			if (new StepSettings(s.Settings).OutputMod is { } om) {
				there.UnionWith(Plugins.InMod(a_mo2.ModDir(om)));
			}
		}
		foreach (var s in a_todo) {
			if (Duplicates.Signatures.TryGetValue(s.Key, out var own)) {
				there.UnionWith(own.Where(Plugins.IsPlugin));
			}
		}
		return there;
	}

	static string When(DateTime a_t) => a_t.ToString("ddd MMM d HH:mm", InvariantCulture);

	// What using this old output means, in plain lines: what it is, when it was made, what changed since, which of this run's
	// tools it no longer fits, and what the player then sees in the game. The window's box, START's preview, the command line
	// and the run's own log all say these same lines
	public static List<string> Warning(Look a_l, string a_profile)
	{
		var m = Of(a_l.Step);
		var lines = new List<string> { $"It holds {m.Holds}." };
		if (!a_l.There) {
			lines.Add($"There is no old output to use: \"{a_l.Mod}\" is missing or holds nothing. A run is refused until it has been built once.");
			return lines;
		}
		lines.Add(a_l.Made is not { } made ? "When it was made is not known."
			: a_l.ByRun ? $"Made {When(made)} by a run of this setup."
			: $"Its files are from about {When(made)} (no run of this setup on record built it).");
		lines.Add(a_l.ChangedSince switch {
			null => "What changed in your mods since then is not known - whatever did is not in it.",
			{ Count: 0 } => $"Nothing changed in the profile {a_profile} since then, as far as Mod Organizer was watched.",
			{ } c => $"{c.Count} change{(c.Count == 1 ? "" : "s")} in the profile {a_profile} since then ({LastRuns.Counted(c)}), e.g. " +
			         string.Join("; ", c.Take(3).Select(x => $"{x.Mod} {x.What}")) + $" - {(c.Count == 1 ? "it is not" : "none of them is")} in it.",
		});
		if (a_l.BuiltAgainBefore.Count > 0) {
			lines.Add($"Built again in this run before its place: {string.Join(", ", a_l.BuiltAgainBefore)}. This old output was made from what " +
			          $"{(a_l.BuiltAgainBefore.Count == 1 ? "that tool" : "those tools")} made before, not from what {(a_l.BuiltAgainBefore.Count == 1 ? "it makes" : "they make")} now.");
		}
		if (a_l.BuiltOnIt.Count > 0) {
			lines.Add($"Built on top of it in this run: {string.Join(", ", a_l.BuiltOnIt)} - {(a_l.BuiltOnIt.Count == 1 ? "it reads" : "they read")} this old output, so what is old in it goes into {(a_l.BuiltOnIt.Count == 1 ? "its" : "their")} new output too.");
		}
		lines.Add("In the game: " + m.WhenOld);
		lines.Add("It was built with the settings its tool had then: a setting you changed in the tool since is not in it.");
		if (!a_l.On) {
			lines.Add("It is switched off in Mod Organizer now: the run switches it on.");
		}
		if (a_l.MissingMaster is { } gone) {
			lines.Add($"It cannot be used as it is: {gone}. The game would not start with it, so a run is refused until it is built again.");
		}
		return lines;
	}

	// the check before a run: an old output that cannot be used stops the start, one that can is a note of the run
	public static IEnumerable<Issue> Check(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<Setup.StepEntry> a_todo)
	{
		foreach (var l in LookAt(a_setup, a_mo2, a_states, a_todo)) {
			if (!l.There) {
				yield return new Issue(l.Step, true, $"it is set to use its old output, and there is none to use: \"{l.Mod}\" is missing or holds nothing - build it once (switch \"Use old output\" off for this step)");
			} else if (l.MissingMaster is { } gone) {
				yield return new Issue(l.Step, true, $"its old output cannot be used: {gone} - the game would not start with it. Build it again (switch \"Use old output\" off for this step)");
			} else {
				yield return new Issue(l.Step, false, $"not run - its old output \"{l.Mod}\" is used, as this setup says" +
					(l.BuiltAgainBefore.Count > 0 ? $"; built again before it in this run: {string.Join(", ", l.BuiltAgainBefore)}" : ""));
			}
		}
	}

	// where each used old output comes back on: the index in a_todo of the first step that stands after it in the setup's own
	// order (a_todo.Count = after the last one)
	public static List<(Setup.StepEntry Step, int At)> Places(Setup a_setup, IReadOnlyList<Setup.StepEntry> a_todo)
	{
		var order = a_setup.Steps.Select(s => s.Key).ToList();
		var places = new List<(Setup.StepEntry, int)>();
		foreach (var k in a_setup.KeptSteps) {
			var mine = order.IndexOf(k.Key);
			var at = 0;
			while (at < a_todo.Count && order.IndexOf(a_todo[at].Key) < mine) {
				++at;
			}
			places.Add((k, at));
		}
		return places;
	}
}
