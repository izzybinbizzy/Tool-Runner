using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace ToolRunner.App;

// A new setup: Mod Organizer's program, its profile, a name - the steps are found from MO2's Executables list.
public static class NewSetupDialog
{
	// a_imported: what an import said (what it took out of the file); null when the setup was made here
	public static string? Run(Window a_owner, out IReadOnlyList<string>? a_imported)
	{
		string? file = null;
		IReadOnlyList<string>? said = null;
		Build(a_owner, (a_file, a_said) => (file, said) = (a_file, a_said)).ShowDialog();
		a_imported = said;
		return file;
	}

	// the box itself (the off-screen pictures draw it without waiting on it). a_made: called when it closes with a setup made
	// or imported - the setup's file, and what an import said
	public static Window Build(Window a_owner, Action<string, IReadOnlyList<string>?> a_made)
	{
		string? made = null;
		List<string>? imported = null;
		// its sizes held to the screen (a least size above the screen's would win over the size it is given)
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = "New setup", Width = Math.Min(640, room.Width), SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize, MinWidth = UiState.Least(480, room.Width),
			MinHeight = UiState.Least(320, room.Height), ShowInTaskbar = false,
			FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		var stack = new StackPanel { Margin = new Thickness(22) };
		stack.Children.Add(new TextBlock { Text = "NEW SETUP", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = (double)a_owner.FindResource("SizeHeading1") });
		stack.Children.Add(new TextBlock { Text = "One setup = one Mod Organizer profile. The steps are found from MO2's Executables list; you can switch any off afterwards.",
			Style = (Style)a_owner.FindResource("MutedText"), Margin = new Thickness(0, 0, 0, 14) });

		var exe = new TextBox();
		var browse = new Button { Content = "Browse ...", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0) };
		// a portable install keeps its instance beside the program; a normal install keeps one or more in %LOCALAPPDATA%\ModOrganizer
		var instance = new ComboBox { DisplayMemberPath = "Key" };
		var profile = new ComboBox();
		var name = new TextBox();
		var pattern = new TextBox { Text = "{output} - {profile}" };
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };

		// the program's path as typed or pasted (the quotes of Explorer's "Copy as path" taken off)
		string Typed() => SettingsPanel.TypedPath(exe.Text);

		void Row(string a_label, FrameworkElement a_edit, FrameworkElement? a_extra = null)
		{
			stack.Children.Add(new TextBlock { Text = a_label, Foreground = Brushes2.Get("Accent"), Margin = new Thickness(0, 8, 0, 4) });
			var d = new DockPanel();
			if (a_extra != null) {
				DockPanel.SetDock(a_extra, Dock.Right);
				d.Children.Add(a_extra);
			}
			d.Children.Add(a_edit);
			System.Windows.Automation.AutomationProperties.SetName(a_edit, a_label);
			stack.Children.Add(d);
		}
		Row(WindowWords.Word("Mod Organizer's program (ModOrganizer.exe)"), exe, browse);
		Row(WindowWords.Word("Its instance"), instance);
		Row(WindowWords.Word("Profile"), profile);
		Row(WindowWords.Word("Setup name"), name);
		Row(WindowWords.Word("Output mod names ({output} and {profile} are filled in)"), pattern);
		stack.Children.Add(note);

		// a step exists only for a tool Mod Organizer's list holds, so this comes before the setup is made
		var findTools = new Button { Content = "Find my tools ...", Style = (Style)a_owner.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 10, 0, 0),
			ToolTip = "Says which tools Mod Organizer's list holds, which are on this PC but not listed, and where to get the rest" };
		var toolRows = new StackPanel();
		stack.Children.Add(findTools);
		var toolsFound = new ScrollViewer { Content = toolRows, MaxHeight = 240, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 6, 0, 0) };
		System.Windows.Automation.AutomationProperties.SetName(toolsFound, WindowWords.Word("The tools found"));
		stack.Children.Add(toolsFound);

		// what was found belongs to the Mod Organizer it was found in: its Add button must not write to one the player has left
		void ForgetTools()
		{
			if (toolRows.Children.Count > 0) {
				toolRows.Children.Clear();
				note.Text = "";
			}
		}
		exe.TextChanged += (_, _) => ForgetTools();
		instance.SelectionChanged += (_, _) => ForgetTools();

		// the instances this program can open: beside it (portable), and the global ones (filled again only for another program,
		// so a picked instance and profile survive a click in and out of the box)
		string? loaded = null;
		void FillInstances()
		{
			loaded = Typed();
			var list = new List<KeyValuePair<string, string?>>();
			var dir = File.Exists(loaded) ? Path.GetDirectoryName(Path.GetFullPath(loaded)) : null;
			if (dir != null && File.Exists(Path.Combine(dir, "ModOrganizer.ini"))) {
				list.Add(new(WindowWords.Word("Portable (beside Mod Organizer)"), null));
			}
			list.AddRange(Mo2Instance.GlobalInstances().Select(d => new KeyValuePair<string, string?>(Path.GetFileName(d), d)));
			instance.ItemsSource = list;
			instance.SelectedIndex = list.Count > 0 ? 0 : -1;
		}

		string? InstanceDir() => instance.SelectedItem is KeyValuePair<string, string?> kv ? kv.Value : null;

		void Fill()
		{
			profile.ItemsSource = null;
			note.Text = "";
			try {
				var mo2 = Mo2Instance.Load(Typed(), InstanceDir());
				var list = mo2.Profiles().ToList();
				profile.ItemsSource = list;
				profile.SelectedItem = list.Contains(mo2.SelectedProfile) ? mo2.SelectedProfile : list.FirstOrDefault();
				note.Text = WindowWords.F($"Found {list.Count} profile(s), {mo2.Executables.Count} executable(s); game: {mo2.GameDir}");
				note.Foreground = Brushes2.Get("Muted");
			} catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) {
				note.Text = e.Message;
				note.Foreground = Brushes2.Get("Bad");
			}
		}
		exe.LostFocus += (_, _) => {
			if (Typed() == loaded) {
				return;
			}
			FillInstances();
			Fill();
		};
		instance.SelectionChanged += (_, _) => Fill();
		browse.Click += (_, _) => {
			var d = new OpenFileDialog { Filter = "Mod Organizer|ModOrganizer.exe", Title = WindowWords.Word("Mod Organizer's program") };
			if (d.ShowDialog(w) == true) {
				exe.Text = d.FileName;
				FillInstances();
				Fill();
			}
		};
		profile.SelectionChanged += (_, _) => {
			if (profile.SelectedItem is string p && (name.Text == "" || profile.ItemsSource is IEnumerable<string> all && all.Contains(name.Text))) {
				name.Text = p;
			}
		};
		// first guess: a Mod Organizer that runs now, else its usual install folder
		var running = Procs.ByImage("ModOrganizer.exe").Select(p => { using (p) { return Procs.ExePath(p); } }).OfType<string>();
		// (never for the off-screen pictures: they show the box as it opens where no Mod Organizer was found, on any PC)
		foreach (var guess in App.Headless ? Enumerable.Empty<string>() : running.Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer 2", "ModOrganizer.exe"))
			         .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mod Organizer", "ModOrganizer.exe"))) {
			if (File.Exists(guess)) {
				exe.Text = guess;
				FillInstances();
				Fill();
				break;
			}
		}

		findTools.Click += async (_, _) => {
			findTools.IsEnabled = false;
			toolRows.Children.Clear();
			note.Text = WindowWords.Word("Looking through your mods and the folders beside Mod Organizer ...");
			note.Foreground = Brushes2.Get("Muted");
			try {
				var (exeText, inst) = (Typed(), InstanceDir());
				var mo2 = Mo2Instance.Load(exeText, inst);
				var rows = await Task.Run(() => ToolFinder.Find(mo2));
				if (Typed() != exeText || InstanceDir() != inst) {
					note.Text = "";
					return;
				}
				foreach (var r in rows) {
					var line = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
					if (r.State == ToolFinder.Found.Missing) {
						var page = new Button { Content = "Its page", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = r.Tool.Page };
						page.Click += (_, _) => {
							try {
								System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(r.Tool.Page) { UseShellExecute = true })?.Dispose();
							} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
								note.Text = $"{r.Tool.Name}'s page did not open ({e.Message}): {r.Tool.Page}";
								note.Foreground = Brushes2.Get("Bad");
							}
						};
						DockPanel.SetDock(page, Dock.Right);
						line.Children.Add(page);
					}
					line.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes2.Get(r.State == ToolFinder.Found.Missing ? "Muted" : "Text"), Text = r.State switch {
						ToolFinder.Found.Listed => WindowWords.F($"{r.Tool.Name}: in Mod Organizer's list"),
						ToolFinder.Found.NotListed => WindowWords.F($"{r.Tool.Name}: found, not in the list yet - {r.Binary}"),
						_ => WindowWords.F($"{r.Tool.Name}: not found - {WindowWords.Word(r.Tool.Where)}"),
					} });
					toolRows.Children.Add(line);
				}
				// (built after the window loaded: into its language like the rest of it)
				WindowWords.Tree(toolRows);
				var missing = rows.Count(r => r.State == ToolFinder.Found.Missing);
				note.Text = missing == 0 ? WindowWords.Word("Every tool was found.") : WindowWords.F($"{missing} tool(s) not found. A run only needs the ones you use: a step is made for each tool the list holds.");
				var found = rows.Count(r => r.State == ToolFinder.Found.NotListed);
				if (found > 0) {
					var add = new Button { Content = WindowWords.F($"Add the {found} found tool(s) to Mod Organizer's list"), Style = (Style)a_owner.FindResource("Ghost"), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 6, 0, 0) };
					add.Click += (_, _) => {
						try {
							var added = ToolFinder.AddFound(Mo2Instance.Load(exeText, inst), rows);
							add.IsEnabled = false;
							Fill();
							note.Text = WindowWords.F($"Added to Mod Organizer's list: {string.Join(", ", added)}.");
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or Mo2BusyException) {
							note.Text = WindowWords.Format("Not added: {0}", e.Message);
							note.Foreground = Brushes2.Get("Bad");
						}
					};
					toolRows.Children.Add(add);
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
				note.Text = e.Message;
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				findTools.IsEnabled = true;
			}
		};

		var create = new Button { Content = "_Make the setup", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var cancel = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		// a setup another player shared: brought in under its own name, with everything taken out that would start something here
		var import = new Button { Content = "Import a setup file ...", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(10, 0, 0, 0),
			ToolTip = "Imports a setup file another player shared with you, for the Mod Organizer and profile picked above. Everything in it that would start a program on this PC is taken out: what a step still needs is set in Settings afterwards." };
		// while the setup is being made the box stays open: closed then, the setup would be written all the same and never shown
		var making = false;
		w.Closing += (_, e) => e.Cancel = making;
		create.Click += async (_, _) => {
			if (profile.SelectedItem is not string p || name.Text.Trim() == "") {
				note.Text = WindowWords.Word("Pick Mod Organizer, a profile and a name.");
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			// (the name is a folder's name too - a run's records and backups: one no run would take is said here, not at the first run)
			if (!Setup.PlainName(Safe(name.Text.Trim()))) {
				note.Text = WindowWords.F($"\"{name.Text.Trim()}\" cannot be a setup's name: Windows takes no folder of that name. Give it another one.");
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			var file = Path.Combine(AppPaths.Setups, Safe(name.Text.Trim()) + ".json");
			// (the program keeps files of its own beside the setups: a setup under one of their names was never listed, and
			// was written over when a step next finished)
			if (Setup.IsStore(file)) {
				note.Text = WindowWords.Format("\"{0}\" is the name of one of the program's own files. Give the setup another name.", name.Text.Trim());
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			// (a run started elsewhere while this box stood open reads the setup of that name)
			if (File.Exists(file) && Engine.Active() is { } going && string.Equals(going.SetupFile, file, StringComparison.OrdinalIgnoreCase)) {
				note.Text = WindowWords.Word("A run is using the setup of that name right now. Give this one another name, or wait for the run to end.");
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			if (File.Exists(file) && !Ask.Show(w, "Replace that setup?", WindowWords.F($"A setup called {name.Text.Trim()} exists. Replace it?"), "Replace", "Keep it")) {
				return;
			}
			// (each tool's output mod is named by this pattern: without {output} every tool was given one and the same mod, and
			// each run's later tools moved the earlier ones' new outputs into the backup)
			if (pattern.Text.Trim() != "" && !pattern.Text.Contains("{output}", StringComparison.OrdinalIgnoreCase)) {
				note.Text = WindowWords.Word("The output mod names need {output} in them: without it every tool would write into the same mod.");
				note.Foreground = Brushes2.Get("Bad");
				return;
			}
			// it looks through every mod (thousands on a big list): off the window's thread, the window says so
			var (exeText, inst, setupName, pat) = (Typed(), InstanceDir(), Safe(name.Text.Trim()), pattern.Text.Trim() == "" ? "{output} - {profile}" : pattern.Text.Trim());
			create.IsEnabled = false;
			cancel.IsEnabled = false;
			import.IsEnabled = false;
			making = true;
			note.Text = WindowWords.Word("Looking through your mods for the tools ...");
			note.Foreground = Brushes2.Get("Muted");
			try {
				var s = await Task.Run(() => SetupMaker.Make(exeText, inst, p, setupName, AppPaths.Data, pat));
				// the setup it replaces goes to the setups' earlier copies first, as with every save
				var text = s.Text();
				var replacing = File.Exists(file);
				if (await Task.Run(() => {
					// a setup that is replaced is written with the run lock held: a run begun since the check above, here or in
					// another copy of the program, never has its setup changed under it
					using var hold = replacing ? Runner.HoldIfFree() : null;
					return replacing && hold == null
						? "a run has started - the setup was not replaced; do it again once the run has ended"
						: Setup.WriteKeepingACopy(file, text, Path.Combine(AppPaths.Data, "Setup backups"));
				}) is { } notWritten) {
					throw new IOException(notWritten);
				}
				made = file;
			} catch (Exception e) {
				note.Text = WindowWords.Format("Not made: {0}", e.Message);
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				making = false;
				create.IsEnabled = true;
				cancel.IsEnabled = true;
				import.IsEnabled = true;
			}
			if (made != null) {
				w.Close();
			}
		};
		import.Click += async (_, _) => {
			var d = new OpenFileDialog { Filter = WindowWords.Word("Setup files") + "|*.json", Title = WindowWords.Word("A setup file to import") };
			if (d.ShowDialog(w) != true) {
				return;
			}
			var from = d.FileName;
			// the Mod Organizer and profile picked in this box, when they are: the setup is made for them
			var (exeText, inst, forProfile) = (Typed(), InstanceDir(), profile.SelectedItem as string);
			create.IsEnabled = false;
			cancel.IsEnabled = false;
			import.IsEnabled = false;
			making = true;
			try {
				// (its own name; one that is taken gets a number after it - nothing of the player's is ever written over)
				var got = await Task.Run(() => {
					SetupShare.ForProfile? mine = null;
					if (forProfile != null) {
						try {
							var mo2 = Mo2Instance.Load(exeText, inst);
							mine = new(Path.GetFullPath(exeText), inst ?? Path.GetDirectoryName(mo2.IniPath)!, forProfile);
						} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
							// (no Mod Organizer that reads is picked: the setup is brought in without one, and Settings takes it)
						}
					}
					return SetupShare.BringIn(from, null, AppPaths.Setups, true, AppPaths.Data, mine);
				});
				if (got.File == null) {
					note.Text = WindowWords.Format("Not imported: {0}", string.Join(" ", got.Lines));
					note.Foreground = Brushes2.Get("Bad");
				} else {
					made = got.File;
					imported = got.Lines;
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) {
				note.Text = WindowWords.Format("Not imported: {0}", e.Message);
				note.Foreground = Brushes2.Get("Bad");
			} finally {
				making = false;
				create.IsEnabled = true;
				cancel.IsEnabled = true;
				import.IsEnabled = true;
			}
			if (made != null) {
				w.Close();
			}
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
		bar.Children.Add(create);
		bar.Children.Add(cancel);
		bar.Children.Add(import);
		stack.Children.Add(bar);
		// the box can be sized; its content scrolls when it is made smaller than it needs (named for a screen reader, which
		// meets the scrolling region as a pane of its own)
		var fields = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
		System.Windows.Automation.AutomationProperties.SetName(fields, "New setup fields");
		w.Content = fields;
		w.Closed += (_, _) => {
			if (made != null) {
				a_made(made, imported);
			}
		};
		return w;
	}

	static string Safe(string a_name) => string.Concat(a_name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c));
}
