using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// The settings drawer: the setup's own settings and each step's, in plain names; edited on a copy, checked, saved with a
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
		["tool_dir"] = "The tool's folder",
		["tool_writes"] = "The folder the tool writes into",
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
	};

	// an edit not saved yet (the window asks before the drawer closes over it)
	public static bool HasChanges { get; private set; }

	static void Changed() => HasChanges = true;

	// The setup's file the settings shown were opened for (null: none). The window stays open while another setup is picked
	// in the main window: what it shows is then another setup's than the one picked.
	static string? s_openedFor;

	public static bool IsFor(Setup? a_setup) => string.Equals(s_openedFor, a_setup?.File, StringComparison.OrdinalIgnoreCase);

	// Why settings opened for one setup are not saved while another is picked (null: they were opened for the picked one).
	// Saved all the same, they were written over the picked setup's own (CodeRabbit on pull request #35).
	public static string? NotThisSetup(string a_openedFor, string a_openedName, Setup? a_picked) =>
		MainViewModel.StillShown(a_openedFor, a_picked?.File) ? null
			: $"Not saved: these are the settings of \"{a_openedName}\", and {(a_picked == null ? "no setup" : $"\"{a_picked.Name}\"")} is picked in the main window now. " +
			  $"Pick \"{a_openedName}\" again to save them (what you typed stays here), or press Undo changes to see the picked setup's.";

	// settings a step reads that its file may not list yet (shown empty, with the default the step uses)
	static readonly Dictionary<string, (string Key, JsonValueKind Kind, string Default)[]> Known = new(StringComparer.Ordinal) {
		["shaders"] = [("build_for_off", JsonValueKind.False, "off"), ("on_crash", JsonValueKind.String, "ask"), ("crash_wait_min", JsonValueKind.Number, "30"),
			("idle_s", JsonValueKind.Number, "45"), ("quiet_min", JsonValueKind.Number, "5"), ("min_files", JsonValueKind.Number, "50"), ("max_restarts", JsonValueKind.Number, "3")],
		["grass"] = [("stall_min", JsonValueKind.Number, "10"), ("min_files", JsonValueKind.Number, "100")],
		["pgpatcher"] = [("pglite_check", JsonValueKind.False, "off")],
		["dyndolod"] = [("no_terrain_underside", JsonValueKind.False, "off"), ("preset", JsonValueKind.String, "none")],
		["landscapefixer"] = [("min_files", JsonValueKind.Number, "3")],
		["acmos"] = [("hidden", JsonValueKind.True, "on")],
		["synthesis"] = [("build_min", JsonValueKind.Number, "15")],
	};

	// the mods a LOD step builds from and the LOD tool's own help, each a page to open (the runner never guesses their values)
	static void LodBlock(MainWindow a_w, Setup a_setup, string a_step, Panel a_host)
	{
		a_host.Children.Add(new TextBlock { Text = "Before you set it up: LOD can come out too bright, too dark or missing when its settings do not match the mods it is " +
			"built from. Each of those mods' own pages (and the LOD tool's help) says what it needs - the runner never guesses another author's values.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 6) });
		static Button Link(MainWindow a_win, string a_text, string a_url)
		{
			var b = new Button { Content = a_text, Style = (Style)a_win.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = a_url };
			b.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(a_url) { UseShellExecute = true });
			return b;
		}
		var docs = new WrapPanel { Margin = new Thickness(-10, 0, 0, 6) };
		a_host.Children.Add(docs);
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6), Foreground = Brushes2.Get("Warn") };
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
			foreach (var (title, url) in t.Result.Item1) {
				docs.Children.Add(Link(a_w, title, url));
			}
			note.Text = t.Result.Item2 ?? "";
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
				rows.Children.Add(new TextBlock { Text = hits.Count == 0 ? "No ticked mod changes what it builds from." :
					$"{hits.Count} ticked mod(s) change what it builds from - check each one's page for its LOD settings:", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 4) });
				foreach (var h in hits) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (h.Page != null) {
						var open = Link(a_w, "Open its page", h.Page);
						DockPanel.SetDock(open, Dock.Right);
						row.Children.Add(open);
					}
					row.Children.Add(new TextBlock { Text = $"{h.Mod} ({h.What}){(h.Page == null ? " - no Nexus page recorded" : "")}", TextWrapping = TextWrapping.Wrap,
						VerticalAlignment = VerticalAlignment.Center });
					rows.Children.Add(row);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				rows.Children.Clear();
				rows.Children.Add(new TextBlock { Text = "Could not read the profile: " + e.Message, TextWrapping = TextWrapping.Wrap });
			} finally {
				find.IsEnabled = true;
			}
		};
		a_host.Children.Add(find);
		a_host.Children.Add(rows);
	}

	// his checklist 478: how much room this setup's run backups take, and a clean-up to the newest few without waiting for a run
	static void BackupsBlock(MainWindow a_w, MainViewModel a_vm, Panel a_host)
	{
		var setup = a_vm.Setup!;
		var say = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0),
			Text = "Measuring this setup's run backups ..." };
		var tidy = new Button { Content = "Keep only the newest now", Style = (Style)a_w.FindResource("Ghost"), IsEnabled = false };
		var open = new Button { Content = "Open the backups folder", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		open.Click += (_, _) => MainViewModel.OpenFolder(setup.BackupDir);
		static string Gb(long a_b) => a_b >= 1L << 30 ? $"{a_b / (double)(1L << 30):0.0} GB" : $"{a_b / (double)(1L << 20):0} MB";
		async void Measure()
		{
			var all = await Task.Run(() => {
				try {
					return Runner.BackupsOf(setup);
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					return [];
				}
			});
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			say.Text = all.Count == 0 ? "This setup has no run backups yet." :
				$"This setup's run backups: {all.Count} run(s), {Gb(all.Sum(b => b.Bytes))} in all - " +
				(keep > 0 ? $"the newest {keep} are kept after each run." : "every one is kept (Backups kept is 0).");
			tidy.IsEnabled = keep > 0 && all.Count > keep;
			tidy.ToolTip = $"Removes all but the newest {keep} now (the number saved above); a run's own backup and one an unfinished run needs are never touched";
		}
		tidy.Click += async (_, _) => {
			var keep = Setup.Int(setup.Root, "keepBackups") ?? 5;
			if (!Ask.Show(a_w, "Remove old backups", $"All but the newest {keep} of this setup's run backups are deleted for good (they are copies of outputs and settings " +
			        "from runs that finished long ago). A backup an unfinished run still needs is kept.", "Remove them", "Keep them")) {
				return;
			}
			tidy.IsEnabled = false;
			using var hand = a_w.Working("old backups are being removed");
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

	// (a setting saved under an empty key still gets a name: its editor is announced by it)
	static string Label(string a_key) => Labels.TryGetValue(a_key, out var l) ? l : a_key.Length == 0 ? "Unnamed setting" : char.ToUpperInvariant(a_key[0]) + a_key[1..].Replace('_', ' ');

	// his checklist 471: the program's own new versions - kept for the whole program, saved the moment it is ticked
	static void NewVersions(MainWindow a_w, StackPanel a_stack)
	{
		a_stack.Children.Add(new TextBlock { Text = "UPDATES", Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });
		var newVersions = new CheckBox { Content = "Tell me when a new version is out", IsChecked = !ProgramUpdate.IsOff(AppPaths.Data), Margin = new Thickness(0, 0, 0, 4) };
		newVersions.Click += (_, _) => ProgramUpdate.SetOff(AppPaths.Data, newVersions.IsChecked != true);
		a_stack.Children.Add(newVersions);
		a_stack.Children.Add(new TextBlock { Text = "At most once a day, when the window opens, it asks its own download page which version is newest - nothing else is sent, " +
			"and nothing is downloaded: the page opens in your browser when you choose.", Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap });
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
		// His list of 2026-11-16: the settings in sub-menus. Each page is a panel of its own; the list on the left shows one at
		// a time, and the search shows the matching rows of every page at once. `stack` is the page the rows below go into.
		var pages = new List<(string Name, StackPanel Panel)>();
		StackPanel Page(string a_name)
		{
			var p = new StackPanel();
			p.Children.Add(new TextBlock { Text = a_name.ToUpperInvariant(), Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 0, 0, 10), Tag = HeadTag });
			pages.Add((a_name, p));
			return p;
		}
		StackPanel setupPage = Page("Setup"), runPage = Page("Run"), gamePage = Page("Game steps"), backupPage = Page("Backups"), alertPage = Page("Alerts and power"),
			stepPage = Page("Steps"), toolPage = Page("Tools"), changesPage = Page("MO2 Changelog"), programPage = Page("Program");
		var cats = new ListBox { Width = 168, Margin = new Thickness(0, 0, 16, 0), ItemsSource = pages.Select(p => p.Name).ToList(), SelectedIndex = s_page < pages.Count ? s_page : 0,
			VerticalAlignment = VerticalAlignment.Top, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent };
		System.Windows.Automation.AutomationProperties.SetName(cats, "Settings pages");
		s_cats = cats;
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
		var errors = new TextBlock { Foreground = Brushes2.Get("Bad"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
		s_errors = errors;
		var apply = new List<Func<string?>>();

		void Head(string a_t) => stack.Children.Add(new TextBlock { Text = a_t, Style = (Style)a_w.FindResource("H2"), Margin = new Thickness(0, 18, 0, 8), Tag = HeadTag });

		// his checklist 475: a search over every setting (Ctrl+F while Settings is open)
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


		// his checklist 466: a copy another player can import - its steps and options, nothing of this PC
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
				if (Ask.Show(a_w, "Shared copy made", $"It is ready: {Path.GetFileName(file)}\n\nWhat the other player sets themselves:\n" +
				    (gone.Count == 0 ? "nothing" : string.Join("\n", gone.Select(g => "- " + g))) + "\n\nThey bring it in with the import command (see the readme).",
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
		// his checklist 457: the character whose saves the in-game steps load (the names are read off the saves, off the window's thread)
		Field(stack, apply, "Character (empty = the newest save)", work, "character");
		var whoHint = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(230, -6, 0, 10) };
		stack.Children.Add(whoHint);
		var forSaves = a_vm.Setup!;
		_ = Task.Run(() => {
			try {
				var mo2 = Mo2Instance.Load(forSaves.Mo2Exe, forSaves.Mo2Instance);
				return SaveHeader.Characters(InGame.SavesDir(new Profile(mo2, forSaves.Profile, () => false)));
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				return new List<string>();
			}
		}).ContinueWith(t => whoHint.Dispatcher.BeginInvoke(() => whoHint.Text = t.Result.Count == 0 ? "No saves found in this profile yet." :
			"Characters with saves here, most recently played first: " + string.Join(", ", t.Result.Take(12))), TaskScheduler.Default);
		// ---- Game steps (the rest of that page)
		// his checklist 460
		work["quietGame"] ??= false;
		Field(gamePage, apply, "Run game steps in a small, muted window (your game settings are put back after)", work, "quietGame", JsonValueKind.True);
		// his checklist 464
		work["copySaves"] ??= true;
		Field(gamePage, apply, "Back up my saves before a game step", work, "copySaves", JsonValueKind.True);
		// his checklist 459: off unless asked for - it changes the game's own saving switch in the run's game
		work["forceSave"] ??= false;
		Field(gamePage, apply, "Force the save when a mod blocks saving (the run's game only)", work, "forceSave", JsonValueKind.True);
		work["guidedReminderMin"] ??= 0;
		Field(gamePage, apply, "Remind me when a game step waits for me, every (minutes; 0 = off)", work, "guidedReminderMin", JsonValueKind.Number);
		// his word of 2026-11-21: a pop-up of the game is closed at once, whatever it is - a setup may leave the ones the run
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
		// his checklist 270 ("all 3."): the preview before a run
		work["previewBeforeRun"] ??= true;
		Field(runPage, apply, "Show the run preview before it starts", work, "previewBeforeRun", JsonValueKind.True);
		// his checklist 650 ("user choice."): on by default - the tools after them build on what the skipped tools made
		work[Runner.KeepEarlierSetting] ??= true;
		Field(runPage, apply, "When a run starts at a later step, leave the earlier tools' outputs on (they are not run again)", work, Runner.KeepEarlierSetting, JsonValueKind.True);
		Field(runPage, apply, "Check on a tool that sits idle for (minutes)", work, "idleMinutes", JsonValueKind.Number);
		Field(runPage, apply, "Free disk space needed (GB)", work, "minFreeGb", JsonValueKind.Number);
		// the forecast of the room a run takes: on, a run that will not fit is not started; off, it is only told
		work["diskForecastStops"] ??= true;
		Field(runPage, apply, "Do not start a run that will not fit on the disk (off: warn only)", work, "diskForecastStops", JsonValueKind.True);
		// his checklist 479: the tools at a lower priority, so the PC stays smooth while they work (a run may take a little longer)
		work["lowerPriority"] ??= false;
		Field(runPage, apply, "Run tools at low priority (smoother PC, slightly longer run)", work, "lowerPriority", JsonValueKind.True);
		// his checklist 477
		work["keepMo2Open"] ??= false;
		Field(runPage, apply, "Keep MO2 open during a run (fewer restarts; needs the MO2 Tools menu entry)", work, "keepMo2Open", JsonValueKind.True);
		Field(runPage, apply, "Commands before a run (one per line; each must succeed)", work, "beforeRun", JsonValueKind.Array);
		Field(runPage, apply, "Commands after a run (one per line)", work, "afterRun", JsonValueKind.Array);

		// ---- Backups: only the last few runs' kept (his checklist 270), as a zip (454), their size and a clean-up (478)
		work["keepBackups"] ??= 5;
		Field(backupPage, apply, "Backups to keep (runs; 0 = all)", work, "keepBackups", JsonValueKind.Number);
		BackupsBlock(a_w, a_vm, backupPage);
		work["zipBackups"] ??= true;
		Field(backupPage, apply, "Zip old outputs in the backup (much smaller)", work, "zipBackups", JsonValueKind.True);

		// ---- Alerts and power
		var alerts = work["alerts"] as JsonObject ?? (JsonObject)(work["alerts"] = new JsonObject { ["notification"] = true, ["sound"] = true, ["flash"] = false });
		Field(alertPage, apply, "Windows notification", alerts, "notification", JsonValueKind.True);
		Field(alertPage, apply, "Sound", alerts, "sound", JsonValueKind.True);
		Field(alertPage, apply, "Flash the taskbar button", alerts, "flash", JsonValueKind.True);
		// his checklist 461 / 463: both optional, off unless set
		Choice(alertPage, apply, "After the run, the PC", work, "afterRunPower", [("none", "stays on"), ("sleep", "sleeps, as Windows is set (one minute's notice)"),
			("shutdown", "shuts down (one minute's notice)")]);
		work["afterRunPowerOnlyFinished"] ??= true;
		Field(alertPage, apply, "Only after a finished run (a failed run keeps the PC on)", work, "afterRunPowerOnlyFinished", JsonValueKind.True);

		// ---- MO2 Changelog: a profile only tests use, or test mods, can be left out of the view (nothing is lost)
		var changes = work["changes"] as JsonObject ?? (JsonObject)(work["changes"] = new JsonObject());
		Field(changesPage, apply, "Hide these profiles (one per line)", changes, "hideProfiles", JsonValueKind.Array);
		Field(changesPage, apply, "Hide mods whose name starts with (one per line)", changes, "hideModsStartingWith", JsonValueKind.Array);
		stack = programPage;

		// the window's look for every setup (his ask 2026-10-27: themes his design agent makes from "Theme template.json")
		// his checklist 476
		Head("LANGUAGE");
		var languageNote = new TextBlock { Text = "English, or a language file in the Languages folder. A file holds each English text and its translation; what it leaves out stays English. Nothing is translated yet: this is ready for whoever wants to.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		var languages = new ComboBox { MinWidth = 220, Margin = new Thickness(0, 0, 10, 0), ItemsSource = WindowWords.Available(), SelectedItem = WindowWords.Picked() };
		System.Windows.Automation.AutomationProperties.SetName(languages, "The window's language");
		languages.SelectionChanged += (_, _) => {
			if (languages.SelectedItem is string picked && picked != WindowWords.Picked()) {
				try {
					WindowWords.Pick(picked);
					languageNote.Text = "It applies the next time this program starts.";
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					a_vm.Say("The language could not be kept: " + e.Message, "bad");
				}
			}
		};
		var makeLanguage = new Button { Content = "Make a language file ...", Style = (Style)a_w.FindResource("Ghost"), ToolTip = "A file with the texts this window shows now, ready to translate. Press it again with History open to add those too." };
		makeLanguage.Click += (_, _) => {
			try {
				var file = WindowWords.WriteTemplate(a_w);
				a_vm.Say("Made: " + file, "good");
				MainViewModel.OpenFolder(WindowWords.Folder);
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

		// his checklist 433-435: add-ons from other modders, each allowed by the player
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
				addOnNote.Text = found.Count == 0 ? "No ticked mod of this profile holds an add-on." : $"{found.Count} add-on file(s) in this profile's ticked mods.";
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
							var order = AddOns.AddToSetup(current.Root, addOn, current.Profile);
							current.Save();
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
						$"{a.Name}, by {a.Author}, in {hit.Mod}" + (AddOns.InSetup(current, a) ? " - in this setup" : "") :
						$"{hit.File}: {string.Join("; ", hit.Problems)}" });
					addOnRows.Children.Add(row);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
				a_vm.Say("The add-ons could not be looked for: " + e.Message, "bad");
			} finally {
				lookAddOns.IsEnabled = true;
			}
		};
		checkAddOn.Click += (_, _) => {
			var d = new Microsoft.Win32.OpenFileDialog { Filter = "Add-on file|*.json", Title = "Your add-on file" };
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

		// his checklist 468: the program in Mod Organizer's Tools menu
		if (a_vm.Setup is { } menuSetup && Environment.ProcessPath is { } program) {
			try {
				var menuMo2 = Mo2Instance.Load(menuSetup.Mo2Exe, menuSetup.Mo2Instance);
				Head("MO2 TOOLS MENU");
				var menuNote = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
					Text = Mo2MenuEntry.IsCurrent(menuMo2, program) ? "This program is in Mod Organizer's Tools menu. It opens here on the profile Mod Organizer shows." :
						"An entry in Mod Organizer's Tools menu that opens this program on the profile Mod Organizer shows. It is one small file in Mod Organizer's plugins folder." };
				stack.Children.Add(menuNote);
				if (!Mo2MenuEntry.IsCurrent(menuMo2, program)) {
					var addMenu = new Button { Content = "Add it to the Tools menu", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
					addMenu.Click += (_, _) => {
						try {
							Mo2MenuEntry.Add(menuMo2, program);
							menuNote.Text = "Added. It shows the next time Mod Organizer starts.";
							addMenu.IsEnabled = false;
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
							a_vm.Say("The entry could not be added: " + e.Message, "bad");
						}
					};
					stack.Children.Add(addMenu);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
				a_vm.Say("Mod Organizer could not be read for its plugins: " + e.Message, "warn");
			}
		}

		// his checklist 492: our add-on, only where Root Builder is in this setup's Mod Organizer
		if (a_vm.Setup is { } rootSetup) {
			try {
				var rootMo2 = Mo2Instance.Load(rootSetup.Mo2Exe, rootSetup.Mo2Instance);
				var state = RootBuilderAddOn.StateOf(rootMo2);
				if (state != RootBuilderAddOn.State.NoRootBuilder) {
					Head("ROOT BUILDER");
					var rootNote = new TextBlock { Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
						Text = "Runs wait for Root Builder to put its files in place and to take them out. Our add-on, Root Builder Profiles, keeps what Root Builder leaves behind per profile " +
						"and offers to move presets no mod owns out of the game folder. It is optional, and it needs Root Builder itself." };
					stack.Children.Add(rootNote);
					if (state == RootBuilderAddOn.State.Current) {
						rootNote.Text += " It is in Mod Organizer.";
					} else {
						var addOn = new Button { Content = state == RootBuilderAddOn.State.NotAdded ? "Add Root Builder Profiles ..." : "Update Root Builder Profiles ...", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
						addOn.Click += (_, _) => {
							if (!Ask.Show(a_w, RootBuilderAddOn.Title, "It goes into Mod Organizer's plugins folder, and Mod Organizer loads it the next time it starts. To take it out, delete its folder there, rootbuilderprofiles.", "Add it", "Not now")) {
								return;
							}
							try {
								RootBuilderAddOn.Add(rootMo2);
								rootNote.Text = "Root Builder Profiles is in Mod Organizer now. It loads the next time Mod Organizer starts.";
								addOn.IsEnabled = false;
							} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
								a_vm.Say("The add-on could not be added: " + e.Message, "bad");
							}
						};
						stack.Children.Add(addOn);
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
				// a setup whose Mod Organizer does not read says so in its check: nothing to offer here
				a_vm.Say("Mod Organizer could not be read for its plugins: " + e.Message, "warn");
			}
		}

		// his ask 2026-10-27: aware of a tool's updates, the download from the runner (its page, in the player's browser)
		Head("TOOL UPDATES");
		// his list of 2026-11-16: the Check button looks for updates too (asked of the tools' pages only on that press)
		work["checkUpdates"] ??= true;
		Field(stack, apply, "Check (F5) also looks for updates", work, "checkUpdates", JsonValueKind.True);
		var updNote = new TextBlock { Text = "Asks each tool's own page (Nexus, GitHub) for its newest version - only when you press this.",
			Style = (Style)a_w.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 8) };
		var updRows = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
		var updCheck = new Button { Content = "Check its tools for updates", Style = (Style)a_w.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left };
		updCheck.Click += async (_, _) => {
			updCheck.IsEnabled = false;
			updRows.Children.Clear();
			updNote.Text = "Asking the tools' pages ...";
			try {
				var setup = a_vm.Setup!;
				var res = await Task.Run(async () => await ToolUpdates.Check(setup, Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance), CancellationToken.None));
				foreach (var r in res) {
					var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (r.Newer) {
						var get = new Button { Content = "Open its download page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0) };
						var page = r.Page;
						get.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
						DockPanel.SetDock(get, Dock.Right);
						row.Children.Add(get);
					}
					var said = r.Problem ?? (r.Newer ? $"a NEWER version is out: {r.Newest} (yours {r.Yours})" : $"up to date ({r.Yours})");
					row.Children.Add(new TextBlock { Text = $"{r.Tool}: {said}", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
						Foreground = Brushes2.Get(r.Newer ? "Warn" : r.Problem != null ? "Muted" : "Text") });
					updRows.Children.Add(row);
				}
				updNote.Text = ToolUpdates.Summary(res);
			} catch (Exception e) {
				updNote.Text = "Could not check: " + e.Message;
			} finally {
				updCheck.IsEnabled = true;
			}
		};
		// his checklist 445: what a tool's update may have moved, looked for before a run needs it
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
			if (a_vm.Setup is not { } testSetup || !Ask.Show(a_w, "Test my tools", "Each tool this program drives through its window opens through Mod Organizer, up to the window a run presses, and closes again. " +
				"Nothing is pressed and nothing is built. Mod Organizer closes for it, and the LOD tools read your whole load order first, so it can take a while.", "Test them", "Not now")) {
				return;
			}
			s_testRows?.Children.Clear();
			using var stop = new CancellationTokenSource();
			s_testing = stop;
			s_showTest?.Invoke();
			using var hand = a_w.Working("your tools are being tested");
			try {
				var results = await Task.Run(() => ToolProof.TestAll(testSetup, RunLog.Null, stop.Token));
				// (into the drawing on screen now: Settings may have been drawn again since the test began, or closed - then the
				// results are said where the window says its messages)
				if (s_testRows is { } rows) {
					foreach (var r in results) {
						rows.Children.Add(new TextBlock { Text = r.Text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes2.Get(r.Good ? "Text" : "Bad"), Margin = new Thickness(0, 2, 0, 2) });
					}
				} else {
					a_vm.Say("The test of your tools: " + string.Join(" | ", results.Select(r => r.Text)), results.All(r => r.Good) ? "good" : "bad");
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
		var stepHost = new StackPanel();
		var stepApply = new List<Func<string?>>();
		pick.SelectionChanged += (_, _) => {
			// the step on screen keeps its fields until every value reads (a wrong one stays where it can be fixed)
			var wrong = stepApply.Select(f => f()).OfType<string>().ToList();
			if (wrong.Count > 0) {
				errors.Text = "Fix this step first:\n" + string.Join("\n", wrong);
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
			// his checklist 475: help on every step - the tool's own page (its author's instructions), opened in the browser
			if (HelpPage(aboutKey) is { } help) {
				var page = new Button { Content = "Its own page", Style = (Style)a_w.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0), ToolTip = help.Url,
					VerticalAlignment = VerticalAlignment.Top };
				System.Windows.Automation.AutomationProperties.SetHelpText(page, help.What);
				page.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(help.Url) { UseShellExecute = true });
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
				Field(stepHost, stepApply, Label(key), o, key, val?.GetValueKind() ?? JsonValueKind.String);
			}
			var stepKey = Setup.Str(steps[pick.SelectedIndex], "step") ?? "";
			// his ask 2026-10-27: the tool's settings from its OWN window, kept for every run (never a copy of its window here).
			// Only while the setup picked in the main window is the one these settings were opened for: with another one
			// picked, the button stood among this setup's steps and opened - and learned for - the other setup's tool
			if (ToolSettings.CanLearn(new StepSettings(o)) && a_vm.Setup is { } setupNow && IsFor(setupNow)) {
				var learned = ToolSettings.Load(setupNow, stepKey);
				var name = StepCatalog.Name(stepKey);
				var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)a_w.FindResource("MutedText"), Margin = new Thickness(0, 4, 0, 6),
					Text = learned == null ? "Its settings are not learned yet: it runs with whatever its own window last saved." :
						$"Its settings were learned {learned.At.ToLocalTime().ToString("MMM d HH:mm", InvariantCulture)} ({learned.Files.Count} file(s)) - every run uses them." };
				var learn = new Button { Content = "Learn its settings (opens its own window)", Style = (Style)a_w.FindResource("Ghost"),
					HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 12) };
				// while a tool is being opened (or is open) to learn its settings, this button stops that wait instead. The wait
				// outlives this step's view - another step picked, Save, Undo - so every learn button stops the one that is going,
				// and none starts a second
				var learnText = learn.Content;
				void ShowLearn()
				{
					if (s_learning is { } going) {
						learn.Content = LearningHere(setupNow, stepKey) ? "Stop waiting" : $"Stop waiting for {s_learningName}";
						learn.IsEnabled = !going.Stopping;
					} else {
						learn.Content = learnText;
						learn.IsEnabled = true;
					}
				}
				// the step whose tool is open: this view's line now says how it goes
				if (s_learning is { } still && LearningHere(setupNow, stepKey)) {
					s_learningSays = t => status.Text = t;
					status.Text = still.Opened ? $"{name} is open - set it up, save, and close it ..." : $"waiting to open {name} ...";
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
					if (!Ask.Show(a_w, "Learn its settings", $"{name} opens through Mod Organizer (Mod Organizer is closed first if it is open). Set it up the way every run " +
					        "should use it, save, and close it - its settings are then kept with this setup. To change them later, do this again.", "Open it", "Not now")) {
						return;
					}
					using var now = new ToolOpen();
					s_learning = now;
					s_learningSetup = setupNow.File;
					s_learningStep = stepKey;
					s_learningName = name;
					s_learningSays = t => status.Text = t;
					s_showLearn?.Invoke();
					status.Text = $"opening {name} ...";
					ToolOpen.Result result;
					try {
						using (a_w.Working($"{name} is open to learn its settings")) {
							result = await now.Go(setupNow, stepKey,
								why => Ask.Show(a_w, $"{name} cannot open yet", $"{ToolsDialog.Capital(why)}.\n\n{name} opens through Mod Organizer, and Mod Organizer is closed first - " +
								                     $"never while that is open. Close it and try again, or wait here: {name} opens by itself once it has ended, and you can stop " +
								                     "waiting at any time.", "Wait for it", "Not now"),
								line => a_w.Dispatcher.BeginInvoke(() => {
									if (s_learning == now) {
										s_learningSays?.Invoke(now.Opened ? $"{name} is open - set it up, save, and close it ..." : line);
									}
								}));
						}
					} finally {
						s_learning = null;
					}
					var says = s_learningSays;
					s_learningSays = null;
					var outcome = (result.Learned is { } l
						? $"Learned: {l.Files.Count} settings file(s) ({string.Join(", ", l.Files.Select(f => Path.GetFileName(f.Live)))}) - every run uses them."
						: result.How == ToolOpen.Ending.Failed ? "Not learned: " + result.Why : "Not learned: " + ToolsDialog.Outcome(name, result)) + ToolsDialog.StillOn(now.StillOn);
					s_showLearn?.Invoke();
					// on the view that shows that step now, and on the main window's message line too (the settings window may be
					// on another step by now, or closed)
					says?.Invoke(outcome);
					a_vm.Say($"{name}: {outcome}", result.Learned != null ? "" : "warn");
				};
				stepHost.Children.Add(status);
				stepHost.Children.Add(learn);
			}
			// his lesson 2026-10-27 (checklist 271): LOD settings depend on the mods it is built from - their own pages say which
			if (LodGuide.Applies(stepKey)) {
				LodBlock(a_w, a_vm.Setup!, stepKey, stepHost);
			}
			foreach (var (key, kind, def) in Known.GetValueOrDefault(stepKey, []).Append((Key: "deadline_min", Kind: JsonValueKind.Number, Default: "the step's own"))
				         .Where(k => !o.ContainsKey(k.Key))) {
				Field(stepHost, stepApply, $"{Label(key)} (default {def})", o, key, kind, def == "on");
			}
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
				// memory back to what is on disk; the drawer keeps what was typed for another try
				a_vm.LoadSteps();
				errors.Text = "Not saved: " + a_vm.Message;
				return;
			}
			a_vm.LoadSteps();
			errors.Text = "";
			a_w.ShowPanel("settings", true);
		};
		undo.Click += (_, _) => a_w.ShowPanel("settings", true);
		folders.Click += (_, _) => MainViewModel.OpenFolder(AppPaths.Data);
		// his checklist 484: a clean removal of everything the program keeps (to the Recycle Bin, never erased)
		var remove = new Button { Content = "Remove its data ...", Style = (Style)a_w.FindResource("Danger"), Margin = new Thickness(10, 0, 0, 0),
			ToolTip = "Sends the program's own data and its run backups to the Recycle Bin - the mods the tools made stay in Mod Organizer" };
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
		bar.Children.Add(remove);
		var where = new TextBlock { Text = "Data folder (setups, runs, backups): " + AppPaths.Data, Style = (Style)a_w.FindResource("MutedText"),
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
		DockPanel.SetDock(cats, Dock.Left);
		dock.Children.Add(cats);
		dock.Children.Add(scroller);
		return dock;
	}

	const string HeadTag = "head";
	static TextBox? s_find;
	// the page last looked at (kept while the program runs, so Save and Undo come back to it)
	static int s_page;

	static ListBox? s_cats;

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
		Ask.Show(a_over, $"Stop waiting for {s_learningName}?", $"{s_learningName} is open. Its settings are kept once you save and close it there.\n\n" +
			$"If you stop waiting, {s_learningName} stays open, but nothing it saves is kept with this setup.", "Stop waiting", "Keep waiting");

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
		s_showTest = null;
		s_testRows = null;
	}

	// the off-screen pictures only: the open settings turned to page a_page; its name, or null when there is no such page
	public static string? ShowPage(int a_page)
	{
		if (s_cats is not { } cats || a_page < 0 || a_page >= cats.Items.Count) {
			return null;
		}
		cats.SelectedIndex = a_page;
		return cats.Items[a_page] as string;
	}

	// the search's line under its box: how many settings on screen hold the word, and the other steps whose settings hold it (a
	// step's own settings are on screen only while it is open on the Steps page)
	static string FindHint(int a_hits, string a_q, IReadOnlyList<JsonObject> a_steps, int a_open)
	{
		if (a_q == "") {
			return "Find a setting - type a word (Ctrl+F)";
		}
		var others = StepsWith(a_steps, a_q, a_open);
		return (a_hits == 0 ? "No setting here has that word." : $"{a_hits} setting(s) found.") +
			(others.Count == 0 ? "" : $" Also in the settings of: {string.Join(", ", others)} (pick it on the Steps page).");
	}

	// the steps (all but a_skip) whose settings hold a_q - in a setting's name, its label or its value
	public static List<string> StepsWith(IReadOnlyList<JsonObject> a_steps, string a_q, int a_skip)
	{
		bool Has(string? a_t) => (a_t ?? "").Contains(a_q, StringComparison.OrdinalIgnoreCase);
		bool Holds(JsonNode? a_v) => a_v switch {
			JsonValue v => v.TryGetValue<string>(out var s) && Has(s),
			JsonArray a => a.Any(Holds),
			_ => false,
		};
		if (a_q.Trim() == "") {
			return [];
		}
		return a_steps.Select((s, i) => (s, i)).Where(x => x.i != a_skip && x.s["settings"] is JsonObject o &&
				o.Any(p => Has(p.Key) || Has(Label(p.Key)) || Holds(p.Value)))
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
				yield return i.ToString() ?? "";
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

	// everything the program keeps to the Recycle Bin, after a plain list of what goes and what stays; the window closes after,
	// since the folder it writes into is gone. Refused while a run goes or one still needs its profile put back.
	// the slow parts (listing every backup folder, recycling) run off the window's thread; the button stays off meanwhile
	static async Task RemoveData(MainWindow a_w, TextBlock a_errors)
	{
		if (HasChanges && !Ask.Show(a_w, "Settings not saved", "You changed settings and did not press Save. Go on without saving them?", "Go on", "Keep editing")) {
			return;
		}
		var keepSetups = Ask.Show(a_w, "Keep your setups?", "Your setups (which steps run, in which order, with which settings) can stay, so a new copy of the program " +
			"picks them up again. Everything else the program keeps goes.", "Keep my setups", "Remove them too");
		var plan = await Task.Run(() => DataRemoval.Make(AppPaths.Data, keepSetups));
		if (plan.Refused != null) {
			a_errors.Text = "Not removed: " + plan.Refused + ".";
			return;
		}
		var lines = plan.Remove.Select(p => "• " + p).ToList();
		var text = "These go to the Recycle Bin (you can bring them back from there):\n" + string.Join("\n", lines) +
			(plan.Kept.Count > 0 ? "\n\nThese stay:\n" + string.Join("\n", plan.Kept.Select(p => "• " + p)) : "") +
			"\n\nThe mods the tools made (the outputs) stay in Mod Organizer - they are part of your load order; remove them there if you want." +
			"\n\nThe window closes afterwards. The program file itself stays where it is - delete it yourself when you are done.";
		if (!Ask.Show(a_w, "Remove the program's data", text, "Send them to the Recycle Bin", "Keep everything")) {
			return;
		}
		List<string> failed;
		using var hand = a_w.Working("the program's data is being removed");
		try {
			failed = await Task.Run(() => DataRemoval.Carry(plan));
		} catch (InvalidOperationException e) {
			a_errors.Text = "Not removed: " + e.Message + ".";
			return;
		}
		if (failed.Count > 0) {
			a_errors.Text = "Some of it could not go to the Recycle Bin:\n" + string.Join("\n", failed);
			return;
		}
		HasChanges = false;
		Ask.Tell(a_w, "Removed", "The program's data is in the Recycle Bin. The window closes now.", "Close");
		Application.Current.Shutdown();
	}

	static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

	// one setting: its editor by its kind; the returned function writes it back (or says what is wrong)
	// a_onUnlessSet: a switch the setup does not hold yet shows on (its default), so saving the page keeps it what it was
	static void Field(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, JsonValueKind a_kind = JsonValueKind.String, bool a_onUnlessSet = false)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		var label = new TextBlock { Text = a_label, Foreground = Brushes2.Get("Accent"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
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
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (t.Text.Trim() == "") {
					a_obj.Remove(a_key);
					return null;
				}
				if (!double.TryParse(t.Text.Trim().Replace(',', '.'), System.Globalization.NumberStyles.Float, InvariantCulture, out var d) || !(d >= 0)) {
					return $"{a_label}: needs a number, 0 or more";
				}
				// (a whole number is kept as one: past what one holds, the number kept would not be the number typed)
				if (d > 2_000_000_000) {
					return $"{a_label}: needs a number below 2,000,000,000";
				}
				if (d < 1 && a_key.EndsWith("_min", StringComparison.Ordinal)) {
					return $"{a_label}: needs 1 minute or more";
				}
				a_obj[a_key] = d == Math.Floor(d) ? JsonValue.Create((long)d) : JsonValue.Create(d);
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind == JsonValueKind.Array && (cur == null || (cur as JsonArray)!.All(n => n is JsonValue))) {
			var t = new TextBox { Text = string.Join("\n", (cur as JsonArray ?? []).Select(n => n?.ToString())), AcceptsReturn = true, MinHeight = 54, TextWrapping = TextWrapping.NoWrap };
			a_apply.Add(() => {
				a_obj[a_key] = new JsonArray(t.Text.Replace("\r", "").Split('\n').Select(s => s.Trim()).Where(s => s != "").Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else if (kind is JsonValueKind.Object or JsonValueKind.Array) {
			var t = new TextBox { Text = cur?.ToJsonString(Pretty) ?? "", AcceptsReturn = true, MinHeight = 80, FontFamily = new System.Windows.Media.FontFamily("Cascadia Mono, Consolas"), FontSize = (double)Application.Current.FindResource("SizeLog") };
			a_apply.Add(() => {
				try {
					a_obj[a_key] = JsonNode.Parse(t.Text);
					return null;
				} catch (JsonException e) {
					return $"{a_label}: not valid ({e.Message})";
				}
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		} else {
			var t = new TextBox { Text = cur?.ToString() ?? "" };
			a_apply.Add(() => {
				if (a_key is "output_mod" or "then_move_to_mod" or "executable" && t.Text.Trim() == "") {
					return $"{a_label}: cannot be empty";
				}
				if (t.Text == "" && cur == null) {
					return null;
				}
				a_obj[a_key] = t.Text;
				return null;
			});
			t.TextChanged += (_, _) => Changed();
			edit = t;
		}
		System.Windows.Automation.AutomationProperties.SetName(edit, a_label);
		Grid.SetColumn(edit, 1);
		row.Children.Add(edit);
		a_host.Children.Add(row);
	}

	static void Choice(Panel a_host, List<Func<string?>> a_apply, string a_label, JsonObject a_obj, string a_key, (string Value, string Text)[] a_options)
	{
		var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
		row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		row.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Accent"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) });
		var box = new ComboBox { ItemsSource = a_options.Select(o => o.Text).ToList() };
		var cur = Setup.Str(a_obj, a_key);
		box.SelectedIndex = Math.Max(0, Array.FindIndex(a_options, o => string.Equals(o.Value, cur, StringComparison.OrdinalIgnoreCase)));
		// (the choice in whole words on hover: a box made narrow cuts a long one off)
		box.ToolTip = box.SelectedItem as string;
		box.SelectionChanged += (_, _) => {
			box.ToolTip = box.SelectedItem as string;
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
