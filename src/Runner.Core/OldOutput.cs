using ToolRunner.Steps;

namespace ToolRunner;

// A player may use the output a tool made before instead of having it built again, and is told, tool by tool, exactly what that
// means. A step set so ("useOld" beside its "on") does not run. Its old output goes off with every
// other output before the first tool and comes back on at the step's own place in the order - where a fresh one would be
// switched on - so no tool before it reads it and every tool after it does. What the choice means is worked out each time, never
// only printed: when the output was made, what changed in the profile since, which tools before it are built again in this run,
// and the things that cannot work (no output there to use; a plugin of it whose master is gone, or is switched off while a
// tool of the run works after it).
public static class OldOutput
{
	public const string Key = "useOld";

	// nothing of their own to keep: the save steps make no output mod, and the shader caches have their own setting for when they
	// are built again (the setup's "shaderRebuild")
	static readonly HashSet<string> s_never = ["shaders", "cleansave", "playsave"];

	// a step that builds into an output mod of its own (every one of these has its own words in the table below)
	public static bool MakesOutput(string a_step) => !s_never.Contains(a_step);

	public static bool CanKeep(string a_step, StepSettings a_s) => MakesOutput(a_step) && a_s.OutputMod != null;

	// why a step has no old output to use, for the window (null = it has)
	public static string? WhyNot(string a_step, StepSettings a_s) => a_step switch {
		"shaders" => "The shader caches have their own setting for this: Settings > Game steps > Rebuild shader caches (when the shader mod changed, every run, never).",
		_ when s_never.Contains(a_step) => $"{StepCatalog.Name(a_step)} makes no output of its own, so there is nothing old to use.",
		_ when a_s.OutputMod == null => $"{StepCatalog.Name(a_step)} has no output mod set (Settings > Steps), so there is nothing old to use.",
		_ => null,
	};

	// What an old output of a tool holds, and what a player sees in the game once it no longer fits their mods - written from
	// what each tool's output is (StepCatalog, the tools' own pages named in the readme's credits)
	public sealed record Meaning(string Holds, string WhenOld);

	static readonly Dictionary<string, Meaning> s_meaning = new(StringComparer.Ordinal) {
		["synthesis"] = new("the Synthesis patch, built from your plugins as they were then",
			"A mod added, updated or moved in the load order since then is not in the patch: its records stay unpatched, or the old patch still wins over them with what they held before. A patcher added to Synthesis since then has not run."),
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
	// master it needs that the game will not find once the run has ended. OffInRun: a plugin of it and a master that is there
	// again after the run, and off while a tool of this run works after the step's place
	public sealed record Look(string Step, string Name, string Mod, bool There, bool On, DateTime? Made, bool ByRun, IReadOnlyList<(string Mod, string What)>? ChangedSince,
		IReadOnlyList<string> BuiltAgainBefore, IReadOnlyList<string> BuiltOnIt, string? MissingMaster)
	{
		public string? OffInRun { get; init; }

		public bool Usable => There && MissingMaster == null && OffInRun == null;
	}

	// their places in the order table are about the game and its saves, not about an output a tool reads
	static readonly HashSet<string> s_noOutput = ["cleansave", "shaders", "playsave"];

	// Every step of the setup that uses its old output (a_only: these ones), looked at for a run of a_todo. Reads only.
	// a_masters false: its plugins' masters are not judged (a run at the step's place judges them by what is on right
	// then, MasterOffNow - what a run would switch was worked out for the profile as it stood before the run)
	public static List<Look> LookAt(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<Setup.StepEntry> a_todo,
		IReadOnlyList<LastRuns.Section>? a_last = null, IReadOnlyList<Setup.StepEntry>? a_only = null, bool a_masters = true)
	{
		var kept = a_only ?? a_setup.KeptSteps;
		var looks = new List<Look>();
		if (kept.Count == 0) {
			return looks;
		}
		var runs = a_todo.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
		Planned? plan = null;
		foreach (var k in kept) {
			var mod = new StepSettings(k.Settings).OutputMod!;
			var dir = a_mo2.ModDir(mod);
			var there = HoldsSomething(dir);
			var last = a_last?.FirstOrDefault(s => s.Key == k.Key);
			// a run on record counts only when it built into the very mod the setup names now
			var byRun = there && last is { LastDone.Ended: not null, BuiltElsewhere: null } && (last.LastDone.Output == null || last.LastDone.Output.Equals(mod, StringComparison.OrdinalIgnoreCase));
			string? missing = null;
			string? offInRun = null;
			if (there && a_masters) {
				foreach (var p in Plugins.InMod(dir)) {
					List<string> masters;
					try {
						masters = Plugins.Masters(Path.Combine(dir, p));
					} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
						continue;
					}
					if (masters.Count == 0) {
						continue;
					}
					plan ??= new Planned(a_setup, a_mo2, a_states, a_todo);
					if (plan.GoneAfter(masters) is { } gone) {
						missing = $"{p} needs {gone}";
						break;
					}
					if (offInRun == null && plan.OffAt(k.Key, masters) is { } away) {
						offInRun = $"{p} needs {away}";
					}
				}
			}
			looks.Add(new Look(k.Key, StepCatalog.Name(k.Key), mod, there, a_states.TryGetValue(mod, out var f) && f == '+',
				byRun ? last!.LastDone!.Ended : there ? NewestIn(dir) : null, byRun, byRun ? last!.ChangedSince : null,
				Names(StepCatalog.MustPrecede.Where(r => r.After == k.Key && runs.Contains(r.Before) && !s_noOutput.Contains(r.Before)).Select(r => r.Before)),
				Names(StepCatalog.MustPrecede.Where(r => r.Before == k.Key && runs.Contains(r.After) && !s_noOutput.Contains(r.After)).Select(r => r.After)), missing) {
				OffInRun = missing == null ? offInRun : null,
			});
		}
		return looks;
	}

	static List<string> Names(IEnumerable<string> a_keys) => a_keys.Distinct(StringComparer.Ordinal).Select(StepCatalog.Name).ToList();

	// A mod folder that holds a file of its own: more than Mod Organizer's meta.ini, and more than folders with nothing in
	// them (an output whose files were cleared away leaves its folders standing, and was taken for an output)
	static bool HoldsSomething(string a_dir)
	{
		try {
			if (!Directory.Exists(a_dir)) {
				return false;
			}
			var meta = Path.Combine(a_dir, "meta.ini");
			var everyFile = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
			return Directory.EnumerateFiles(a_dir, "*", everyFile).Any(f => !f.Equals(meta, StringComparison.OrdinalIgnoreCase));
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

	// What a run of a_todo does to the plugin files the game and the tools find, worked out once for a look and only as far
	// as it is asked (by file name: a plugin that is there is never called gone because of where it stands in the order).
	// A run switches mods off before its first tool - every output of the setup, the mods a step replaces, and each mod a
	// plugin of which needs one of those - so a master that is ticked now is not always there when it is needed: one in a
	// replaced mod stays off for good, and the others are off while the tools work and come back when the run has ended.
	sealed class Planned
	{
		readonly Setup _setup;
		readonly Mo2Instance _mo2;
		readonly IReadOnlyDictionary<string, char> _states;
		readonly IReadOnlyList<Setup.StepEntry> _todo;
		readonly List<(Setup.StepEntry Step, int At)> _places;
		readonly List<Setup.StepEntry> _kept;
		readonly HashSet<string> _replaced;
		readonly Dictionary<string, List<string>> _inMod = new(StringComparer.OrdinalIgnoreCase);
		List<string>? _data;
		HashSet<string>? _after;
		HashSet<string>? _stay;
		Dictionary<string, string>? _off;

		public Planned(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyList<Setup.StepEntry> a_todo)
		{
			_setup = a_setup;
			_mo2 = a_mo2;
			_states = a_states;
			_todo = a_todo;
			_places = Places(a_setup, a_todo);
			_kept = a_setup.KeptSteps;
			_replaced = a_todo.SelectMany(s => new StepSettings(s.Settings).List("replaces")).ToHashSet(StringComparer.OrdinalIgnoreCase);
		}

		List<string> InMod(string a_mod) => _inMod.TryGetValue(a_mod, out var l) ? l : _inMod[a_mod] = Plugins.InMod(_mo2.ModDir(a_mod));

		// the game's own Data folder, and Mod Organizer's overwrite folder: what lies there is in the game's Data for every
		// tool and for the game, whatever is ticked (the check says so of the files it holds)
		List<string> Data => _data ??= [.. Plugins.InMod(Path.Combine(_mo2.GameDir, "Data")), .. Plugins.InMod(_mo2.OverwriteDir)];

		IEnumerable<string> Ticked => _states.Where(s => s.Value == '+').Select(s => s.Key);

		static string? Output(Setup.StepEntry a_s) => new StepSettings(a_s.Settings).OutputMod;

		// the plugins a step's output holds: as its mod stands now, and the names its tool gives its own plugins (for an
		// output that is not built yet)
		IEnumerable<string> Built(Setup.StepEntry a_s) =>
			(Output(a_s) is { } om ? InMod(om) : []).Concat(Duplicates.Signatures.TryGetValue(a_s.Key, out var own) ? own.Where(Plugins.IsPlugin) : []);

		static bool Holds(IEnumerable<string> a_plugins, string a_plugin) => a_plugins.Contains(a_plugin, StringComparer.OrdinalIgnoreCase);

		// once the run has ended: the game's own Data folder, every ticked mod but the ones a step of this run replaces
		// for good, what the run builds, and every old output that is used
		HashSet<string> After()
		{
			if (_after != null) {
				return _after;
			}
			var there = new HashSet<string>(Data, StringComparer.OrdinalIgnoreCase);
			foreach (var m in Ticked.Where(m => !_replaced.Contains(m))) {
				there.UnionWith(InMod(m));
			}
			foreach (var s in _todo) {
				there.UnionWith(Built(s));
			}
			foreach (var om in _kept.Select(Output).OfType<string>()) {
				there.UnionWith(InMod(om));
			}
			return _after = there;
		}

		// the ticked mods the run switches off before its first tool, each with why
		Dictionary<string, string> Off()
		{
			if (_off != null) {
				return _off;
			}
			var off = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var (m, why) in Runner.OutputsToTurnOff(_setup, _states, _todo)) {
				off[m] = why;
			}
			foreach (var (m, why) in Profile.DependentsOf(_mo2, _states, [.. off.Keys])) {
				off.TryAdd(m, why);
			}
			return _off = off;
		}

		// for the whole run: the game's own, and those of every ticked mod the run leaves on
		HashSet<string> Stay()
		{
			if (_stay != null) {
				return _stay;
			}
			var off = Off();
			var there = new HashSet<string>(Data, StringComparer.OrdinalIgnoreCase);
			foreach (var m in Ticked.Where(m => !off.ContainsKey(m))) {
				there.UnionWith(InMod(m));
			}
			return _stay = there;
		}

		// the first of a_masters that the game does not find once the run has ended, in words (null: each one is there)
		public string? GoneAfter(IReadOnlyList<string> a_masters)
		{
			var after = After();
			if (a_masters.FirstOrDefault(m => !after.Contains(m)) is not { } gone) {
				return null;
			}
			var replaced = Ticked.FirstOrDefault(m => _replaced.Contains(m) && Holds(InMod(m), gone));
			return replaced == null ? $"{gone}, which is no longer in your load order" : $"{gone}, which is in \"{replaced}\" - a mod this run replaces and leaves switched off";
		}

		// The first of a_masters that is off while a tool of this run works after the place of a_kept, in words: that tool
		// would find the old output's plugin with a master missing. Null: each one is there by then - or no tool runs after
		// that place (the save to play starts the game once everything is back on)
		public string? OffAt(string a_kept, IReadOnlyList<string> a_masters)
		{
			var at = _places.Where(p => p.Step.Key == a_kept).Select(p => (int?)p.At).FirstOrDefault() ?? _todo.Count;
			if (!_todo.Skip(at).Any(s => s.Key != "playsave")) {
				return null;
			}
			var there = new HashSet<string>(Stay(), StringComparer.OrdinalIgnoreCase);
			foreach (var s in _todo.Take(at)) {
				there.UnionWith(Built(s));
			}
			// Every old output that comes back on before the same tool is on by the time that tool runs - the ones listed
			// after this one too: no tool works between them, and the run switches them on together, each one that another
			// needs first (InMasterOrder)
			foreach (var (k, _) in _places.Where(p => p.At <= at)) {
				if (Output(k) is { } om) {
					there.UnionWith(InMod(om));
				}
			}
			if (a_masters.FirstOrDefault(m => !there.Contains(m)) is not { } away) {
				return null;
			}
			if (_todo.Skip(at).FirstOrDefault(s => Holds(Built(s), away)) is { } later) {
				return $"{away}: it is built again only later in this run, by {StepCatalog.Name(later.Key)}";
			}
			if (_places.Where(p => p.At > at && Output(p.Step) is { } om && Holds(InMod(om), away)).Select(p => p.Step).FirstOrDefault() is { } after) {
				return $"{away}: it is in the old output of {StepCatalog.Name(after.Key)}, which comes back on only later in the order";
			}
			var off = Off().FirstOrDefault(o => Holds(InMod(o.Key), away));
			return off.Key == null ? $"{away}: it is switched off while the run's tools work" : $"{away}: it is in \"{off.Key}\", which is switched off while the run's tools work ({off.Value})";
		}
	}

	// At an old output's place in a run, with the run's own switches made (a_states: the profile as it stands at this
	// moment): a plugin of it and a master of that plugin that is not switched on now, in words - the tool that runs next
	// would find the plugin with a master missing. Null: every master is on
	// a_active: the plugins ticked in the profile's plugin list as it is now. a_with: the other old outputs that come on at the
	// same place (they are on, their plugins ticked, before the next tool runs)
	public static string? MasterOffNow(Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyCollection<string> a_active, string a_mod, IEnumerable<string>? a_with = null) =>
		MasterNotOn(a_mo2, a_states, a_active, a_mod, a_with) is { } m ? $"{m.Plugin} needs {m.Master}, which is not switched on at this point of the run" : null;

	// The same as its two parts: a plugin of the mod, and the master of it that the game does not load. A master is loaded when
	// it is in the game's own Data folder (the game's own plugins load with no line in the plugin list), or when a switched-on
	// mod or Mod Organizer's overwrite folder holds it AND it is ticked in the plugin list (a_active) - a mod switched on with
	// that plugin unticked loads no master - or when the switch at hand ticks it: a_with, whose plugins are ticked together with
	// it, and the mod itself when a_ticksItself. At an old output's place the switch ticks every plugin of the mod; a put-back
	// that finds the mod switched on already lays its ticks back as they were and ticks none of them, so there a master in the
	// same mod counts only while its plugin is ticked. Null: every master is loaded
	public static (string Plugin, string Master)? MasterNotOn(Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_states, IReadOnlyCollection<string> a_active, string a_mod,
		IEnumerable<string>? a_with = null, bool a_ticksItself = true)
	{
		HashSet<string>? on = null;
		var ticked = new HashSet<string>(a_active, StringComparer.OrdinalIgnoreCase);
		foreach (var (p, masters) in MastersIn(a_mo2, a_mod)) {
			// a plugin of the mod that stays unticked loads nothing and needs nothing: only one the profile will load is judged
			// (where the switch at hand ticks none of the mod's plugins, that is one ticked now)
			if (!a_ticksItself && !ticked.Contains(p)) {
				continue;
			}
			if (on == null) {
				on = new HashSet<string>(Plugins.InMod(Path.Combine(a_mo2.GameDir, "Data")), StringComparer.OrdinalIgnoreCase);
				on.UnionWith(Plugins.InMod(a_mo2.OverwriteDir).Where(ticked.Contains));
				foreach (var m in a_states.Where(s => s.Value == '+').Select(s => s.Key)) {
					on.UnionWith(Plugins.InMod(a_mo2.ModDir(m)).Where(ticked.Contains));
				}
				foreach (var m in a_ticksItself ? (a_with ?? []).Append(a_mod) : a_with ?? []) {
					on.UnionWith(Plugins.InMod(a_mo2.ModDir(m)));
				}
			}
			if (masters.FirstOrDefault(m => !on.Contains(m)) is { } away) {
				return (p, away);
			}
		}
		return null;
	}

	// In a run, before an old output is switched on: a plugin of it and a master the game will not find once the run has
	// ended and its profile is back, in words. a_before: the mod states the run found as it began, after the setup's own
	// commands before a run - what its put-back restores (the check before the run looked earlier, and such a command may
	// have switched the master's mod off since). Null: every master is there by then
	public static string? MasterGoneAfterRun(Setup a_setup, Mo2Instance a_mo2, IReadOnlyDictionary<string, char> a_before, IReadOnlyList<Setup.StepEntry> a_todo, string a_mod)
	{
		Planned? plan = null;
		foreach (var (p, masters) in MastersIn(a_mo2, a_mod)) {
			plan ??= new Planned(a_setup, a_mo2, a_before, a_todo);
			if (plan.GoneAfter(masters) is { } gone) {
				return $"{p} needs {gone}";
			}
		}
		return null;
	}

	// each plugin of a mod that names masters, with them (one that cannot be read is passed over, as the look passes it)
	static IEnumerable<(string Plugin, List<string> Masters)> MastersIn(Mo2Instance a_mo2, string a_mod)
	{
		var dir = a_mo2.ModDir(a_mod);
		foreach (var p in Plugins.InMod(dir)) {
			List<string> masters;
			try {
				masters = Plugins.Masters(Path.Combine(dir, p));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				continue;
			}
			if (masters.Count > 0) {
				yield return (p, masters);
			}
		}
	}

	// The old outputs that come on at one place of a run, each one that another of them needs before that other: a plugin
	// the order files do not hold yet gets its place as its mod is switched on, and a master stands before what needs it.
	// Two that need each other, and the ones that need none of the others, keep the order they were given in
	public static List<Setup.StepEntry> InMasterOrder(Mo2Instance a_mo2, IReadOnlyList<Setup.StepEntry> a_due) =>
		[.. MasterOrder(a_mo2, [.. a_due.Select(s => new StepSettings(s.Settings).OutputMod ?? "")]).Select(i => a_due[i])];

	// the same for mods by name (a put-back switches what a run built and used on in this order)
	public static List<string> ModsInMasterOrder(Mo2Instance a_mo2, IReadOnlyList<string> a_mods) => [.. MasterOrder(a_mo2, a_mods).Select(i => a_mods[i])];

	static List<int> MasterOrder(Mo2Instance a_mo2, IReadOnlyList<string> a_mods)
	{
		var left = Enumerable.Range(0, a_mods.Count).ToList();
		if (a_mods.Count < 2) {
			return left;
		}
		var has = a_mods.Select(m => (m == "" ? [] : Plugins.InMod(a_mo2.ModDir(m))).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
		var needs = a_mods.Select(m => (m == "" ? [] : MastersIn(a_mo2, m).SelectMany(x => x.Masters)).ToHashSet(StringComparer.OrdinalIgnoreCase)).ToList();
		var order = new List<int>();
		while (left.Count > 0) {
			var free = left.Where(i => !left.Any(o => o != i && needs[i].Overlaps(has[o]))).ToList();
			var next = free.Count > 0 ? free[0] : left[0];
			order.Add(next);
			left.Remove(next);
		}
		return order;
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
		if (a_l.OffInRun is { } away) {
			lines.Add($"It cannot be used in this run: {away}. A tool that runs after its place would find that plugin with a master missing, so the run is refused " +
			          "until it is built again, or until the tools after it are left out of the run.");
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
			} else if (l.OffInRun is { } away) {
				yield return new Issue(l.Step, true, $"its old output cannot be used in this run: {away} - a tool that runs after it would find that plugin with a master missing. " +
					"Build it again (switch \"Use old output\" off for this step), or leave the tools after it out of this run");
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
