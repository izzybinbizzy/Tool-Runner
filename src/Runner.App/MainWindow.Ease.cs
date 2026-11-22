using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ToolRunner.App;

// His checklist 635 ("all"): the ease-of-use ideas of the page "Tool Runner quality of life" - a step's settings from a
// double-click, its output mod's folder and its tool's page from a right-click, a setup copied or renamed from the setup list,
// the command box (Ctrl+K) and the compact view (Ctrl+J)
public partial class MainWindow
{
	// ---------------------------------------------------------------- a step's settings, its output, its tool's page
	void StepList_DoubleClick(object a_s, MouseButtonEventArgs a_e)
	{
		var from = a_e.OriginalSource as DependencyObject;
		if (Up<System.Windows.Controls.Primitives.ButtonBase>(from) != null || Up<ListBoxItem>(from)?.DataContext is not StepRow row) {
			return;
		}
		a_e.Handled = true;
		OpenStepSettings(row);
	}

	void StepSettings_Click(object a_s, RoutedEventArgs a_e)
	{
		if (RowOf(a_s) is { } row) {
			OpenStepSettings(row);
		}
	}

	// Settings opened (or brought forward) on its Steps page with this step's settings
	public void OpenStepSettings(StepRow a_row)
	{
		// Settings standing open on another setup with changes not saved stays as it is (it does not follow the setup picked):
		// that setup's step is not the one double-clicked
		if (_panels.ContainsKey("settings") && SettingsPanel.HasChanges && !SettingsPanel.IsFor(_vm.Setup)) {
			ShowPanel("settings");
			_vm.Say($"Settings shows another setup's changes, not saved yet - save them or undo them, then open {a_row.Name}'s settings again.", "warn");
			return;
		}
		ShowPanel("settings");
		if (!SettingsPanel.ShowStep(a_row.Key)) {
			_vm.Say($"{a_row.Name}'s settings could not be shown: the step open in Settings has a value to fix first.", "warn");
		}
	}

	async void OpenOutput_Click(object a_s, RoutedEventArgs a_e)
	{
		if (RowOf(a_s) is not { } row || _vm.Setup is not { } setup) {
			return;
		}
		var mod = setup.Steps.FirstOrDefault(s => s.Key == row.Key) is { } entry ? new StepSettings(entry.Settings).OutputMod : null;
		if (string.IsNullOrWhiteSpace(mod)) {
			_vm.Say($"{row.Name} makes no output mod of its own.", "warn");
			return;
		}
		try {
			var dir = await Task.Run(() => Mo2Instance.Load(setup.Mo2Exe, setup.Mo2Instance).ModDir(mod)).ConfigureAwait(true);
			if (!Directory.Exists(dir)) {
				_vm.Say($"{row.Name}'s output \"{mod}\" is not there yet - its first run makes it.", "warn");
				return;
			}
			MainViewModel.OpenFolder(dir);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) {
			_vm.Say("Mod Organizer could not be read: " + e.Message, "bad");
		}
	}

	void OpenToolPage_Click(object a_s, RoutedEventArgs a_e)
	{
		if (RowOf(a_s) is not { } row) {
			return;
		}
		if (SettingsPanel.HelpPage(row.Key) is not { } help) {
			_vm.Say($"No page is known for {row.Name}'s tool.", "warn");
			return;
		}
		try {
			System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(help.Url) { UseShellExecute = true });
		} catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) {
			_vm.Say($"{help.What} did not open: {e.Message}", "bad");
		}
	}

	// ---------------------------------------------------------------- a setup copied or renamed
	bool _copying;

	// the copy is made on a worker (a slow disk never holds the window), one at a time
	async void DuplicateSetup_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_copying || _vm.IsRunning || _vm.SelectedSetup is not { } item) {
			return;
		}
		var name = Ask.Input(this, "Copy this setup", $"A new setup with the steps and settings of \"{item.Name}\", and the settings its tools learned. Nothing of \"{item.Name}\" changes. The copy's name:",
			SetupFiles.FreeCopyName(item.File), "Make the copy", n => SetupFiles.BadName(AppPaths.Setups, n));
		if (name == null) {
			return;
		}
		_copying = true;
		try {
			var from = item.File;
			var made = await Task.Run(() => SetupFiles.Duplicate(from, name)).ConfigureAwait(true);
			_vm.LoadSetups(made);
			_vm.Say($"\"{name.Trim()}\" made: a copy of \"{item.Name}\". It has no runs of its own yet.", "good");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) {
			_vm.Say("The copy could not be made: " + e.Message, "bad");
		} finally {
			_copying = false;
		}
	}

	bool _renaming;

	async void RenameSetup_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_renaming || _vm.SelectedSetup is not { } item) {
			return;
		}
		// (a queue between two setups too: the next setup it starts is read by its file)
		if (StillGoing() || _vm.IsRunning) {
			_vm.Say("A run or a queue is going - rename the setup when it has ended.", "bad");
			return;
		}
		// Settings stands open on the setup's old file: its Save would write there - it closes first, asked about what is typed
		if (!LeaveSettings()) {
			return;
		}
		if (_panels.TryGetValue("settings", out var open)) {
			open.Close();
		}
		var name = Ask.Input(this, "Rename this setup", $"The new name of \"{item.Name}\". Its runs and their history, and the settings its tools learned, go with it.",
			item.Name, "Rename", n => SetupFiles.BadName(AppPaths.Setups, n, item.File));
		if (name == null || name.Trim() == item.Name) {
			return;
		}
		_renaming = true;
		// from here no change or save of the setup starts (Renaming), until the renamed one is shown
		_vm.Renaming = true;
		try {
			// a change to the setup that is still on its way into the old file lands there first - every one, also one that
			// began while this waited
			while (!MainViewModel.PendingSetupSave.IsCompleted || _vm.Saving) {
				await MainViewModel.PendingSetupSave.ConfigureAwait(true);
				await Task.Yield();
			}
			var done = await Task.Run(() => SetupFiles.Rename(item.File, name, AppPaths.Runs)).ConfigureAwait(true);
			var was = MainViewModel.ToolsOfferedKey(item.File);
			if (UiState.Flag(was)) {
				UiState.Set(MainViewModel.ToolsOfferedKey(done.File), true);
				UiState.Set(was, false);
			}
			_vm.LoadSetups(done.File);
			_vm.Say(done.NotRepointed.Count == 0 ? $"Renamed to \"{name.Trim()}\"." :
				$"Renamed to \"{name.Trim()}\". {done.NotRepointed.Count} run record(s) could not be written and show under no setup in History.", done.NotRepointed.Count == 0 ? "good" : "warn");
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or InvalidDataException or System.Text.Json.JsonException) {
			_vm.LoadSetups(File.Exists(item.File) ? item.File : null);
			_vm.Say("The setup was not renamed: " + e.Message, "bad");
		} finally {
			_vm.Renaming = false;
			_renaming = false;
		}
	}

	// ---------------------------------------------------------------- the command box (Ctrl+K)
	public List<CommandBox.Item> CommandList()
	{
		var items = new List<CommandBox.Item> {
			new("Start", () => _vm.StartCommand.Execute(null)),
			new("Check", () => _vm.CheckCommand.Execute(null)),
			new("Dry run", () => _vm.DryRunCommand.Execute(null)),
		};
		if (_vm.IsRunning) {
			items.Add(new("Stop now", () => _vm.StopNowCommand.Execute(null)));
			items.Add(new("Stop after this step", () => _vm.StopAfterCommand.Execute(null)));
		}
		items.AddRange([
			new("History", () => ShowPanel("history")),
			new("MO2 Changelog", () => ShowPanel("changes")),
			new("Settings", () => ShowPanel("settings")),
			new("Themes", () => ShowPanel("themes")),
			new("Tools - set up each tool", () => Tools_Click(this, new RoutedEventArgs())),
			new("Queue of setups", () => Queue_Click(this, new RoutedEventArgs())),
			new("New setup", () => NewSetup_Click(this, new RoutedEventArgs())),
			new("Copy this setup", () => DuplicateSetup_Click(this, new RoutedEventArgs())),
			new("Rename this setup", () => RenameSetup_Click(this, new RoutedEventArgs())),
			new(_logWindow == null ? "Log in its own window" : "Log back in the main window", () => LogPop_Click(this, new RoutedEventArgs())),
			new("Compact view", () => ShowCompact()),
		]);
		foreach (var s in _vm.Setups.Where(s => !ReferenceEquals(s, _vm.SelectedSetup))) {
			items.Add(new($"Show setup: {s.Name}", () => {
				if (!_vm.IsRunning) {
					_vm.SelectedSetup = s;
				}
			}));
		}
		foreach (var r in _vm.Steps) {
			items.Add(new($"Settings of {r.Name}", () => OpenStepSettings(r)));
		}
		foreach (var r in _vm.Steps) {
			items.Add(new($"Run only {r.Name}", () => StartFor(r, "only")));
		}
		return items;
	}

	void FindSetting(string a_q)
	{
		ShowPanel("settings");
		SettingsPanel.Find(a_q);
	}

	public void ShowCommands() => CommandBox.Run(this, CommandList(), FindSetting);

	// the off-screen pictures: the box with a few letters typed
	public Window DemoCommandBox(string a_typed)
	{
		var w = CommandBox.Make(this, CommandList(), _ => { }, out var type);
		type(a_typed);
		return w;
	}

	// ---------------------------------------------------------------- the compact view (Ctrl+J)
	// A small window of its own for a second screen: the step, the clock and the time left, the progress bar, Stop. It is no
	// panel of the main window (an owned window goes away when its owner is minimized - this one is for exactly that), and it
	// closes with the main window
	Window? _compact;

	void Compact_Click(object a_s, RoutedEventArgs a_e) => ShowCompact();

	public Window ShowCompact()
	{
		if (_compact is { } open) {
			Front(open);
			return open;
		}
		var w = new Window {
			Title = "Tool Runner - compact", Width = UiState.Fit(UiState.Number("compact.width"), 380, 300, SystemParameters.WorkArea.Width),
			Height = UiState.Fit(UiState.Number("compact.height"), 190, 160, SystemParameters.WorkArea.Height), MinWidth = 300, MinHeight = 160,
			WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.CanResize, DataContext = _vm, Icon = Icon,
			FontFamily = (System.Windows.Media.FontFamily)FindResource("UiFont"), FontSize = (double)FindResource("SizeBody"),
		};
		w.SetResourceReference(BackgroundProperty, "Panel");
		w.SetResourceReference(ForegroundProperty, "Text");
		var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
		title.SetBinding(TextBlock.TextProperty, nameof(MainViewModel.HeadTitle));
		title.SetResourceReference(TextBlock.ForegroundProperty, "AccentLight");
		// (it wraps: in a small window the step count went off its right edge)
		var clock = new TextBlock { Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
		clock.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		clock.SetBinding(TextBlock.TextProperty, new System.Windows.Data.MultiBinding {
			Converter = new JoinNonEmpty(), Bindings = {
				new System.Windows.Data.Binding(nameof(MainViewModel.Clock)), new System.Windows.Data.Binding(nameof(MainViewModel.Left)),
				new System.Windows.Data.Binding(nameof(MainViewModel.FinishAt)), new System.Windows.Data.Binding(nameof(MainViewModel.StepOf)) } });
		var bar = new ProgressBar { Height = 8, Maximum = 1, Margin = new Thickness(0, 10, 0, 10) };
		bar.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty, nameof(MainViewModel.Progress));
		bar.SetBinding(ProgressBar.IsIndeterminateProperty, nameof(MainViewModel.ProgressBusy));
		bar.SetResourceReference(ForegroundProperty, "AccentBar");
		System.Windows.Automation.AutomationProperties.SetName(bar, "Run progress");
		var stop = new Button { Content = "Stop _now", Style = (Style)FindResource("Danger"), HorizontalAlignment = HorizontalAlignment.Left, Command = _vm.StopNowCommand,
			ToolTip = "Closes the tool at once and puts your profile back" };
		stop.SetBinding(IsEnabledProperty, nameof(MainViewModel.IsRunning));
		var main = new Button { Content = "Main window", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0), ToolTip = "Brings the main window forward" };
		main.Click += (_, _) => Front(this);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal };
		buttons.Children.Add(stop);
		buttons.Children.Add(main);
		var body = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };
		foreach (var c in new UIElement[] { title, clock, bar, buttons }) {
			body.Children.Add(c);
		}
		w.Content = body;
		w.PreviewKeyDown += OnKeys;
		void MainGone(object? a_sender, EventArgs a_args) => w.Close();
		Closed += MainGone;
		w.IsVisibleChanged += (_, _) => SeenOrNot();
		w.Closed += (_, _) => {
			Closed -= MainGone;
			_compact = null;
			SeenOrNot();
			if (!App.Headless) {
				UiState.Set("compact.width", w.ActualWidth);
				UiState.Set("compact.height", w.ActualHeight);
			}
		};
		_compact = w;
		if (!App.Headless) {
			w.Show();
		}
		return w;
	}
}

// the compact view's second line: its parts that hold something, joined
public sealed class JoinNonEmpty : System.Windows.Data.IMultiValueConverter
{
	public object Convert(object?[] a_values, Type a_type, object? a_parameter, System.Globalization.CultureInfo a_culture) =>
		string.Join("  ·  ", a_values.OfType<string>().Where(v => v != ""));

	public object[] ConvertBack(object? a_value, Type[] a_types, object? a_parameter, System.Globalization.CultureInfo a_culture) => throw new NotSupportedException();
}
