using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// The settings window: the setup's own settings and each step's, in plain names; edited on a copy, checked, saved with a
// backup (MainViewModel.SaveSetup) - never a hand-edited file.
public static class SettingsPanel
{
	static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal) {
		["executable"] = "Its entry in MO2's Executables list",
		["image"] = "Its program file name",
		["output_mod"] = "Output mod",
		["then_move_to_mod"] = "Output mod",
		["output_dir"] = "The tool's own output folder",
		["deadline_min"] = "Give up after (minutes)",
		["enable_during"] = "Mods switched ON while it runs (one per line)",
		["disable_during"] = "Mods switched OFF while it runs (one per line)",
		["replaces"] = "Mods it replaces - kept off after (one per line)",
		["worldspaces"] = "Worldspaces (all, or one per line)",
		["args"] = "Extra arguments",
		["settings"] = "PGPatcher settings set (a file beside the setup)",
		["pglite_check"] = "Stop when PGLite's tick and PGLite Mode disagree",
		["launch_executable"] = "Game launcher (its MO2 entry)",
		["interior_cell"] = "Cell the clean save is made in (QASmoke)",
		["dyndolod_wait_s"] = "Wait after DynDOLOD goes off (seconds)",
		["dyndolod_off"] = "Switch DynDOLOD off in game first",
		["save"] = "Save to start from (empty = the newest)",
		["start_min"] = "Game start limit (minutes)",
		["wait_s"] = "Wait after a save loads (seconds)",
		["by"] = "How (fasterngio or game)",
		["fasterngio_exe"] = "FasterNGIO's program",
		["fasterngio_args"] = "FasterNGIO's arguments",
		["exe"] = "Program",
		["dyndolod"] = "DynDOLOD's folder",
		["preset"] = "DynDOLOD preset file",
		["no_terrain_underside"] = "Never make a terrain underside",
		["reads"] = "The terrain LOD folder it paints",
		["tool_writes"] = "The mod the tool writes into",
		["min_files"] = "Fewest files that counts as a result",
		["hidden"] = "Hidden (the tool runs where it never shows on your screen)",
		["caches"] = "Shader caches (each: its shader mod, its cache mod)",
		["settle_s"] = "Wait before closing (seconds)",
		["quiet_min"] = "Done when quiet for (minutes), when the game's processor use cannot be read",
		["idle_s"] = "Done when the game has gone quiet for (seconds)",
		["build_for_off"] = "Also build the cache of a shader mod that is switched off",
		["on_crash"] = "When the game crashes while compiling: ask, restart or stop",
		["crash_wait_min"] = "Wait for your answer after a crash (minutes)",
		["max_restarts"] = "Restarts allowed",
		["build_min"] = "Patcher build limit (minutes)",
		["extra_args"] = "Extra arguments (one per line)",
		["stall_min"] = "Give up when nothing moves for (minutes)",
		["compile_watch_s"] = "Watch for a shader compile before a save is loaded (seconds)",
		["compile_max_min"] = "Longest shader compile waited for before a save is loaded (minutes)",
		["sanity_min"] = "Stop when the shader mod shows no sign of running for (minutes)",
		["first_start_min"] = "A first game start that ends sooner counts as a fault (minutes)",
		["max_idle_restarts"] = "Restarts in a row allowed that add no grass file",
		["max_offers"] = "Times Faster Archives may close the game for its offer",
		["offer_wait_min"] = "Wait for your choice in Faster Archives' window (minutes)",
		["start_buttons"] = "Words on its start button (one per line)",
		["skse_dll"] = "AnvilLOD's --skse-dll value",
		["settings_dirs"] = "More folders its settings are kept in (one per line)",
		["nexus_id"] = "Its Nexus page number (for the update check)",
		["live_settings"] = "PGPatcher's own settings file",
		["pgpatcher_mod"] = "The mod PGPatcher is installed in",
		["exe_path"] = "Synthesis's program file",
		["roads"] = "Road set (the name ACMOS gives it)",
		["stuck_min"] = "Start the game again when no grass file is added for (minutes)",
	};

	// every label the step editor can show for a known setting (a shipped language must hold each)
	public static IReadOnlyCollection<string> StepLabels => [.. Labels.Values.Append(AnvilPresetLabel).Append(ArriveLabel).Append(NoCleanSaveLabel).Distinct(StringComparer.Ordinal)];

	// an edit not saved yet (the window asks before Settings closes over it)
	public static bool HasChanges { get; private set; }

	static void Changed() => HasChanges = true;

	// The setup's file the settings shown were opened for (null: none). The window stays open while another setup is picked
	// in the main window: what it shows is then another setup's than the one picked.
	static string? s_openedFor;

	public static bool IsFor(Setup? a_setup) => string.Equals(s_openedFor, a_setup?.File, StringComparison.OrdinalIgnoreCase);

	// Why settings opened for one setup are not saved while another is picked (null: they were opened for the picked one).
	// Saved all the same, they were written over the picked setup's own.
	public static string? NotThisSetup(string a_openedFor, string a_openedName, Setup? a_picked) =>
		MainViewModel.StillShown(a_openedFor, a_picked?.File) ? null
			: $"Not saved: these are the settings of \"{a_openedName}\", and {(a_picked == null ? "no setup" : $"\"{a_picked.Name}\"")} is picked in the main window now. " +
			  $"Pick \"{a_openedName}\" again to save them (what you typed stays here), or press Undo changes to see the picked setup's.";

	const string StepsOwn = "the step's own";

	// every game start of a step: how often Faster Archives may close the game for its offer, and how long its window may wait
	static readonly (string Key, JsonValueKind Kind, string Default)[] OfferOfFasterArchives = [("max_offers", JsonValueKind.Number, "3"), ("offer_wait_min", JsonValueKind.Number, "30")];

	// a game start that loads a save: the start's limit and the watch for a shader compile before the load is sent
	static readonly (string Key, JsonValueKind Kind, string Default)[] BeforeASaveLoads = [("start_min", JsonValueKind.Number, "8"), ("compile_watch_s", JsonValueKind.Number, "300"), ("compile_max_min", JsonValueKind.Number, "90")];

	static readonly (string Key, JsonValueKind Kind, string Default)[] ModsWhileItRuns = [("enable_during", JsonValueKind.Array, "none"), ("disable_during", JsonValueKind.Array, "none")];

	// settings a step reads that its file may not list yet (shown empty, with the default the step uses): each one a step
	// reads and a new setup does not write stands here, so none can be reached only by editing the setup's file by hand
	static readonly Dictionary<string, (string Key, JsonValueKind Kind, string Default)[]> Known = new(StringComparer.Ordinal) {
		["shaders"] = [("build_for_off", JsonValueKind.False, "off"), ("on_crash", JsonValueKind.String, "ask"), ("crash_wait_min", JsonValueKind.Number, "30"),
			("idle_s", JsonValueKind.Number, "45"), ("quiet_min", JsonValueKind.Number, "5"), ("min_files", JsonValueKind.Number, "50"), ("max_restarts", JsonValueKind.Number, "3"),
			("sanity_min", JsonValueKind.Number, "3"), ("settle_s", JsonValueKind.Number, "30"), .. ModsWhileItRuns, .. OfferOfFasterArchives],
		["cleansave"] = [("settle_s", JsonValueKind.Number, "10"), .. BeforeASaveLoads, .. OfferOfFasterArchives],
		["playsave"] = [("save", JsonValueKind.String, "none"), ("wait_s", JsonValueKind.Number, "60"), ("dyndolod_wait_s", JsonValueKind.Number, "30"), ("settle_s", JsonValueKind.Number, "10"), .. BeforeASaveLoads, .. OfferOfFasterArchives],
		["grass"] = [("stall_min", JsonValueKind.Number, "10"), ("min_files", JsonValueKind.Number, "100"), ("fasterngio_args", JsonValueKind.String, "--world all"),
			("first_start_min", JsonValueKind.Number, "5"), ("stuck_min", JsonValueKind.Number, "20"), ("max_restarts", JsonValueKind.Number, "15"), ("max_idle_restarts", JsonValueKind.Number, "3"),
			("disable_during", JsonValueKind.Array, "none"), .. OfferOfFasterArchives],
		["pgpatcher"] = [("pglite_check", JsonValueKind.False, "off"), ("settings", JsonValueKind.String, "none"), ("live_settings", JsonValueKind.String, StepsOwn), ("pgpatcher_mod", JsonValueKind.String, StepsOwn),
			("args", JsonValueKind.String, "--autostart"), ("disable_during", JsonValueKind.Array, "none")],
		["xlodgen"] = [("min_files", JsonValueKind.Number, "100"), ("args", JsonValueKind.String, StepsOwn), .. ModsWhileItRuns],
		["acmos"] = [("hidden", JsonValueKind.True, "on"), ("min_files", JsonValueKind.Number, "100"), ("roads", JsonValueKind.String, StepsOwn), ("exe", JsonValueKind.String, StepsOwn)],
		["treelodgen"] = [("preset", JsonValueKind.String, "none"), ("extra_args", JsonValueKind.Array, "none"), .. ModsWhileItRuns],
		["texgen"] = [("min_files", JsonValueKind.Number, "1"), ("start_buttons", JsonValueKind.Array, StepsOwn), ("args", JsonValueKind.String, StepsOwn), .. ModsWhileItRuns],
		["dyndolod"] = [("no_terrain_underside", JsonValueKind.False, "off"), ("preset", JsonValueKind.String, "none"), ("min_files", JsonValueKind.Number, "1"),
			("start_buttons", JsonValueKind.Array, StepsOwn), ("args", JsonValueKind.String, StepsOwn), .. ModsWhileItRuns],
		["anvillod"] = [("preset", JsonValueKind.String, "high"), ("skse_dll", JsonValueKind.String, "1170"), ("min_files", JsonValueKind.Number, "50"), ("extra_args", JsonValueKind.Array, "none"), .. ModsWhileItRuns],
		["landscapefixer"] = [("min_files", JsonValueKind.Number, "3"), ("args", JsonValueKind.String, "none"), .. ModsWhileItRuns],
		["pandora"] = [("args", JsonValueKind.String, "--auto_run --auto_close"), .. ModsWhileItRuns],
		["synthesis"] = [("build_min", JsonValueKind.Number, "15"), ("exe_path", JsonValueKind.String, StepsOwn), ("disable_during", JsonValueKind.Array, "none")],
	};

	// What a step's page offers beyond what its file lists: the step's own (Known), then what every step with a tool, an
	// output or a time limit of its own reads. The two save steps have no time limit of the whole step (their waits are
	// their own settings), so none is offered there
	static IEnumerable<(string Key, JsonValueKind Kind, string Default)> Offered(string a_step, JsonObject a_settings)
	{
		foreach (var k in Known.GetValueOrDefault(a_step, [])) {
			yield return k;
		}
		if (a_settings.ContainsKey("executable")) {
			yield return ("image", JsonValueKind.String, StepsOwn);
			yield return ("settings_dirs", JsonValueKind.Array, "none");
		}
		if (a_settings.ContainsKey("output_mod") || a_settings.ContainsKey("then_move_to_mod")) {
			yield return ("replaces", JsonValueKind.Array, "none");
		}
		if (ToolUpdates.Known.ContainsKey(a_step) || a_settings.ContainsKey("executable") || a_settings.ContainsKey("exe")) {
			yield return ("nexus_id", JsonValueKind.String, ToolUpdates.Known.ContainsKey(a_step) ? "the tool's own page" : "none");
		}
		if (!Setup.SaveSteps.Contains(a_step)) {
			yield return ("deadline_min", JsonValueKind.Number, StepsOwn);
		}
	}

	// every setting a_step's page can offer when the step's file does not hold it
	public static IReadOnlyCollection<string> OfferedKeys(string a_step) =>
		[.. Offered(a_step, new JsonObject { ["executable"] = "", ["output_mod"] = "" }).Select(k => k.Key)];

	// the label a_step's page shows for a setting
	public static string LabelOf(string a_key, string a_step) => Label(a_key, a_step);

	// the mods a LOD step builds from and the LOD tool's own help, each a page to open (the runner never guesses their values)
	static void LodBlock(MainWindow a_w, Setup a_setup, string a_step, Panel a_host)
	{
		a_host.Children.Add(new TextBlock { Text = WindowWords.Word("Before you set it up: LOD can come out too bright, too dark or missing when its settings do not match the mods it is " +
			"built from. Each of those mods' own pages (and the LOD tool's help) says what it needs - the runner never guesses another author's values."),
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
		static Button Link(MainWindow a_win, string a_text, string a_url)
		{
			var b = new Button { Content = a_text, Style = (Style)a_win.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = a_url };
			b.Click += (_, _) => Opened(a_win, a_url);
			return b;
		}
		var docs = new WrapPanel { Margin = new Thickness(-10, 0, 0, 6) };
		a_host.Children.Add(docs);
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) }.Live(TextBlock.ForegroundProperty, "Warn");
		a_host.Children.Add(note);
		// the setup's own pages (a Community Shaders setup has one more) - read off the profile, so not on the window's thread
		_ = Task.Run(() => {
			try {
				var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
				return (LodGuide.DocsFor(a_setup, mo2, a_step), LodGuide.SetupNote(a_setup, mo2, a_step));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				return (LodGuide.Docs.GetValueOrDefault(a_step, []).ToList(), (string?)null);
			}
		}).ContinueWith(t => {
			// a read that failed some other way leaves the block without its pages (never an error box over the settings)
			if (!t.IsCompletedSuccessfully) {
				return;
			}
			// (added after the page was put into the window's language: each said in it here)
			foreach (var (title, url) in t.Result.Item1) {
				docs.Children.Add(Link(a_w, WindowWords.Word(title), url));
			}
			note.Text = WindowWords.Word(t.Result.Item2 ?? "");
			note.Visibility = t.Result.Item2 == null ? Visibility.Collapsed : Visibility.Visible;
		}, TaskScheduler.FromCurrentSynchronizationContext());
		var rows = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
		var find = new Button { Content = "Find the mods it is built from", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
		find.Click += async (_, _) => {
			find.IsEnabled = false;
			rows.Children.Clear();
			rows.Children.Add(new TextBlock { Text = "Reading the profile's plugins ...", Style = (Style)a_w.FindResource("MutedText") });
			try {
				var hits = await Task.Run(() => LodGuide.Find(a_setup, Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance), a_step));
				rows.Children.Clear();
				rows.Children.Add(new TextBlock { Text = hits.Count == 0 ? WindowWords.Word("No ticked mod changes what it builds from.") :
					WindowWords.F($"{hits.Count} ticked mod(s) change what it builds from - check each one's page for its LOD settings:"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
				foreach (var h in hits) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (h.Page != null) {
						var open = Link(a_w, WindowWords.Word("Open its page"), h.Page);
						DockPanel.SetDock(open, Dock.Right);
						row.Children.Add(open);
					}
					var what = string.Join(LodGuide.KindsJoin, h.What.Split(LodGuide.KindsJoin).Select(WindowWords.Word));
					row.Children.Add(new TextBlock { Text = $"{h.Mod} ({what})" + (h.Page == null ? WindowWords.Word(" - no Nexus page recorded") : ""), TextWrapping = TextWrapping.Wrap,
						VerticalAlignment = VerticalAlignment.Center });
					rows.Children.Add(row);
				}
				// (built after the window loaded: into its language like the rest of it)
				WindowWords.Tree(rows);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				rows.Children.Clear();
				rows.Children.Add(new TextBlock { Text = WindowWords.Format("Could not read the profile: {0}", e.Message), TextWrapping = TextWrapping.Wrap });
			} finally {
				find.IsEnabled = true;
			}
		};
		a_host.Children.Add(find);
		a_host.Children.Add(rows);
	}

	// how much room this setup's run backups take, and a clean-up to the newest few without waiting for a run
	static void BackupsBlock(MainWindow a_w, MainViewModel a_vm, Panel a_host)
	{
		var setup = a_vm.Setup!;
		var say = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
			Text = "Measuring this setup's run backups ..." };
		var tidy = new Button { Content = "Keep only the newest now", Style = (Style)a_w.FindResource("Ghost"), IsEnabled = false };
		var open = new Button { Content = "Open the backups folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		open.Click += (_, _) => {
			if (MainViewModel.OpenFolder(setup.BackupDir) is { } why) {
				a_w.Say("The backups folder did not open: " + why);
			}
		};
		static string Gb(long a_b) => a_b >= 1L << 30 ? $"{a_b / (double)(1L << 30):0.0} GB" : $"{a_b / (double)(1L << 20):0} MB";
		async void Measure()
		{
			var all = await Task.Run(() => {
				try {
					return Runner.BackupsOf(setup, AppPaths.Runs);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return [];
				}
			});
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			say.Text = all.Count == 0 ? WindowWords.Word("This setup has no run backups yet.") :
				WindowWords.F($"This setup's run backups: {all.Count} run(s), {Gb(all.Sum(b => b.Bytes))} in all - ") +
				(keep > 0 ? WindowWords.F($"the newest {keep} are kept after each run.") : WindowWords.Word("every one is kept (Backups to keep is 0)."));
			tidy.IsEnabled = keep > 0 && all.Count > keep;
			tidy.ToolTip = WindowWords.F($"Removes all but the newest {keep} now (the number saved above); a run's own backup and one an unfinished run needs are never touched");
		}
		tidy.Click += async (_, _) => {
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			if (!Ask.Show(a_w, "Remove old backups", WindowWords.F($"All but the newest {keep} of this setup's run backups are deleted for good (they are copies of outputs and settings from runs that finished long ago). A backup an unfinished run still needs is kept."),
			        "Remove them", "Keep them")) {
				return;
			}
			tidy.IsEnabled = false;
			using var hand = a_w.Working(WindowWords.Word("old backups are being removed"));
			try {
				var removed = await Task.Run(() => {
					// one at a time with runs and put-backs (the run lock, taken and given back on this thread)
					using var hold = Runner.HoldIfFree();
					return hold == null ? null : Runner.PruneBackupsOf(setup, AppPaths.Runs, null, keep, RunLog.Null);
				});
				a_vm.Say(removed == null ? "Not removed: a run or a put-back is going - try again when it is done." : $"{removed.Count} old backup(s) removed.", removed == null ? "bad" : "good");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				// a backup folder that went away or locked part way: said, and the button can be pressed again
				a_vm.Say("The old backups could not all be looked at: " + e.Message, "bad");
			} finally {
				tidy.IsEnabled = true;
			}
			Measure();
		};
		var row = new WrapPanel { Margin = new Thickness(230, -4, 0, 12) };
		row.Children.Add(say);
		row.Children.Add(tidy);
		row.Children.Add(open);
		a_host.Children.Add(row);
		Measure();
	}

	// the page that explains a step's tool: its download page (the author's description and instructions), or for the save steps
	// DynDOLOD's own help on the clean save
	public static (string Url, string What)? HelpPage(string a_step) =>
		a_step is "cleansave" or "playsave" ? ("https://dyndolod.info/Help/Clean-Save", "DynDOLOD's help on the clean save") :
		ToolUpdates.Known.TryGetValue(a_step, out var src) ? (src.Page.Replace("?tab=files", "", StringComparison.Ordinal), "the tool's own page") : null;

	// a page in the player's browser; when it does not open, the main window says why
	static void Opened(MainWindow a_w, string a_url)
	{
		if (MainViewModel.OpenPage(a_url) is { } why) {
			a_w.Say("The page did not open: " + why);
		}
	}

	// one line of a Windows file picker's filter: its name in the window's language (a "|" in a translation would end the name
	// there and read as the pattern), then the pattern
	static string FilterOf(string a_name, string a_pattern) => WindowWords.Word(a_name).Replace("|", "", StringComparison.Ordinal) + "|" + a_pattern;

	// (a setting saved under an empty key still gets a name: its editor is announced by it)
	// AnvilLOD's "preset" is a word (its own low / medium / high), DynDOLOD's a file
	const string AnvilPresetLabel = "AnvilLOD preset";

	// (in the two save steps the wait is the one after the player was moved, not one before the game closes)
	const string ArriveLabel = "Wait after the teleport (seconds)";

	// (the final save starts from the run's own clean save; the save a setup names is only for a run that made none)
	const string NoCleanSaveLabel = "Save to start from when the run made no clean save";

	static string Label(string a_key, string a_step) => a_step == "anvillod" && a_key == "preset" ? AnvilPresetLabel :
		a_key == "settle_s" && Setup.SaveSteps.Contains(a_step) ? ArriveLabel :
		a_key == "save" && a_step == "playsave" ? NoCleanSaveLabel : Label(a_key);

	static string Label(string a_key) => Labels.TryGetValue(a_key, out var l) ? l : a_key.Length == 0 ? "Unnamed setting" : char.ToUpperInvariant(a_key[0]) + a_key[1..].Replace('_', ' ');

	// the program's own new versions - kept for the whole program, saved the moment it is ticked
	static void NewVersions(MainWindow a_w, StackPanel a_stack)
	{
		a_stack.Children.Add(new TextBlock { Text = "UPDATES", Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });
		var newVersions = new CheckBox { Content = "Tell me when a new version is out", IsChecked = !ProgramUpdate.IsOff(AppPaths.Data), Margin = new Thickness(0, 0, 0, 4) };
		newVersions.Click += (_, _) => ProgramUpdate.SetOff(AppPaths.Data, newVersions.IsChecked != true);
		a_stack.Children.Add(newVersions);
		a_stack.Children.Add(new TextBlock { Text = WindowWords.Word("At most once a day, when the window opens, it asks its own download page which version is newest - nothing else is sent, " +
			"and nothing is downloaded: the page opens in your browser when you choose."), Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap });
	}

	// Which drawing of the settings is the one on screen: counted on with every drawing, and when the settings window closes.
	// A save that ends later goes on only for the drawing it was started from (a setup's file alone does not tell: the
	// window may have been closed and opened again for the same setup, with new edits in it)
	static int s_drawing;

	public static FrameworkElement Build(MainWindow a_w, MainViewModel a_vm)
	{
		var drawing = ++s_drawing;
		s_openedFor = a_vm.Setup?.File;
		s_errors = null;
		if (a_vm.Setup == null) {
			// the program's own new-version look happens with no setup too, so its switch is here as well
			var none = new StackPanel();
			none.Children.Add(new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap });
			NewVersions(a_w, none);
			return none;
		}
		// whose settings these are, for as long as this panel stands (Save writes to no other setup)
		var (openedFor, openedName) = (a_vm.Setup.File, a_vm.Setup.Name);
		var work = (JsonObject)a_vm.Setup.Root.DeepClone();
		// the setup's steps, and which of them is open on the Steps page (-1 = none) - the search names the other steps that hold its word
		var steps = (work["steps"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
		var shown = -1;
		HasChanges = false;
		// The settings in sub-menus. Each page is a panel of its own; the list on the left shows one at
		// a time, and the search shows the matching rows of every page at once. `stack` is the page the rows below go into.
		var pages = new List<(string Name, StackPanel Panel)>();
		StackPanel Page(string a_name)
		{
			var p = new StackPanel();
			p.Children.Add(new TextBlock { Text = a_name.ToUpperInvariant(), Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 0, 0, 10), Tag = HeadTag });
			pages.Add((a_name, p));
			return p;
		}
		StackPanel setupPage = Page("Setup"), runPage = Page("Run"), gamePage = Page("Game steps"), backupPage = Page("Backups"), alertPage = Page("Alerts"),
			stepPage = Page("Steps"), toolPage = Page("Tools"), changesPage = Page("MO2 Changelog"), programPage = Page("Program");
		// (the pages are shown in the window's language and known by their English names)
		var cats = new ListBox { Width = 168, Margin = new Thickness(0, 0, 16, 0), ItemsSource = pages.Select(p => WindowWords.Word(p.Name)).ToList(), SelectedIndex = s_page < pages.Count ? s_page : 0,
			VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
		System.Windows.Automation.AutomationProperties.SetName(cats, "Settings pages");
		s_cats = cats;
		s_catNames = [.. pages.Select(p => p.Name)];
		var host = new StackPanel();
		var scroller = new ScrollViewer { Content = host, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(0, 0, 12, 0) };
		System.Windows.Automation.AutomationProperties.SetName(scroller, "The settings of the page picked");
		// with nothing searched for: the picked page alone; with a word: every page that still shows a row
		void ShowPages(string a_q)
		{
			for (var i = 0; i < pages.Count; ++i) {
				var p = pages[i].Panel;
				var any = p.Children.OfType<FrameworkElement>().Any(e => e.Visibility == Visibility.Visible && e is not TextBlock { Tag: HeadTag });
				p.Visibility = (a_q == "" ? i == cats.SelectedIndex : any) ? Visibility.Visible : Visibility.Collapsed;
			}
		}
		int FilterAll(string a_q)
		{
			var hits = pages.Sum(p => Filter(p.Panel, a_q));
			ShowPages(a_q);
			return hits;
		}
		var stack = setupPage;
		var errors = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) }.Live(TextBlock.ForegroundProperty, "Bad");
		s_errors = errors;
		var apply = new List<Func<string?>>();

		void Head(string a_t) => stack.Children.Add(new TextBlock { Text = a_t, Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });

		// a search over every setting (Ctrl+F while Settings is open)
		var find = new TextBox { Margin = new Thickness(0, 0, 0, 4), ToolTip = "Ctrl+F" };
		System.Windows.Automation.AutomationProperties.SetName(find, "Find a setting");
		System.Windows.Automation.AutomationProperties.SetAcceleratorKey(find, "Ctrl+F");
		var findHint = new TextBlock { Text = "Find a setting - type a word (Ctrl+F)", Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 6) };
		find.TextChanged += (_, _) => {
			var hits = FilterAll(find.Text.Trim());
			findHint.Text = FindHint(hits, find.Text.Trim(), steps, shown);
		};
		s_find = find;
		// picking a page ends a search (its box is emptied, which shows that page alone) and is kept for the next time
		cats.SelectionChanged += (_, _) => {
			s_page = Math.Max(0, cats.SelectedIndex);
			if (find.Text != "") {
				find.Text = "";
			} else {
				ShowPages("");
			}
			scroller.ScrollToTop();
		};
		var top = new StackPanel();
		top.Children.Add(find);
		top.Children.Add(findHint);


		// a copy another player can import - its steps and options, nothing of this PC
		var share = new Button { Content = "Share this setup ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
			ToolTip = "Writes a copy of this setup (as last saved) for another player: every path of this PC and the commands it runs before and after a run are taken out" };
		share.Click += async (_, _) => {
			if (a_vm.Setup is not { } current || !share.IsEnabled) {
				return;
			}
			// These settings stay open when another setup is picked over what was typed: the button then stood under one
			// setup's name and wrote a copy of the other. Only the setup these settings were opened for is shared from here
			if (!MainViewModel.StillShown(openedFor, current.File)) {
				a_vm.Say($"Not shared: these are the settings of \"{openedName}\", and \"{current.Name}\" is picked in the main window now. Pick \"{openedName}\" again to share it.", "bad");
				return;
			}
			// the setup as it is this moment, written off the window's thread; one share at a time
			share.IsEnabled = false;
			var text = System.Text.Encoding.UTF8.GetBytes(current.Root.ToJsonString());
			var source = current.File;
			try {
				var (file, gone) = await Task.Run(() => SetupShare.Write(Setup.Parse(text, source), Path.Combine(AppPaths.Data, "Shared setups"), MainWindow.AppVersion))
					.ConfigureAwait(true);
				if (Ask.Show(a_w, "Shared copy made", WindowWords.F($"It is ready: {Path.GetFileName(file)}") + "\n\n" + WindowWords.Word("What the other player sets themselves:") + "\n" +
				    (gone.Count == 0 ? WindowWords.Word("nothing") : string.Join("\n", gone.Select(g => "- " + g))) + "\n\n" + WindowWords.Word("They bring it in with New setup > Import a setup file."),
				    "Show the file", "Close")) {
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true })?.Dispose();
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException or InvalidDataException) {
				a_vm.Say("The shared copy could not be made: " + e.Message, "bad");
			} finally {
				share.IsEnabled = true;
			}
		};
		stack.Children.Add(share);
		var mo2 = work["mo2"] as JsonObject ?? (JsonObject)(work["mo2"] = new JsonObject());
		Field(stack, apply, "MO2 program (ModOrganizer.exe)", mo2, "exe");
		Field(stack, apply, "MO2 instance folder", mo2, "instance");
		Field(stack, apply, "MO2 profile", mo2, "profile");
		Field(stack, apply, "Backups folder", work, "backups");
		Field(stack, apply, "Scratch folder (emptied after a run)", work, "scratch");
		Field(stack, apply, "SKSE log folder (empty = My Games)", work, "skseLogs");
		stack = gamePage;
		// the character whose saves the game steps load, picked from the characters of the profile's saves (read off the window's thread)
		CharacterChoice(stack, apply, work, a_vm.Setup!);
		// ---- Game steps (the rest of that page)
		work["quietGame"] ??= false;
		Field(gamePage, apply, "Run game steps in a small, muted window (your game settings are put back after)", work, "quietGame", JsonValueKind.True);
		work["copySaves"] ??= true;
		Field(gamePage, apply, "Back up my saves before a game step", work, "copySaves", JsonValueKind.True);
		// off unless asked for - it changes the game's own saving switch in the run's game
		work["forceSave"] ??= false;
		Field(gamePage, apply, "Force the save when a mod blocks saving (the run's game only)", work, "forceSave", JsonValueKind.True);
		work["guidedReminderMin"] ??= 0;
		Field(gamePage, apply, "Remind me when a game step waits for me, every (minutes; 0 = off)", work, "guidedReminderMin", JsonValueKind.Number);
		// a pop-up of the game is closed at once, whatever it is - a setup may leave the ones the run
		// does not know to the player instead
		Choice(gamePage, apply, "A game pop-up the run does not know", work, InGame.UnknownBoxSetting,
			[("close", "close it at once (named in the log and the notes)"), ("stop", "leave it to me (unanswered, the step stops)")]);
		work["boxWaitSeconds"] ??= 60;
		Field(gamePage, apply, "Wait for your answer to a game pop-up that is left to you (seconds)", work, "boxWaitSeconds", JsonValueKind.Number);
		Choice(gamePage, apply, "Rebuild shader caches", work, "shaderRebuild", [("whenChanged", "when the shader mod changed"), ("always", "every run"), ("never", "never")]);
		Field(gamePage, apply, "Allow writing into the game folder (only NGIO's grass cache needs it)", work, "gameFolderWrites", JsonValueKind.True);
		// two mods that put a window of their own in front of a game start (GameMods.cs); each line matters only with that mod ticked
		Choice(gamePage, apply, "Faster Archives' repack offer", work, FasterArchives.SettingName,
			[("ask", "wait for my choice in its window"), ("skip", "skip it during the run (it asks in my own game)")]);
		// shown as the run reads it (a setup of the first builds may still say it under their old key, and an empty value says
		// nothing), so a save keeps what the run does today
		work[Crdw.SettingName] = Crdw.Word(a_vm.Setup!);
		Choice(gamePage, apply, "Cached Recursive Directory Walk (its pop-up off)", work, Crdw.SettingName,
			[("changed", "new cache when the run changed files"), ("always", "new cache at every game start"), ("never", "leave it alone - warn me only")]);

		// ---- Run
		// the preview before a run
		work["previewBeforeRun"] ??= true;
		Field(runPage, apply, "Show the run preview before it starts", work, "previewBeforeRun", JsonValueKind.True);
		// on by default - the tools after them build on what the skipped tools made
		work[Runner.KeepEarlierSetting] ??= true;
		Field(runPage, apply, "When a run starts at a later step, leave the earlier tools' outputs on (they are not run again)", work, Runner.KeepEarlierSetting, JsonValueKind.True);
		Field(runPage, apply, "Check on a tool that sits idle for (minutes)", work, "idleMinutes", JsonValueKind.Number);
		Field(runPage, apply, "Free disk space needed (GB)", work, "minFreeGb", JsonValueKind.Number);
		// the forecast of the room a run takes: on, a run that will not fit is not started; off, it is only told
		work["diskForecastStops"] ??= true;
		Field(runPage, apply, "Do not start a run that will not fit on the disk (off: warn only)", work, "diskForecastStops", JsonValueKind.True);
		// the tools at a lower priority, so the PC stays smooth while they work (a run may take a little longer)
		work["lowerPriority"] ??= false;
		Field(runPage, apply, "Run tools at low priority (smoother PC, slightly longer run)", work, "lowerPriority", JsonValueKind.True);
		work["keepMo2Open"] ??= false;
		Field(runPage, apply, "Keep MO2 open during a run (fewer restarts; needs the MO2 Tools menu entry)", work, "keepMo2Open", JsonValueKind.True);
		Field(runPage, apply, "Commands before a run (one per line; each must succeed)", work, "beforeRun", JsonValueKind.Array);
		Field(runPage, apply, "Commands after a run (one per line)", work, "afterRun", JsonValueKind.Array);
		// what a run does to the profile besides its steps' own outputs: the LOD outputs that go off with them, and where the
		// tools' plugins stand in the plugin list afterwards (a new setup writes the tools' own guidance; a player may change
		// or empty each)
		Field(runPage, apply, "LOD output mods switched off before the first step (one per line)", work, "lodOutputs", JsonValueKind.Array);
		Field(runPage, apply, "Plugin places after a run (each: the plugin, then last_master, after:<plugin> or end)", work, "placement", JsonValueKind.Object);
		Field(runPage, apply, "A plugin's place when a run first ticks it (the plugin: the plugin it goes below)", work, "placeAfter", JsonValueKind.Object);

		// ---- Backups: only the last few runs' kept, as a zip, their size and a clean-up
		work["keepBackups"] ??= 5;
		Field(backupPage, apply, "Backups to keep (runs; 0 = all)", work, "keepBackups", JsonValueKind.Number);
		BackupsBlock(a_w, a_vm, backupPage);
		work["zipBackups"] ??= true;
		Field(backupPage, apply, "Zip old outputs in the backup (much smaller)", work, "zipBackups", JsonValueKind.True);

		// ---- Alerts
		var alerts = work["alerts"] as JsonObject ?? (JsonObject)(work["alerts"] = new JsonObject { ["notification"] = true, ["sound"] = true, ["flash"] = false });
		Field(alertPage, apply, "Windows notification", alerts, "notification", JsonValueKind.True);
		Field(alertPage, apply, "Sound", alerts, "sound", JsonValueKind.True);
		Field(alertPage, apply, "Flash the taskbar button", alerts, "flash", JsonValueKind.True);

		// ---- MO2 Changelog: a profile only tests use, or test mods, can be left out of the view (nothing is lost)
		var changes = work["changes"] as JsonObject ?? (JsonObject)(work["changes"] = new JsonObject());
		Field(changesPage, apply, "Hide these profiles (one per line)", changes, "hideProfiles", JsonValueKind.Array);
		Field(changesPage, apply, "Hide mods whose name starts with (one per line)", changes, "hideModsStartingWith", JsonValueKind.Array);
		stack = programPage;

		Head("LANGUAGE");
		var languageNote = new TextBlock { Text = "The window's language. A language file of your own in the Languages folder adds to the shipped ones, or corrects one: it holds each English text and its translation, and what it leaves out stays as it was.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		var languages = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 10, 0), ItemsSource = WindowWords.Available(), SelectedItem = WindowWords.Picked() };
		System.Windows.Automation.AutomationProperties.SetName(languages, "The window's language");
		languages.SelectionChanged += (_, _) => {
			if (languages.SelectedItem is string picked && picked != WindowWords.Picked()) {
				try {
					WindowWords.Pick(picked);
					languageNote.Text = WindowWords.Word("It applies the next time this program starts.");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					a_vm.Say("The language could not be kept: " + e.Message, "bad");
				}
			}
		};
		var makeLanguage = new Button { Content = "Make a language file ...", Style = (Style)a_w.FindResource("Ghost"), ToolTip = "A file with every text of the window, ready to translate." };
		makeLanguage.Click += (_, _) => {
			try {
				var file = WindowWords.WriteTemplate(a_w);
				a_vm.Say("Made: " + file + (MainViewModel.OpenFolder(WindowWords.Folder) is { } why ? " (its folder did not open: " + why + ")" : ""), "good");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
				a_vm.Say("The language file could not be made: " + e.Message, "bad");
			}
		};
		stack.Children.Add(languageNote);
		var languageRow = new StackPanel { Orientation = Orientation.Horizontal };
		languageRow.Children.Add(languages);
		languageRow.Children.Add(makeLanguage);
		stack.Children.Add(languageRow);

		Head("LOOK");
		stack.Children.Add(new TextBlock { Text = "Themes and the text size have their own page: every look shown as a picture, one click to use it.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
		var openThemes = new Button { Content = "Open the themes ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, ToolTip = "Ctrl+T" };
		openThemes.Click += (_, _) => a_w.ShowPanel("themes");
		stack.Children.Add(openThemes);
		// drawn without the graphics card unless asked: less memory and a faster start for a window of text and bars (Drawing)
		// (a tick's own words do not wrap - the long form ran past the window's edge: the tick says what, a line under it the rest)
		var card = new CheckBox { IsChecked = Drawing.Hardware, Margin = new Thickness(0, 12, 0, 4), Content = "Draw this window with the graphics card" };
		card.Click += (_, _) => {
			try {
				Drawing.Pick(card.IsChecked == true);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_vm.Say("That could not be kept: " + e.Message, "bad");
			}
		};
		stack.Children.Add(card);
		stack.Children.Add(new TextBlock { Text = "It then holds about 25 MB more memory. Applies the next time the program starts.", Style = (Style)a_w.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap });

		NewVersions(a_w, stack);

		// add-ons from other modders, each allowed by the player
		stack = toolPage;
		Head("ADD-ONS");
		var addOnNote = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
			Text = "A modder can add their patcher with one file in their own mod. The wiki has the guide and an example." };
		var addOnRows = new StackPanel();
		var lookAddOns = new Button { Content = "Look for add-ons in this profile", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		var checkAddOn = new Button { Content = "Check my add-on file ...", Style = (Style)a_w.FindResource("Ghost"), ToolTip = "For modders: says in plain words what is wrong with an add-on file" };
		lookAddOns.Click += async (_, _) => {
			addOnRows.Children.Clear();
			if (a_vm.Setup is not { } current) {
				return;
			}
			lookAddOns.IsEnabled = false;
			try {
				// a long mod list on a slow disk: off the window's thread
				var (mo2, found) = await Task.Run(() => {
					var there = Mo2Instance.Load(current.Mo2Exe, current.Mo2Instance);
					return (there, AddOns.Find(there, new Profile(there, current.Profile, () => true)));
				});
				if (a_vm.Setup != current) {
					return;
				}
				addOnNote.Text = found.Count == 0 ? WindowWords.Word("No ticked mod of this profile holds an add-on.") : WindowWords.F($"{found.Count} add-on file(s) in this profile's ticked mods.");
				foreach (var hit in found) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (hit.AddOn is { } addOn && !AddOns.Allowed(current, addOn)) {
						var allow = new Button { Content = AddOns.InSetup(current, addOn) ? "It changed: allow it again" : "Add it", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
						allow.Click += (_, _) => {
							if (HasChanges) {
								a_vm.Say("Save or undo your changes first.", "warn");
								return;
							}
							if (AddOns.OutputTaken(mo2, current, addOn) is { } taken) {
								a_vm.Say($"{addOn.Name} would write into your mod {taken}, and a run replaces what its output holds. Rename that mod to use this add-on.", "bad");
								return;
							}
							if (Setup.Bool(current.Root, AddOns.NoAskSetting) != true && !Ask.Show(a_w, "Add this add-on?", addOn.Says, "Allow it", "Not now")) {
								return;
							}
							// the box can stay open while a run starts: a run's plan is what its preview showed
							if (a_vm.SetupInUse) {
								a_vm.Say("A run is using this setup: add it when the run is done.", "warn");
								return;
							}
							if (a_vm.WhyNotWriting("added") is { } notNow) {
								a_vm.Say(notNow, "warn");
								return;
							}
							var before = (JsonObject)current.Root.DeepClone();
							var order = AddOns.AddToSetup(current.Root, addOn, current.Profile);
							try {
								current.Save();
							} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
								// the setup shown goes back to what its file holds
								foreach (var k in current.Root.Select(x => x.Key).ToList()) {
									current.Root.Remove(k);
								}
								foreach (var (k, v) in before.ToList()) {
									current.Root[k] = v?.DeepClone();
								}
								a_vm.Say("Not added: the setup could not be saved: " + e.Message, "bad");
								return;
							}
							a_vm.LoadSetups(current.File);
							a_w.ShowPanel("settings", true);
							if (order != null) {
								a_vm.Say(order, "warn");
							}
						};
						DockPanel.SetDock(allow, Dock.Right);
						row.Children.Add(allow);
					}
					row.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = hit.AddOn is { } a ?
						WindowWords.F($"{a.Name}, by {a.Author}, in {hit.Mod}") + (AddOns.InSetup(current, a) ? WindowWords.Word(" - in this setup") : "") :
						$"{hit.File}: {string.Join("; ", hit.Problems)}" });
					addOnRows.Children.Add(row);
				}
				// (built after the window loaded: into its language like the rest of it)
				WindowWords.Tree(addOnRows);
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				a_vm.Say("The add-ons could not be looked for: " + e.Message, "bad");
			} finally {
				lookAddOns.IsEnabled = true;
			}
		};
		checkAddOn.Click += (_, _) => {
			var d = new Microsoft.Win32.OpenFileDialog { Filter = WindowWords.Word("Add-on file").Replace("|", "", StringComparison.Ordinal) + "|*.json", Title = WindowWords.Word("Your add-on file") };
			if (d.ShowDialog(a_w) != true) {
				return;
			}
			try {
				var parent = Path.GetDirectoryName(d.FileName);
				var problems = AddOns.Problems(Files.ReadSharedText(d.FileName), AddOns.IsFolderInMod(Path.GetFileName(parent)) ? Path.GetDirectoryName(parent) : null);
				a_vm.Say(problems.Count == 0 ? "That add-on file is right." : string.Join(" ", problems.Select(p => p + ".")), problems.Count == 0 ? "good" : "bad");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
				a_vm.Say("That file could not be read: " + e.Message, "bad");
			}
		};
		stack.Children.Add(addOnNote);
		var addOnBar = new WrapPanel { Orientation = Orientation.Horizontal };
		addOnBar.Children.Add(lookAddOns);
		addOnBar.Children.Add(checkAddOn);
		stack.Children.Add(addOnBar);
		stack.Children.Add(addOnRows);
		work[AddOns.NoAskSetting] ??= false;
		Field(stack, apply, "Do not ask before adding an add-on, or when its file changes", work, AddOns.NoAskSetting, JsonValueKind.True);

		// the program in Mod Organizer's Tools menu
		if (a_vm.Setup is { } menuSetup && Environment.ProcessPath is { } program) {
			try {
				var menuMo2 = Mo2Instance.Load(menuSetup.Mo2Exe, menuSetup.Mo2Instance);
				Head("MO2 TOOLS MENU");
				var menuNote = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
					Text = WindowWords.Word(Mo2MenuEntry.IsCurrent(menuMo2, program) ? "This program is in Mod Organizer's Tools menu. It opens here on the profile Mod Organizer shows." :
						"An entry in Mod Organizer's Tools menu that opens this program on the profile Mod Organizer shows. It is one small file in Mod Organizer's plugins folder.") };
				stack.Children.Add(menuNote);
				if (!Mo2MenuEntry.IsCurrent(menuMo2, program)) {
					var addMenu = new Button { Content = "Add it to the Tools menu", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
					addMenu.Click += (_, _) => {
						try {
							Mo2MenuEntry.Add(menuMo2, program);
							menuNote.Text = WindowWords.Word("Added. It shows the next time Mod Organizer starts.");
							addMenu.IsEnabled = false;
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
							a_vm.Say("The entry could not be added: " + e.Message, "bad");
						}
					};
					stack.Children.Add(addMenu);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				a_vm.Say("Mod Organizer could not be read for its plugins: " + e.Message, "warn");
			}
		}

		// our add-on, only where Root Builder is in this setup's Mod Organizer
		if (a_vm.Setup is { } rootSetup) {
			try {
				var rootMo2 = Mo2Instance.Load(rootSetup.Mo2Exe, rootSetup.Mo2Instance);
				var state = RootBuilderAddOn.StateOf(rootMo2);
				if (state != RootBuilderAddOn.State.NoRootBuilder) {
					Head("ROOT BUILDER");
					var rootNote = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
						Text = WindowWords.Word("Runs wait for Root Builder to put its files in place and to take them out. Our add-on, Root Builder Profiles, keeps what Root Builder leaves behind per profile " +
						"and offers to move presets no mod owns out of the game folder. It is optional, and it needs Root Builder itself.") };
					stack.Children.Add(rootNote);
					if (state == RootBuilderAddOn.State.Current) {
						rootNote.Text += WindowWords.Word(" It is in Mod Organizer.");
					} else {
						var addOn = new Button { Content = state == RootBuilderAddOn.State.NotAdded ? "Add Root Builder Profiles ..." : "Update Root Builder Profiles ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
						addOn.Click += (_, _) => {
							if (!Ask.Show(a_w, RootBuilderAddOn.Title, "It goes into Mod Organizer's plugins folder, and Mod Organizer loads it the next time it starts. To take it out, delete its folder there, rootbuilderprofiles.", "Add it", "Not now")) {
								return;
							}
							try {
								RootBuilderAddOn.Add(rootMo2);
								rootNote.Text = WindowWords.Word("Root Builder Profiles is in Mod Organizer now. It loads the next time Mod Organizer starts.");
								addOn.IsEnabled = false;
							} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
								a_vm.Say("The add-on could not be added: " + e.Message, "bad");
							}
						};
						stack.Children.Add(addOn);
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				// a setup whose Mod Organizer does not read says so in its check: nothing to offer here
				a_vm.Say("Mod Organizer could not be read for its plugins: " + e.Message, "warn");
			}
		}

		// aware of a tool's updates, the download from the runner (its page, in the player's browser)
		Head("TOOL UPDATES");
		// the Check button looks for updates too (asked of the tools' pages only on that press)
		work["checkUpdates"] ??= true;
		Field(stack, apply, "Check (F5) also looks for updates", work, "checkUpdates", JsonValueKind.True);
		var updNote = new TextBlock { Text = "Asks each tool's own page (Nexus, GitHub) for its newest version - only when you press this.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
		var updRows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
		var updCheck = new Button { Content = "Check its tools for updates", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
		updCheck.Click += async (_, _) => {
			if (a_vm.Setup is not { } setup) {
				return;
			}
			if (NotThisSetup(openedFor, openedName, setup) is { } another) {
				updNote.Text = another;
				return;
			}
			updCheck.IsEnabled = false;
			updRows.Children.Clear();
			updNote.Text = WindowWords.Word("Asking the tools' pages ...");
			try {
				var res = await Task.Run(async () => await ToolUpdates.Check(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), CancellationToken.None));
				foreach (var r in res) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (r.Newer) {
						var get = new Button { Content = "Open its download page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
						var page = r.Page;
						get.Click += (_, _) => Opened(a_w, page);
						DockPanel.SetDock(get, Dock.Right);
						row.Children.Add(get);
					}
					var said = r.Problem ?? (r.Newer ? WindowWords.F($"a NEWER version is out: {r.Newest} (yours {r.Yours})") : WindowWords.F($"up to date ({r.Yours})"));
					row.Children.Add(new TextBlock { Text = $"{r.Tool}: {said}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }
						.Live(TextBlock.ForegroundProperty, r.Newer ? "Warn" : r.Problem != null ? "Muted" : "Text"));
					updRows.Children.Add(row);
				}
				// (built after the window loaded: into its language like the rest of it)
				WindowWords.Tree(updRows);
				var (summary, count) = ToolUpdates.SummaryParts(res);
				updNote.Text = count is { } n ? WindowWords.Format(summary, n) : WindowWords.Word(summary);
			} catch (Exception e) {
				updNote.Text = WindowWords.Format("Could not check: {0}", e.Message);
			} finally {
				updCheck.IsEnabled = true;
			}
		};
		// what a tool's update may have moved, looked for before a run needs it
		var testTools = new Button { Content = "Test my tools ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8),
			ToolTip = "Opens each tool that is driven through its window, up to the window a run presses, and looks for what it would press" };
		// While the tools are tested a button of its own stops the test: a LOD tool takes minutes to reach its window, and the
		// main window does not close until the test is over. The two buttons take turns, so each keeps its own words
		var testStop = new Button { Content = "Stop the test", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 8) };
		var testRows = new StackPanel();
		void ShowTest()
		{
			var going = s_testing != null;
			var (shown, hidden) = going ? (testStop, testTools) : (testTools, testStop);
			var hadFocus = hidden.IsKeyboardFocused;
			hidden.Visibility = Visibility.Collapsed;
			shown.Visibility = Visibility.Visible;
			testStop.IsEnabled = s_testing is { IsCancellationRequested: false };
			if (hadFocus) {
				shown.Focus();
			}
		}
		s_showTest = ShowTest;
		s_testRows = testRows;
		ShowTest();
		testStop.Click += (_, _) => {
			s_testing?.Cancel();
			testStop.IsEnabled = false;
		};
		testTools.Click += async (_, _) => {
			if (s_testing != null) {
				return;
			}
			if (a_vm.Setup is { } picked && NotThisSetup(openedFor, openedName, picked) is { } another) {
				errors.Text = another;
				return;
			}
			if (a_vm.Setup is not { } testSetup || !Ask.Show(a_w, "Test my tools", WindowWords.Word("Each tool this program drives through its window opens through Mod Organizer, up to the window a run presses, and closes again. " +
				"Nothing is pressed and nothing is built. Mod Organizer closes for it, and the LOD tools read your whole load order first, so it can take a while."), "Test them", "Not now")) {
				return;
			}
			// (each tool is opened through the setup's Mod Organizer: what the setup starts is asked about first)
			if (!a_vm.StartsFirst(testSetup)) {
				return;
			}
			s_testRows?.Children.Clear();
			using var stop = new CancellationTokenSource();
			s_testing = stop;
			s_showTest?.Invoke();
			using var hand = a_w.Working(WindowWords.Word("your tools are being tested"));
			try {
				var results = await Task.Run(() => ToolProof.TestAll(testSetup, RunLog.Null, stop.Token));
				// (into the drawing on screen now: Settings may have been drawn again since the test began)
				foreach (var r in results) {
					s_testRows?.Children.Add(new TextBlock { Text = r.Text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) }
						.Live(TextBlock.ForegroundProperty, r.Good ? "Text" : "Bad"));
				}
			} catch (OperationCanceledException) {
				a_vm.Say("The test of your tools was stopped; the tool it had opened is closed again.", "warn");
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or StepFailedException) {
				a_vm.Say("The tools could not be tested: " + e.Message, "bad");
			} finally {
				s_testing = null;
				s_showTest?.Invoke();
			}
		};
		// the look for updates first, its note right under its own button (the note turns into that look's answer) - standing
		// above the button it read as words about "Test my tools", the button over it
		stack.Children.Add(updCheck);
		stack.Children.Add(updNote);
		stack.Children.Add(updRows);
		stack.Children.Add(testTools);
		stack.Children.Add(testStop);
		stack.Children.Add(testRows);

		stack = stepPage;
		var pick = new ComboBox { ItemsSource = steps.Select(s => StepCatalog.Name(Setup.Str(s, "step") ?? "")).ToList(), Margin = new Thickness(0, 0, 0, 8) };
		System.Windows.Automation.AutomationProperties.SetName(pick, "Step to edit");
		s_pick = pick;
		s_steps = steps;
		var stepHost = new StackPanel();
		var stepApply = new List<Func<string?>>();
		pick.SelectionChanged += (_, _) => {
			// the step on screen keeps its fields until every value reads (a wrong one stays where it can be fixed)
			var wrong = stepApply.Select(f => f()).OfType<string>().ToList();
			if (wrong.Count > 0) {
				errors.Text = WindowWords.Word("Fix this step first:") + "\n" + string.Join("\n", wrong);
				pick.SelectedIndex = shown;
				return;
			}
			if (pick.SelectedIndex == shown) {
				return;
			}
			shown = pick.SelectedIndex;
			// a search in force is applied again once the new step's fields are built (else they stay hidden under the old filter)
			pick.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () => {
				if (find.Text.Trim() is { Length: > 0 } q) {
					var hits = FilterAll(q);
					findHint.Text = FindHint(hits, q, steps, shown);
				}
			});
			stepHost.Children.Clear();
			stepApply.Clear();
			if (pick.SelectedIndex < 0) {
				return;
			}
			var o = steps[pick.SelectedIndex]["settings"] as JsonObject ?? (JsonObject)(steps[pick.SelectedIndex]["settings"] = new JsonObject());
			var aboutKey = Setup.Str(steps[pick.SelectedIndex], "step") ?? "";
			var aboutRow = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
			// help on every step - the tool's own page (its author's instructions), opened in the browser
			if (HelpPage(aboutKey) is { } help) {
				var page = new Button { Content = "Its own page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = help.Url,
					VerticalAlignment = VerticalAlignment.Top };
				System.Windows.Automation.AutomationProperties.SetHelpText(page, WindowWords.Word(help.What));
				page.Click += (_, _) => Opened(a_w, help.Url);
				DockPanel.SetDock(page, Dock.Right);
				aboutRow.Children.Add(page);
			}
			aboutRow.Children.Add(new TextBlock { Text = StepCatalog.Info(aboutKey).About, Style = (Style)a_w.FindResource("MutedText") });
			stepHost.Children.Add(aboutRow);
			foreach (var (key, val) in o.ToList()) {
				if (key.StartsWith('_')) {
					stepHost.Children.Add(new TextBlock { Text = val?.ToString() ?? "", Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 8) });
					continue;
				}
				Field(stepHost, stepApply, Label(key, aboutKey), o, key, val?.GetValueKind() ?? JsonValueKind.String, a_step: aboutKey);
			}
			var stepKey = Setup.Str(steps[pick.SelectedIndex], "step") ?? "";
			// the tool's settings from its OWN window, kept for every run (never a copy of its window here).
			// Only while the setup picked in the main window is the one these settings were opened for: with another one
			// picked, the button stood among this setup's steps and opened - and learned for - the other setup's tool
			if (ToolSettings.CanLearn(new StepSettings(o)) && a_vm.Setup is { } setupNow && IsFor(setupNow)) {
				var learned = ToolSettings.Load(setupNow, stepKey);
				var name = StepCatalog.Name(stepKey);
				var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 4, 0, 6),
					Text = learned == null ? WindowWords.Word("Its settings are not learned yet: it runs with whatever its own window last saved.") :
						WindowWords.F($"Its settings were learned {learned.At.ToLocalTime().ToString("MMM d HH:mm", InvariantCulture)} ({learned.Files.Count} file(s)) - every run uses them.") };
				var learn = new Button { Content = "Learn its settings (opens its own window)", Style = (Style)a_w.FindResource("Ghost"),
					HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
				// while a tool is being opened (or is open) to learn its settings, this button stops that wait instead. The wait
				// outlives this step's view - another step picked, Save, Undo - so every learn button stops the one that is going,
				// and none starts a second
				var learnText = learn.Content;
				void ShowLearn()
				{
					if (s_learning is { } going) {
						learn.Content = LearningHere(setupNow, stepKey) ? WindowWords.Word("Stop waiting") : WindowWords.F($"Stop waiting for {s_learningName}");
						learn.IsEnabled = !going.Stopping;
					} else {
						learn.Content = learnText;
						learn.IsEnabled = true;
					}
				}
				// the step whose tool is open: this view's line now says how it goes
				if (s_learning is { } still && LearningHere(setupNow, stepKey)) {
					s_learningSays = t => status.Text = t;
					status.Text = still.Opened ? WindowWords.F($"{name} is open - set it up, save, and close it ...") : WindowWords.F($"waiting to open {name} ...");
				}
				s_showLearn = ShowLearn;
				ShowLearn();
				learn.Click += async (_, _) => {
					if (s_learning is { } o) {
						// (not open yet: stopped at once; open: only on the player's word - decided on the one state the opening sets)
						if (!o.Stopping && (o.StopIfNotOpen() || AskStopLearning(a_w))) {
							o.Stop();
							learn.IsEnabled = false;
						}
						return;
					}
					if (!Ask.Show(a_w, "Learn its settings", WindowWords.F($"{name} opens through Mod Organizer (Mod Organizer is closed first if it is open). Set it up the way every run should use it, save, and close it - its settings are then kept with this setup. To change them later, do this again."),
					        "Open it", "Not now")) {
						return;
					}
					// (the tool opens through the setup's Mod Organizer: what the setup starts is asked about first)
					if (!a_vm.StartsFirst(setupNow)) {
						return;
					}
					using var now = new ToolOpen();
					s_learning = now;
					s_learningSetup = setupNow.File;
					s_learningStep = stepKey;
					s_learningName = name;
					s_learningSays = t => status.Text = t;
					s_showLearn?.Invoke();
					status.Text = WindowWords.F($"opening {name} ...");
					ToolOpen.Result result;
					try {
						using (a_w.Working(WindowWords.F($"{name} is open to learn its settings"))) {
							result = await now.Go(setupNow, stepKey,
								why => Ask.Show(a_w, WindowWords.F($"{name} cannot open yet"), $"{ToolsDialog.Capital(why)}.\n\n" +
								                     WindowWords.F($"{name} opens through Mod Organizer, and Mod Organizer is closed first - never while that is open. Close it and try again, or wait here: {name} opens by itself once it has ended, and you can stop waiting at any time."),
								                     "Wait for it", "Not now"),
								line => a_w.Dispatcher.BeginInvoke(() => {
									if (s_learning == now) {
										s_learningSays?.Invoke(now.Opened ? WindowWords.F($"{name} is open - set it up, save, and close it ...") : line);
									}
								}));
						}
					} finally {
						s_learning = null;
					}
					var says = s_learningSays;
					s_learningSays = null;
					var outcome = (result.Learned is { } l
						? WindowWords.F($"Learned: {l.Files.Count} settings file(s) ({string.Join(", ", l.Files.Select(f => Path.GetFileName(f.Live)))}) - every run uses them.")
						: result.How == ToolOpen.Ending.Failed ? WindowWords.Format("Not learned: {0}", result.Why) : WindowWords.Format("Not learned: {0}", ToolsDialog.Outcome(name, result))) + ToolsDialog.StillOn(now.StillOn);
					s_showLearn?.Invoke();
					// on the view that shows that step now, and on the main window's message line too (the settings window may be
					// on another step by now, or closed)
					says?.Invoke(outcome);
					a_vm.Say($"{name}: {outcome}", result.Learned != null ? "" : "warn");
				};
				stepHost.Children.Add(status);
				stepHost.Children.Add(learn);
			}
			// LOD settings depend on the mods it is built from - their own pages say which
			if (LodGuide.Applies(stepKey)) {
				LodBlock(a_w, a_vm.Setup!, stepKey, stepHost);
			}
			foreach (var (key, kind, def) in Offered(stepKey, o).Where(k => !o.ContainsKey(k.Key)).ToList()) {
				Field(stepHost, stepApply, WindowWords.Format("{0} (default {1})", WindowWords.Word(Label(key, stepKey)), WindowWords.Word(def)), o, key, kind, def == "on", stepKey);
			}
			// drawn after Settings opened (another step picked): into the window's language like the rest of it
			WindowWords.Tree(stepHost);
		};
		stack.Children.Add(pick);
		stack.Children.Add(stepHost);
		if (a_vm.SelectedStep != null) {
			pick.SelectedIndex = steps.FindIndex(s => Setup.Str(s, "step") == a_vm.SelectedStep.Key);
		}

		var save = new Button { Content = "_Save", Style = (Style)a_w.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0) };
		var undo = new Button { Content = "_Undo changes", Style = (Style)a_w.FindResource("Ghost") };
		var folders = new Button { Content = "Open the data folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
		save.Click += async (_, _) => {
			if (NotThisSetup(openedFor, openedName, a_vm.Setup) is { } another) {
				errors.Text = another;
				return;
			}
			if (a_vm.SetupInUse) {
				errors.Text = "Not saved: a run is using this setup right now - save when it is done (what you typed stays here).";
				return;
			}
			if (a_vm.Saving) {
				errors.Text = "Not saved yet: the change before this one is still being saved - press Save again in a moment (what you typed stays here).";
				return;
			}
			var problems = apply.Concat(stepApply).Select(f => f()).OfType<string>().ToList();
			if (problems.Count > 0) {
				errors.Text = "Not saved:\n" + string.Join("\n", problems);
				return;
			}
			var trial = Setup.FromJson((JsonObject)work.DeepClone(), a_vm.Setup!.File);
			var p = trial.Problems();
			if (p.Count > 0) {
				errors.Text = "Not saved: " + string.Join("; ", p);
				return;
			}
			// (what the setup started before this change, allowed or not: a change made here to a setup whose programs and
			// commands were the player's own leaves them the player's own - one never looked at stays to be asked about)
			var known = MainViewModel.StartsKnown(a_vm.Setup);
			foreach (var k in a_vm.Setup.Root.Select(x => x.Key).ToList()) {
				a_vm.Setup.Root.Remove(k);
			}
			foreach (var k in work.Select(x => x.Key).ToList()) {
				a_vm.Setup.Root[k] = work[k]?.DeepClone();
			}
			// Written away from the window's thread. Until it is done the button waits (a second press cannot start a second
			// save) and nothing in the settings can be changed: a field changed after its text was taken would be gone when
			// the settings are drawn again below - unsaved, and never asked about. After a save that failed everything is
			// free again, with what was typed, for another try. Undo waits too: it draws the settings again from the setup
			// in memory, which holds what is being saved, not what is in the file. (A button that is switched off loses the
			// keyboard: Save gets it back, so a second try needs no mouse.)
			var byKeyboard = save.IsKeyboardFocused;
			save.IsEnabled = false;
			undo.IsEnabled = false;
			host.IsEnabled = false;
			bool saved;
			try {
				saved = await a_vm.SaveSetup();
			} finally {
				save.IsEnabled = true;
				undo.IsEnabled = true;
				host.IsEnabled = true;
				if (byKeyboard) {
					save.Focus();
				}
			}
			if (saved && known) {
				MainViewModel.AllowStarts(trial);
			}
			// The settings window was closed while this was being written - or closed and opened again, for this setup or
			// another: what is on screen now is not the drawing this save was started from. It is left exactly as it is
			// (drawn again from here, edits made in the new one were gone without a question, and a window the player
			// had closed came back). The main window's list still follows what was saved
			if (drawing != s_drawing) {
				if (NotThisSetup(openedFor, openedName, a_vm.Setup) == null) {
					a_vm.LoadSteps();
				}
				// The save failed, and settings opened again for this setup while it was being written were drawn from the setup
				// in memory - which held what was being saved, not what is in its file. They keep those values (nothing typed
				// is lost), marked as not saved - closing them asks first - and say why in the settings themselves
				if (!saved && MainViewModel.StillShown(openedFor, s_openedFor) && s_errors is { } now) {
					HasChanges = true;
					now.Text = "Not saved: " + a_vm.Message + " - what these settings show is not in the setup's file yet: press Save again.";
				}
				return;
			}
			// another setup was picked while this one was being written: its file holds what was saved (or not), and the
			// setup shown now is neither read again nor are these settings drawn again over it
			if (NotThisSetup(openedFor, openedName, a_vm.Setup) != null) {
				errors.Text = saved ? "" : "Not saved: " + a_vm.Message;
				return;
			}
			if (!saved) {
				// memory back to what is on disk; Settings keeps what was typed for another try
				a_vm.LoadSteps();
				errors.Text = "Not saved: " + a_vm.Message;
				return;
			}
			a_vm.LoadSteps();
			errors.Text = "";
			a_w.ShowPanel("settings", true);
		};
		undo.Click += (_, _) => a_w.ShowPanel("settings", true);
		folders.Click += (_, _) => {
			if (MainViewModel.OpenFolder(AppPaths.Data) is { } why) {
				a_w.Say("The data folder did not open: " + why);
			}
		};
		// a clean removal of everything the program keeps (to the Recycle Bin, never erased)
		var remove = new Button { Content = "Remove program data ...", Style = (Style)a_w.FindResource("Danger"), Margin = new Thickness(10, 0, 0, 0),
			ToolTip = "Moves Tool Runner's data and run backups to the Recycle Bin. The mods the tools created stay in Mod Organizer." };
		remove.Click += async (_, _) => {
			remove.IsEnabled = false;
			try {
				await RemoveData(a_w, errors);
			} finally {
				remove.IsEnabled = true;
			}
		};
		var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
		bar.Children.Add(save);
		bar.Children.Add(undo);
		bar.Children.Add(folders);
		// the data kept in Windows' own app-data folder, so a new version unzipped anywhere keeps the run history
		if (!AppPaths.InAppData) {
			var keep = new Button { Content = "Keep data through updates ...", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0),
				ToolTip = "Moves Tool Runner's data (setups, run history, learned settings) into Windows' app-data folder, where every new version finds it" };
			keep.Click += async (_, _) => {
				keep.IsEnabled = false;
				try {
					await MoveDataToAppData(a_w, a_vm, errors);
				} finally {
					keep.IsEnabled = true;
				}
			};
			bar.Children.Add(keep);
		} else if (!AppPaths.AppDataChosen) {
			// the data is there already (this copy could not write beside itself): only a new copy elsewhere would not find it yet
			var keep = new Button { Content = "Keep data through updates", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0),
				ToolTip = "Every copy of Tool Runner on this PC uses this data folder from now on, also a new version unzipped into another folder" };
			keep.Click += async (_, _) => {
				keep.IsEnabled = false;
				try {
					await Task.Run(() => DataFolder.Choose(AppPaths.Data));
					bar.Children.Remove(keep);
					a_w.Say("Every copy of Tool Runner on this PC uses this data folder from now on.");
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					errors.Text = "Not kept: " + e.Message;
				} finally {
					keep.IsEnabled = true;
				}
			};
			bar.Children.Add(keep);
		}
		bar.Children.Add(remove);
		var where = new TextBlock { Text = WindowWords.Format("Data folder (setups, runs, backups): {0}", AppPaths.Data), Style = (Style)a_w.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		var dock = new DockPanel();
		DockPanel.SetDock(where, Dock.Bottom);
		DockPanel.SetDock(bar, Dock.Bottom);
		dock.Children.Add(where);
		DockPanel.SetDock(errors, Dock.Bottom);
		dock.Children.Add(bar);
		dock.Children.Add(errors);
		DockPanel.SetDock(top, Dock.Top);
		dock.Children.Add(top);
		foreach (var (_, p) in pages) {
			host.Children.Add(p);
		}
		ShowPages("");
		// History and Themes open from here (they are not on the main window): above the list of pages
		var side = new DockPanel { Width = 168, Margin = new Thickness(0, 0, 16, 0) };
		var opens = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
		foreach (var (text, panel, keys) in new[] { ("History", "history", "Ctrl+H"), ("Themes", "themes", "Ctrl+T") }) {
			var open = new Button { Content = text, Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 6),
				ToolTip = WindowWords.Word(panel == "history" ? "Past runs and the MO2 Changelog" : "The window's look and text size") + " (" + keys + ")" };
			open.Click += (_, _) => a_w.ShowPanel(panel);
			opens.Children.Add(open);
		}
		DockPanel.SetDock(opens, Dock.Top);
		side.Children.Add(opens);
		cats.Width = double.NaN;
		cats.Margin = new Thickness(0);
		side.Children.Add(cats);
		DockPanel.SetDock(side, Dock.Left);
		dock.Children.Add(side);
		dock.Children.Add(scroller);
		return dock;
	}

	const string HeadTag = "head";
	static TextBox? s_find;
	// the page last looked at (kept while the program runs, so Save and Undo come back to it)
	static int s_page;

	static ListBox? s_cats;
	static List<string> s_catNames = [];

	// the line of the drawing on screen that says why its settings were not saved (a save of an earlier drawing that ends
	// late writes its failure there)
	static TextBlock? s_errors;

	// A tool opened to learn its settings, while the wait for it goes (one at a time): it outlives the step view that started it
	// and the settings window, so it can always be stopped. s_learningSays: the line of the view that shows that step of that
	// setup now; s_showLearn: the learn button of the view drawn last
	static ToolOpen? s_learning;
	static string s_learningSetup = "";
	static string s_learningStep = "";
	static string s_learningName = "";
	static Action<string>? s_learningSays;
	static Action? s_showLearn;

	// the tool being learned is this setup's own step (another setup's step of the same tool learns into that setup only)
	static bool LearningHere(Setup a_setup, string a_step) =>
		s_learningStep == a_step && string.Equals(s_learningSetup, a_setup.File, StringComparison.OrdinalIgnoreCase);

	static bool AskStopLearning(Window a_over) =>
		Ask.Show(a_over, WindowWords.F($"Stop waiting for {s_learningName}?"), WindowWords.F($"{s_learningName} is open. Its settings are kept once you save and close it there.") + "\n\n" +
			WindowWords.F($"If you stop waiting, {s_learningName} stays open, but nothing it saves is kept with this setup."), "Stop waiting", "Keep waiting");

	// The settings window is about to close: a wait for a tool opened from it would go on with nothing left to stop it, and keep
	// the main window from closing. One still waiting for its window is stopped (nothing is lost); one whose tool is open is
	// stopped only on the player's word - true when the window may close
	public static bool LeaveLearning(Window a_over)
	{
		if (s_learning is not { } o || o.Stopping) {
			return true;
		}
		if (o.StopIfNotOpen()) {
			return true;
		}
		if (!AskStopLearning(a_over)) {
			return false;
		}
		o.Stop();
		return true;
	}

	// The test of the tools belongs to the window, not to one drawing of Settings: Undo changes draws Settings again while a
	// test may still run, and the new drawing must stop that test, never start a second one beside it. s_showTest and
	// s_testRows are the test buttons and the result rows of the drawing on screen; a test that ends writes there.
	static CancellationTokenSource? s_testing;
	static Action? s_showTest;
	static StackPanel? s_testRows;

	// the settings window closed: nothing typed is left to ask about
	public static void Closed()
	{
		s_showLearn = null;
		s_learningSays = null;
		++s_drawing;
		HasChanges = false;
		s_openedFor = null;
		s_find = null;
		s_cats = null;
		s_errors = null;
		s_pick = null;
		s_steps = null;
		s_showTest = null;
		s_testRows = null;
	}

	// the drawing's step picker on the Steps page and the steps it lists
	static ComboBox? s_pick;
	static List<JsonObject>? s_steps;

	// The Steps page with a_key's settings open (a step double-clicked in the main window). False when this
	// drawing has no such step, or the step open now has a value that does not read (it stays, to be fixed first)
	public static bool ShowStep(string a_key)
	{
		if (s_cats is not { } cats || s_pick is not { } pick || s_steps is not { } steps) {
			return false;
		}
		var i = steps.FindIndex(s => Setup.Str(s, "step") == a_key);
		if (i < 0) {
			return false;
		}
		// a search left in the box would keep hiding the step's fields: the category's own change clears it, but Steps may be
		// the page already
		if (s_find is { } f && f.Text != "") {
			f.Text = "";
		}
		if (s_catNames.IndexOf("Steps") is var page and >= 0) {
			cats.SelectedIndex = page;
		}
		pick.SelectedIndex = i;
		return pick.SelectedIndex == i;
	}

	// the search box with a_q in it (the command box's "find a setting")
	public static void Find(string a_q)
	{
		if (s_find is { } f) {
			f.Text = a_q;
			f.Focus();
			f.CaretIndex = f.Text.Length;
		}
	}

	// the off-screen pictures only: the open settings turned to page a_page; its name, or null when there is no such page
	public static string? ShowPage(int a_page)
	{
		if (s_cats is not { } cats || a_page < 0 || a_page >= cats.Items.Count) {
			return null;
		}
		cats.SelectedIndex = a_page;
		return a_page < s_catNames.Count ? s_catNames[a_page] : cats.Items[a_page] as string;
	}

	// the search's line under its box: how many settings on screen hold the word, and the other steps whose settings hold it (a
	// step's own settings are on screen only while it is open on the Steps page)
	static string FindHint(int a_hits, string a_q, IReadOnlyList<JsonObject> a_steps, int a_open)
	{
		// (set after the window opened, so put into the window's language here - the walk that translates a window is past)
		if (a_q == "") {
			return WindowWords.Word("Find a setting - type a word (Ctrl+F)");
		}
		var others = StepsWith(a_steps, a_q, a_open);
		return (a_hits == 0 ? WindowWords.Word("No setting here has that word.") : WindowWords.Format("{0} setting(s) found.", a_hits)) +
			(others.Count == 0 ? "" : " " + WindowWords.Format("Also in the settings of: {0} (pick it on the Steps page).", string.Join(", ", others)));
	}

	// the steps (all but a_skip) whose settings hold a_q - in a setting's name, its label or its value
	public static List<string> StepsWith(IReadOnlyList<JsonObject> a_steps, string a_q, int a_skip)
	{
		bool Has(string? a_t) => (a_t ?? "").Contains(a_q, StringComparison.OrdinalIgnoreCase);
		// a setting's label as the window shows it: its English words, and the words of the language in use
		bool Named(string a_key, string a_step) => Label(a_key, a_step) is var label && (Has(label) || Has(WindowWords.Word(label)));
		bool Holds(JsonNode? a_v) => a_v switch {
			JsonValue v => v.TryGetValue<string>(out var s) && Has(s),
			JsonArray a => a.Any(Holds),
			_ => false,
		};
		if (a_q.Trim() == "") {
			return [];
		}
		return a_steps.Select((s, i) => (s, i)).Where(x => x.i != a_skip && x.s["settings"] is JsonObject o &&
				o.Any(p => Has(p.Key) || Named(p.Key, Setup.Str(x.s, "step") ?? "") || Holds(p.Value)))
			.Select(x => StepCatalog.Name(Setup.Str(x.s, "step") ?? "")).Distinct(StringComparer.Ordinal).ToList();
	}

	// the window's Ctrl+F while Settings is open
	public static void FocusFind()
	{
		if (s_find is { IsVisible: true } f) {
			f.Focus();
			f.SelectAll();
		}
	}

	// every row whose words hold the query stays, the others fold away; a section's heading stays while it has a row left (or
	// matches itself, then its whole section stays). Returns how many rows are shown
	static int Filter(Panel a_stack, string a_q)
	{
		bool Match(string? a_t) => (a_t ?? "").Contains(a_q, StringComparison.OrdinalIgnoreCase);
		var shown = 0;
		TextBlock? head = null;
		var headHit = false;
		var rowsSince = 0;
		void CloseSection()
		{
			if (head != null) {
				head.Visibility = a_q == "" || headHit || rowsSince > 0 ? Visibility.Visible : Visibility.Collapsed;
			}
		}
		foreach (var e in a_stack.Children.OfType<FrameworkElement>()) {
			if (e is TextBlock { Tag: HeadTag } h) {
				CloseSection();
				head = h;
				headHit = a_q != "" && Match(h.Text);
				rowsSince = 0;
				continue;
			}
			var hit = a_q == "" || headHit || Words(e).Any(Match);
			e.Visibility = hit ? Visibility.Visible : Visibility.Collapsed;
			if (hit) {
				++rowsSince;
				++shown;
			}
		}
		CloseSection();
		return shown;
	}

	// the words a row shows: its labels, notes, button texts, a list's items, its tip
	static IEnumerable<string> Words(DependencyObject a_e)
	{
		if (a_e is FrameworkElement { ToolTip: string tip }) {
			yield return tip;
		}
		switch (a_e) {
		case TextBlock t:
			yield return t.Text;
			break;
		case ContentControl { Content: string c }:
			yield return c;
			break;
		// a tick box whose words are a wrapping text block of its own
		case ContentControl { Content: DependencyObject inner }:
			foreach (var w in Words(inner)) {
				yield return w;
			}
			break;
		case ItemsControl items:
			foreach (var i in items.Items.OfType<object>()) {
				// (an item that is a control says its class before its words: only its words are a row's words)
				yield return i is ContentControl { Content: string said } ? said : i.ToString() ?? "";
			}
			break;
		}
		if (a_e is Panel p) {
			foreach (var c in p.Children.OfType<DependencyObject>()) {
				foreach (var w in Words(c)) {
					yield return w;
				}
			}
		}
	}

	// the program's data into Windows' app-data folder (DataFolder.MoveTo), with the run lock held and no setup written meanwhile;
	// the program closes after, and starts again on the new folder
	static async Task MoveDataToAppData(MainWindow a_w, MainViewModel a_vm, TextBlock a_errors)
	{
		if (HasChanges && !Ask.Show(a_w, "Settings not saved", "You changed settings and did not press Save. Go on without saving them?", "Go on", "Keep editing")) {
			return;
		}
		var to = AppPaths.AppDataFolder;
		if (!Ask.Show(a_w, "Keep data through updates", WindowWords.F($"Tool Runner's data (your setups, the run history, the settings learned from your tools) moves from\n{AppPaths.Data}\nto\n{to}") + "\n\n" +
			    WindowWords.Word("Every copy of Tool Runner on this PC then uses it, also a new version unzipped into another folder. Run backups stay where your setups put them.") + "\n\n" +
			    WindowWords.Word("Tool Runner closes when the move is done; start it again."), "Move my data", "Not now")) {
			return;
		}
		using var hand = a_w.Working(WindowWords.Word("the program's data is being moved"));
		// from here no setup is written, copied or started (MovingData); a change already on its way into its file lands first
		a_vm.MovingData = true;
		string? leftOver;
		try {
			while (!MainViewModel.PendingSetupSave.IsCompleted || a_vm.Saving) {
				try {
					await MainViewModel.PendingSetupSave.ConfigureAwait(true);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) {
				}
				await Task.Yield();
			}
			leftOver = await Task.Run(() => {
				using var hold = Runner.HoldIfFree() ?? throw new InvalidOperationException("a run is going - move the data when it has ended");
				string? left = null;
				// what the window keeps goes along (its sizes and flags), and what it writes from here on goes to the new place
				UiState.MoveWith(() => left = DataFolder.MoveTo(AppPaths.Data, to), to);
				return left;
			});
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) {
			a_vm.MovingData = false;
			a_errors.Text = "Not moved: " + e.Message;
			return;
		}
		// moved: the program runs on the new folder from its next start, so nothing is written here again before it closes
		HasChanges = false;
		Ask.Tell(a_w, "Moved", WindowWords.F($"Your data is now in {to}.") + (leftOver == null ? "" : $"\n\n{leftOver}") + " " + WindowWords.Word("Tool Runner closes now; start it again."), "Close");
		Application.Current.Shutdown();
	}

	// everything the program keeps to the Recycle Bin, after a plain list of what goes and what stays; the window closes after,
	// since the folder it writes into is gone. Refused while a run goes or one still needs its profile put back.
	// the slow parts (listing every backup folder, recycling) run off the window's thread; the button stays off meanwhile
	static async Task RemoveData(MainWindow a_w, TextBlock a_errors)
	{
		if (HasChanges && !Ask.Show(a_w, "Settings not saved", "You changed settings and did not press Save. Go on without saving them?", "Go on", "Keep editing")) {
			return;
		}
		var keepSetups = Ask.Show(a_w, "Keep your setups?", WindowWords.Word("Your setups (which steps run, in which order, with which settings) can stay, so a new copy of the program " +
			"picks them up again. Everything else the program keeps goes."), "Keep my setups", "Remove them too");
		var plan = await Task.Run(() => DataRemoval.Make(AppPaths.Data, keepSetups));
		if (plan.Refused != null) {
			a_errors.Text = "Not removed: " + plan.Refused + ".";
			return;
		}
		var lines = plan.Remove.Select(p => "• " + p).ToList();
		var text = WindowWords.Word("These go to the Recycle Bin (you can bring them back from there):") + "\n" + string.Join("\n", lines) +
			(plan.Kept.Count > 0 ? "\n\n" + WindowWords.Word("These stay:") + "\n" + string.Join("\n", plan.Kept.Select(p => "• " + p)) : "") +
			"\n\n" + WindowWords.Word("The mods the tools made (the outputs) stay in Mod Organizer - they are part of your load order; remove them there if you want.") +
			"\n\n" + WindowWords.Word("The window closes afterwards. The program file itself stays where it is - delete it yourself when you are done.");
		if (!Ask.Show(a_w, "Remove the program's data", text, "Send them to the Recycle Bin", "Keep everything")) {
			return;
		}
		List<string> failed = [];
		using var hand = a_w.Working(WindowWords.Word("the program's data is being removed"));
		// the window's own watch on the runs folder is let go first: with it, Windows refuses to move the data folder
		a_w.WatchRuns(false);
		try {
			// (with no write of the window's own in between, and none after all of it went: UiState.RemoveWith)
			await Task.Run(() => UiState.RemoveWith(() => {
				failed = DataRemoval.Carry(plan);
				return failed.Count == 0;
			}));
		} catch (InvalidOperationException e) {
			a_w.WatchRuns(true);
			a_errors.Text = "Not removed: " + e.Message + ".";
			return;
		}
		if (failed.Count > 0) {
			a_w.WatchRuns(true);
			a_errors.Text = WindowWords.Word("Some of it could not go to the Recycle Bin:") + "\n" + string.Join("\n", failed);
			return;
		}
		HasChanges = false;
		Ask.Tell(a_w, "Removed", "The program's data is in the Recycle Bin. The window closes now.", "Close");
		Application.Current.Shutdown();
	}

	static readonly JsonSerializerOptions Pretty = JsonWrite.IndentedLetters;

	// one setting: its editor by its kind; the returned function writes it back (or says what is wrong)
	// a_onUnlessSet: a switch the setup does not hold yet shows on (its default), so saving the page keeps it what it was
	static void Field(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, JsonValueKind a_kind = JsonValueKind.String, bool a_onUnlessSet = false,
		string? a_step = null)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var label = new TextBlock { Text = a_label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }
			.Live(TextBlock.ForegroundProperty, "Accent");
		row.Children.Add(label);
		FrameworkElement edit;
		var cur = a_obj[a_key];
		var kind = cur?.GetValueKind() ?? a_kind;
		if (kind is JsonValueKind.True or JsonValueKind.False) {
			var box = new CheckBox { IsChecked = cur?.GetValue<bool>() ?? a_onUnlessSet, VerticalAlignment = VerticalAlignment.Center };
			box.Click += (_, _) => Changed();
			a_apply.Add(() => {
				a_obj[a_key] = box.IsChecked == true;
				return null;
			});
			edit = box;
		} else if (kind == JsonValueKind.Number) {
			// (one line high whatever the label's height: a label of three lines drew its box three lines high)
			var t = new TextBox { Text = cur?.ToString() ?? "", VerticalAlignment = VerticalAlignment.Center };
			a_apply.Add(() => {
				if (t.Text.Trim() == "") {
					a_obj.Remove(a_key);
					return null;
				}
				if (!double.TryParse(t.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, InvariantCulture, out var d) || !(d >= 0)) {
					return WindowWords.Format("{0}: needs a number, 0 or more", WindowWords.Word(a_label));
				}
				// (a whole number is kept as one: past what one holds, the number kept would not be the number typed)
				if (d > 2_000_000_000) {
					return WindowWords.Format("{0}: needs a number below 2,000,000,000", WindowWords.Word(a_label));
				}
				if (d < 1 && a_key.EndsWith("_min", StringComparison.Ordinal)) {
					return WindowWords.Format("{0}: needs 1 minute or more", WindowWords.Word(a_label));
				}
				a_obj[a_key] = d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind == JsonValueKind.Array && (cur == null || (cur as JsonArray)!.All(n => n is JsonValue))) {
			var t = new TextBox { Text = string.Join("\n", (cur as JsonArray ?? []).Select(n => n?.ToString())), AcceptsReturn = true, MinHeight = 54, TextWrapping = TextWrapping.NoWrap };
			a_apply.Add(() => {
				// (a list the setup does not hold and nobody typed into is not written into it as an empty one)
				if (cur == null && t.Text.Trim() == "") {
					return null;
				}
				a_obj[a_key] = new JsonArray(t.Text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s != "").Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind is JsonValueKind.Object or JsonValueKind.Array) {
			var t = new TextBox { Text = cur?.ToJsonString(Pretty) ?? "", AcceptsReturn = true, MinHeight = 80 }.Live(Control.FontFamilyProperty, "MonoFont").Live(Control.FontSizeProperty, "SizeLog");
			a_apply.Add(() => {
				// (emptied: the setting leaves the setup - "" is no JSON, and saving a page nobody typed into said "not valid")
				if (t.Text.Trim() == "") {
					a_obj.Remove(a_key);
					return null;
				}
				try {
					a_obj[a_key] = JsonNode.Parse(t.Text, documentOptions: JsonRead.ByHand);
					return null;
				} catch (JsonException e) {
					return WindowWords.Format("{0}: not valid ({1})", WindowWords.Word(a_label), e.Message);
				}
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else {
			var t = new TextBox { Text = cur?.ToString() ?? "", VerticalAlignment = VerticalAlignment.Center };
			a_apply.Add(() => {
				if (a_key is "output_mod" or "then_move_to_mod" or "executable" && t.Text.Trim() == "") {
					return WindowWords.Format("{0}: cannot be empty", WindowWords.Word(a_label));
				}
				if (t.Text == "" && cur == null) {
					return null;
				}
				a_obj[a_key] = PathKind(a_key, a_step) == null ? t.Text : TypedPath(t.Text);
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		}
		System.Windows.Automation.AutomationProperties.SetName(edit, a_label);
		// a setting that holds a folder, a file or a program: picked with Windows' own picker, or typed
		FrameworkElement shown = edit;
		if (edit is TextBox pathBox && kind == JsonValueKind.String && PathKind(a_key, a_step) is { } pick) {
			var browse = new Button { Content = WindowWords.Word("Browse ..."), Style = (Style)Application.Current.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0),
				ToolTip = WindowWords.Word(pick switch { PathPick.Folder => "Choose the folder", PathPick.File => "Choose the file", _ => "Choose the program" }) };
			// (the five settings of the first page have their own words for it; every other one is named by its label)
			var browseFor = "Browse for " + a_label;
			System.Windows.Automation.AutomationProperties.SetName(browse, WindowWords.Word(browseFor) is var named && named != browseFor ? named :
				WindowWords.Format("Browse for {0}", WindowWords.Word(a_label)));
			browse.Click += async (_, _) => {
				browse.IsEnabled = false;
				try {
					var now = pathBox.Text.Trim();
					// (a folder on a network share that is gone can take a while to answer: never on the window's thread)
					var start = await Task.Run(() => StartFolder(pick, now));
					if (Browse(Window.GetWindow(browse), pick, start, WindowWords.Word(a_label)) is { } chosen) {
						pathBox.Text = chosen;
					}
				} finally {
					browse.IsEnabled = true;
				}
			};
			var both = new DockPanel();
			DockPanel.SetDock(browse, Dock.Right);
			both.Children.Add(browse);
			both.Children.Add(pathBox);
			shown = both;
		}
		Grid.SetColumn(shown, 1);
		row.Children.Add(shown);
		a_host.Children.Add(row);
	}

	// A path as it was typed or pasted: the blanks round it taken off, and the quotes Explorer's "Copy as path" puts round it
	// (kept, the path would name nothing that is there)
	public static string TypedPath(string a_text)
	{
		var t = a_text.Trim();
		return t.Length >= 2 && t[0] == '"' && t[^1] == '"' ? t[1..^1].Trim() : t;
	}

	enum PathPick
	{
		Folder,
		Program,
		File,
	}

	// which settings hold a folder, a file or a program (null: none of them; a step's "executable" is the NAME of its entry in
	// Mod Organizer's Executables list and "tool_writes" a mod's name, no paths). a_step: the step the setting is one of
	static PathPick? PathKind(string a_key, string? a_step = null) => a_key switch {
		"exe" or "exe_path" => PathPick.Program,
		_ when a_key.EndsWith("_exe", StringComparison.Ordinal) => PathPick.Program,
		"instance" or "backups" or "scratch" or "skseLogs" or "output_dir" or "dyndolod" or "reads" => PathPick.Folder,
		"live_settings" => PathPick.File,
		"preset" when a_step != "anvillod" => PathPick.File,
		_ when a_key.EndsWith("_dir", StringComparison.Ordinal) || a_key.EndsWith("Dir", StringComparison.Ordinal) => PathPick.Folder,
		_ => null,
	};

	// the folder the picker opens in: where the setting points now, when that folder is there (null: Windows' own choice)
	static string? StartFolder(PathPick a_pick, string a_now)
	{
		try {
			var start = a_now == "" ? null : a_pick == PathPick.Folder ? a_now : Path.GetDirectoryName(a_now);
			return start != null && Directory.Exists(start) ? start : null;
		} catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException) {
			return null;
		}
	}

	// Windows' own folder or file picker, opened in a_start; the path chosen, or null when it was cancelled
	static string? Browse(Window? a_owner, PathPick a_pick, string? a_start, string a_label)
	{
		var start = a_start;
		if (a_pick == PathPick.Folder) {
			var d = new Microsoft.Win32.OpenFolderDialog { Title = a_label, InitialDirectory = start ?? "" };
			return (a_owner == null ? d.ShowDialog() : d.ShowDialog(a_owner)) == true ? d.FolderName : null;
		}
		var f = new Microsoft.Win32.OpenFileDialog { Title = a_label, Filter = a_pick == PathPick.Program ? FilterOf("Programs (*.exe)", "*.exe") + "|" + FilterOf("All files", "*.*") : FilterOf("All files", "*.*"), InitialDirectory = start ?? "" };
		return (a_owner == null ? f.ShowDialog() : f.ShowDialog(a_owner)) == true ? f.FileName : null;
	}

	// The character the game steps load: the most recent save of any character, or one character's. The list holds the characters
	// found in the profile's saves, most recently played first; a name the setup holds that no save carries stays in it, marked
	const string AnyCharacter = "Most recent save (any character)";

	static void CharacterChoice(Panel a_host, List<Func<string?>> a_apply, JsonObject a_obj, Setup a_setup)
	{
		const string label = "Character to load";
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }
			.Live(TextBlock.ForegroundProperty, "Accent"));
		var current = Setup.Str(a_obj, "character");
		var names = new List<string> { WindowWords.Word(AnyCharacter) };
		if (current != null) {
			names.Add(current);
		}
		var box = new ComboBox { ItemsSource = names.ToList(), SelectedIndex = current == null ? 0 : 1,
			ToolTip = "The game steps load this character's newest save. Characters are read from the saves in this profile." };
		// (filling the list once the saves are read is no change of the player's)
		var filling = false;
		box.SelectionChanged += (_, _) => {
			if (!filling) {
				Changed();
			}
		};
		System.Windows.Automation.AutomationProperties.SetName(box, label);
		a_apply.Add(() => {
			var picked = box.SelectedIndex > 0 ? box.SelectedItem as string : null;
			var name = picked?.EndsWith(NoSaves, StringComparison.Ordinal) == true ? picked[..^NoSaves.Length] : picked;
			if (name == null) {
				a_obj.Remove("character");
			} else {
				a_obj["character"] = name;
			}
			return null;
		});
		Grid.SetColumn(box, 1);
		row.Children.Add(box);
		a_host.Children.Add(row);
		_ = Task.Run(() => {
			try {
				var mo2 = Mo2Instance.Load(a_setup.Mo2Exe, a_setup.Mo2Instance);
				return SaveHeader.Characters(InGame.SavesDir(new Profile(mo2, a_setup.Profile, () => false)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				return new List<string>();
			}
		}).ContinueWith(t => box.Dispatcher.BeginInvoke(() => {
			if (!t.IsCompletedSuccessfully) {
				return;
			}
			// the player may have picked meanwhile: that pick stays picked
			var picked = box.SelectedIndex > 0 ? box.SelectedItem as string : null;
			var found = t.Result;
			var list = new List<string> { WindowWords.Word(AnyCharacter) };
			list.AddRange(found);
			if (current != null && !found.Contains(current, StringComparer.OrdinalIgnoreCase)) {
				list.Add(current + NoSaves);
			}
			filling = true;
			box.ItemsSource = list;
			var at = picked == null ? 0 : list.FindIndex(n => string.Equals(Bare(n), Bare(picked), StringComparison.OrdinalIgnoreCase));
			static string Bare(string a_entry) => a_entry.EndsWith(NoSaves, StringComparison.Ordinal) ? a_entry[..^NoSaves.Length] : a_entry;
			box.SelectedIndex = Math.Max(0, at);
			filling = false;
		}), TaskScheduler.Default);
	}

	static string NoSaves => " (" + WindowWords.Word("no saves in this profile") + ")";

	static void Choice(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, (string Value, string Text)[] a_options)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = a_label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }
			.Live(TextBlock.ForegroundProperty, "Accent"));
		// shown in the window's language; what is saved is each choice's own value, by its place in the list. Each choice is an
		// item of its own: as plain texts, two choices a language file gives the same words would count as one
		var box = new ComboBox { ItemsSource = a_options.Select(o => new ComboBoxItem { Content = WindowWords.Word(o.Text) }).ToList() };
		var cur = Setup.Str(a_obj, a_key);
		box.SelectedIndex = Math.Max(0, Array.FindIndex(a_options, o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)));
		// (the choice in whole words on hover: a box made narrow cuts a long one off)
		box.ToolTip = (box.SelectedItem as ComboBoxItem)?.Content;
		box.SelectionChanged += (_, _) => {
			box.ToolTip = (box.SelectedItem as ComboBoxItem)?.Content;
			Changed();
		};
		System.Windows.Automation.AutomationProperties.SetName(box, a_label);
		a_apply.Add(() => {
			a_obj[a_key] = a_options[Math.Max(0, box.SelectedIndex)].Value;
			return null;
		});
		Grid.SetColumn(box, 1);
		row.Children.Add(box);
		a_host.Children.Add(row);
	}
}
