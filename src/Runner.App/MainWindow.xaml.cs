using System.Media;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace ToolRunner.App;

// a WPF window ends with OnClosed, never Dispose: the notification-area icon it owns is released there (Tray.Dispose)
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "released in OnClosed, the window's own end")]
public partial class MainWindow : Window
{
	readonly MainViewModel _vm = new();
	// How often the window looks: every second while something is going (a run's clock and progress, a start being checked),
	// seldom while nothing is, hardly at all while it is minimised. A change in the runs folder (RunsWatch) and the window
	// coming back wake it at once, so a slow look loses nothing
	static readonly TimeSpan Live = TimeSpan.FromSeconds(1), Calm = TimeSpan.FromSeconds(3), Away = TimeSpan.FromSeconds(20);
	readonly DispatcherTimer _timer = new() { Interval = Live };
	RunsWatch? _watch;
	int _woken;
	bool _demo;
	bool _wasMaximized;
	Tray? _tray;
	// History (the MO2 Changelog is its second view), Settings and Themes: each in a window of its own while it is open
	readonly Dictionary<string, PanelWindow> _panels = [];
	// which of the history window's two views is shown
	string _historyView = "history";
	// the log in a window of its own, with its text box and its Follow tick there
	PanelWindow? _logWindow;
	LogView? _logOut;
	CheckBox? _followOut;
	// whether the log was shown in the main window before it went into its own
	bool _logWasHere = true;
	// the main window is closing: its panels go with it and ask nothing more
	bool _closingAll;

	// his version scheme is two parts (1.0, 1.1)
	public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(2) ?? "";

	public MainWindow()
	{
		InitializeComponent();
		RestoreSize();
		FitTheScreen();
		DataContext = _vm;
		_vm.Alert += Notify;
		// a start that would be refused is only told (one button); one that would go ahead is asked
		_vm.ConfirmRun = (a_title, a_text, a_yes) => {
			if (a_yes != null) {
				return Ask.Show(this, a_title, a_text, a_yes, "Not now");
			}
			Ask.Tell(this, a_title, a_text, "Close");
			return false;
		};
		_vm.OfferTools = (a_setup, a_tools) => ToolsDialog.Run(this, _vm, a_setup, a_tools, true);
		// the header drops the word "Setup" where the window is narrow
		SizeChanged += (_, e) => _vm.Roomy = e.NewSize.Width >= 1100;
		_vm.Freed += () => {
			if (_closeWhenFree) {
				_closeWhenFree = false;
				Close();
			}
		};
		_vm.LogChanged += OnLog;
		_timer.Tick += async (_, _) => await Look();
		_timer.Start();
		StateChanged += (_, _) => Wake();
		StateChanged += (_, _) => SeenOrNot();
		// how it stood before it was minimized: closed from the taskbar while minimized, it comes back maximized if it was
		StateChanged += (_, _) => {
			if (WindowState != WindowState.Minimized) {
				_wasMaximized = WindowState == WindowState.Maximized;
			}
		};
		// Windows' own "show animations" switch, changed while the window is up: the moving bar follows at once
		void MotionChanged(object? a_sender, System.ComponentModel.PropertyChangedEventArgs a_e)
		{
			if (a_e.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) {
				_vm.MotionChanged();
			}
		}
		SystemParameters.StaticPropertyChanged += MotionChanged;
		Closed += (_, _) => SystemParameters.StaticPropertyChanged -= MotionChanged;
		IsVisibleChanged += (_, _) => SeenOrNot();
		_vm.WantsLook += Wake;
		_vm.SetupShown += SettingsFollow;
		// the off-screen pictures show a made-up run: nothing is watched for them
		if (!App.Headless) {
			_watch = new RunsWatch(AppPaths.Runs, Wake);
		}
		Footer.Text = $"Ctrl+K commands  ·  Ctrl+Enter start  ·  F5 check  ·  Ctrl+D dry run  ·  Ctrl+H history  ·  Ctrl+L log  ·  Ctrl+J compact  ·  Ctrl+T themes  ·  Ctrl+, settings   ·   version {AppVersion}";
		Footer.ToolTip = "Data folder: " + AppPaths.Data;
		PreviewKeyDown += OnKeys;
		// a mod ticked in Mod Organizer while the window was in the background: each step's last run is worked out again
		Activated += async (_, _) => {
			Wake();
			await _vm.LoadLastRunsIfStale();
		};
		if (_vm.Setups.Count == 0) {
			_vm.Say("No setup yet: press New setup and pick your Mod Organizer and profile.", "warn");
		}
		// what changes on its own is said to a screen reader (his checklist 490) - once the window is up, so the first look at the
		// last run is not read out as news
		Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _vm.PropertyChanged += Announce);
		// his checklist 471: once the window is up, at most once a day, whether a newer version of the program is out
		// (never for the off-screen pictures: no web question, no file written into that data folder, no banner in a picture)
		Loaded += async (_, _) => {
			if (!App.Headless) {
				await _vm.LookForNewVersion(AppVersion);
			}
		};
		Theme.Applied += _vm.Repaint;
		// the log as the player left it: shown here, or hidden with its room given to the card above
		if (UiState.Flag("log.hidden")) {
			ShowLogHere(false);
		}
	}

	public void Say(string a_text) => _vm.Say(a_text, "warn");

	// nothing moves in a window nobody sees (minimized, or put away while a run goes) - unless the compact view shows the run:
	// it is for exactly the time the main window is minimized
	void SeenOrNot() => _vm.WindowAway = (WindowState == WindowState.Minimized || !IsVisible) && _compact is not { IsVisible: true };

	// work the window carries itself (Settings' own buttons): it is not closed in the middle of it
	public IDisposable Working(string a_what) => _vm.Working(a_what);

	// The runs folder watched, or let go. It is let go while the program's data is removed: Windows moves no folder to the
	// Recycle Bin while a folder inside it is watched (the timer alone looks meanwhile)
	public void WatchRuns(bool a_on)
	{
		_watch?.Dispose();
		_watch = a_on && !App.Headless ? new RunsWatch(AppPaths.Runs, Wake) : null;
	}

	// one look, and the pace of the next ones set by what it found
	async Task Look()
	{
		await _vm.PollAsync();
		// the notification-area icon (there once a run started or a notification was shown) says what runs
		if (_vm.IsRunning || _tray?.Shown == true) {
			_tray?.Show(Brand.Name + " - " + _vm.NowTitle + (_vm.FinishAt == "" ? "" : ", " + _vm.FinishAt));
		}
		var pace = _vm.Busy ? Live : WindowState == WindowState.Minimized ? Away : Calm;
		if (_timer.Interval != pace) {
			_timer.Interval = pace;
		}
	}

	// from any thread: a look soon. Wakes that come before it has run are that one look
	void Wake()
	{
		if (!_demo && Interlocked.Exchange(ref _woken, 1) == 0) {
			Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Woken));
		}
	}

	async void Woken()
	{
		Interlocked.Exchange(ref _woken, 0);
		if (!_demo) {
			await Look();
		}
	}

	public void ShowProfile(string a_mo2Exe, string a_base, string a_profile)
	{
		if (!_vm.SelectFor(a_mo2Exe, a_base, a_profile)) {
			_vm.Say($"No setup for the profile {a_profile} yet: press New setup.", "warn");
		}
	}

	void Announce(object? a_s, System.ComponentModel.PropertyChangedEventArgs a_e)
	{
		switch (a_e.PropertyName) {
		case nameof(MainViewModel.Message):
			A11y.Say(this, _vm.Message, _vm.MessageKind == "bad");
			break;
		case nameof(MainViewModel.NowTitle) when _vm.IsRunning && _vm.Waiting == "":
			A11y.Say(this, "Now: " + _vm.NowTitle);
			break;
		case nameof(MainViewModel.Waiting) when _vm.Waiting != "":
			A11y.Say(this, _vm.WaitingTitle + ": " + _vm.Waiting, true);
			break;
		case nameof(MainViewModel.Verdict) when _vm.Verdict != "":
			A11y.Say(this, _vm.NowTitle + ": " + _vm.Verdict, true);
			break;
		case nameof(MainViewModel.NewVersion) when _vm.NewVersion != "":
			A11y.Say(this, _vm.NewVersion);
			break;
		case nameof(MainViewModel.Interrupted) when _vm.Interrupted != "":
			A11y.Say(this, _vm.Interrupted, true);
			break;
		}
	}

	// a small screen or a big Windows scaling (a 1280 x 720 laptop at 150 % leaves about 850 x 480): the window's limits and size
	// shrink to the screen's usable area, so it always fits and can be moved
	void FitTheScreen()
	{
		var area = SystemParameters.WorkArea;
		if (area.Width <= 0 || area.Height <= 0) {
			return;
		}
		MinWidth = Math.Min(MinWidth, area.Width);
		MinHeight = Math.Min(MinHeight, area.Height);
		Width = Math.Min(Width, area.Width);
		Height = Math.Min(Height, area.Height);
	}

	// the size it had last time (never for the off-screen pictures, which draw at sizes of their own)
	void RestoreSize()
	{
		if (App.Headless) {
			return;
		}
		if (UiState.Number("main.width") is { } w && UiState.Number("main.height") is { } h) {
			Width = Math.Max(MinWidth, w);
			Height = Math.Max(MinHeight, h);
		}
		if (UiState.Flag("main.maximized")) {
			WindowState = WindowState.Maximized;
			_wasMaximized = true;
		}
	}

	void KeepSize()
	{
		if (App.Headless) {
			return;
		}
		UiState.Set("main.maximized", WindowState == WindowState.Maximized || (WindowState == WindowState.Minimized && _wasMaximized));
		// maximized or minimized, the size it comes back to is the one Windows keeps for its normal state
		var normal = WindowState == WindowState.Normal ? new Size(ActualWidth, ActualHeight) : RestoreBounds.Size;
		if (normal.Width > 0 && normal.Height > 0 && !double.IsInfinity(normal.Width) && !double.IsInfinity(normal.Height)) {
			UiState.Set("main.width", normal.Width);
			UiState.Set("main.height", normal.Height);
		}
	}

	// the window's keys - from one of its panel windows too, so Ctrl+H or Ctrl+, work wherever the keyboard is
	void OnKeys(object a_s, KeyEventArgs a_e)
	{
		if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) {
			return;
		}
		switch (a_e.Key) {
		case Key.H:
			ShowPanel("history");
			break;
		case Key.M:
			ShowPanel("changes");
			break;
		case Key.OemComma:
			ShowPanel("settings");
			break;
		case Key.T:
			ShowPanel("themes");
			break;
		case Key.L:
			PopLog();
			break;
		case Key.K:
			ShowCommands();
			break;
		case Key.J:
			ShowCompact();
			break;
		case Key.F when _panels.TryGetValue("settings", out var settings) && ReferenceEquals(a_s, settings):
			SettingsPanel.FocusFind();
			break;
		case Key.N when ReferenceEquals(a_s, this):
			NewSetup_Click(this, new RoutedEventArgs());
			break;
		default:
			return;
		}
		a_e.Handled = true;
	}

	// ---------------------------------------------------------------- alerts (his pick: notification + sound on, flash off by default)
	void Notify(string a_title, string a_text)
	{
		var s = _vm.Setup;
		var toast = s?.AlertNotification ?? true;
		var sound = s?.AlertSound ?? true;
		// Windows keeps 63 characters of a notification's title: the program's name goes only where the title still fits
		var title = (Brand.Name + " - " + a_title).Length <= 63 ? Brand.Name + " - " + a_title : a_title;
		var shown = toast && _tray != null && _tray.Notify(title, a_text.Length > 250 ? a_text[..250] + " ..." : a_text, sound);
		if (!shown && sound) {
			SystemSounds.Asterisk.Play();
		}
		if (s?.AlertFlash == true && !IsActive) {
			Native.Flash(new WindowInteropHelper(this).Handle, true);
		}
	}

	// ---------------------------------------------------------------- steps
	// the context menu and the buttons act on the selected step (a right-click selects the row under it)
	StepRow? RowOf(object a_sender) => a_sender is FrameworkElement { DataContext: StepRow r } ? r : _vm.SelectedStep;

	async void StepOn_Click(object a_s, RoutedEventArgs a_e)
	{
		if (a_e.OriginalSource is CheckBox { DataContext: StepRow row } box) {
			if (_vm.IsRunning) {
				box.IsChecked = row.On = !(box.IsChecked == true);
				return;
			}
			await _vm.SetOn(row, box.IsChecked == true);
		}
	}

	void RunOnly_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "only");
	void RunFrom_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "from");
	void RunUntil_Click(object a_s, RoutedEventArgs a_e) => StartFor(RowOf(a_s), "until");

	void StartFor(StepRow? a_row, string a_how)
	{
		if (a_row == null) {
			return;
		}
		if (!a_row.On) {
			_vm.Say($"{a_row.Name} is switched off in this setup - tick it first.", "bad");
			return;
		}
		if (a_row.LeftOut) {
			_vm.Say($"{a_row.Name} is left out for a new game - untick \"New game\" to run it.", "bad");
			return;
		}
		if (a_row.UsesOld) {
			_vm.Say($"{a_row.Name} uses its old output in this setup, so it does not run - press \"Build it again\" under the steps first.", "bad");
			return;
		}
		_ = _vm.StartWith(a_how == "only" ? a_row.Key : null, a_how == "from" ? a_row.Key : null, a_how == "until" ? a_row.Key : null);
	}

	// His word of 2026-11-21: a step may use the output its tool made before - behind a box that says exactly what that means
	// for this tool, now. Pressed for a step that is set so already, it is built again from the next run on, with no box
	bool _oldAsked;

	async void UseOld_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.SelectedStep is not { } row) {
			_vm.Say("Select a step in the list first.", "bad");
			return;
		}
		if (_vm.IsRunning || _oldAsked) {
			return;
		}
		if (row.UsesOld) {
			await _vm.SetUseOld(row, false);
			return;
		}
		if (!row.On || row.LeftOut) {
			_vm.Say(row.LeftOut ? $"{row.Name} is left out for a new game, so there is nothing of it to use." : $"{row.Name} is switched off in this setup - tick it first.", "bad");
			return;
		}
		_oldAsked = true;
		try {
			var (look, whyNot) = await _vm.OldOutputOf(row);
			// (the look ran away from the window's thread: the setup it was made for may no longer be the one shown)
			if (!_vm.Steps.Contains(row)) {
				_vm.Say("Another setup was picked meanwhile - select the step again.", "warn");
			} else if (look == null) {
				Ask.Tell(this, "No old output to use", whyNot ?? "", "OK");
			} else if (!look.Usable) {
				Ask.Tell(this, $"{row.Name}'s old output cannot be used", string.Join("\n\n", OldOutput.Warning(look, _vm.Setup?.Profile ?? "")), "OK");
			} else if (Ask.Show(this, $"Use {row.Name}'s old output?", _vm.OldOutputText(look), "Use the old output", "Build it again")) {
				await _vm.SetUseOld(row, true);
			}
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) {
			_vm.Say("The old output could not be looked at: " + e.Message, "bad");
		} finally {
			_oldAsked = false;
		}
	}

	// the off-screen pictures: that box for a made-up DynDOLOD output with changes since, and tools built again before it
	public Window DemoOldOutputBox()
	{
		var look = new OldOutput.Look("dyndolod", "DynDOLOD", "DynDOLOD Output", true, true, new DateTime(2026, 10, 27, 21, 52, 0, DateTimeKind.Local), true,
			[("Blubbos Trees", "added"), ("Skyland AIO", "ticked"), ("Old Town Overhaul", "removed")], ["xLODGen", "TexGen"], [], null);
		return Ask.Make(this, "Use DynDOLOD's old output?", _vm.OldOutputText(look), "Use the old output", "Build it again", _ => { });
	}

	// the off-screen pictures: a step of the made-up setup shown as using its old output, selected so the button reads for it
	public void DemoUsesOld(string a_key, bool a_on)
	{
		if (_vm.Steps.FirstOrDefault(s => s.Key == a_key) is { } row) {
			row.UsesOld = a_on;
			_vm.SelectedStep = a_on ? row : null;
			_vm.DemoRefreshOld();
		}
	}

	void Up_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), -1);
	void Down_Click(object a_s, RoutedEventArgs a_e) => Move(RowOf(a_s), +1);

	async void StepList_KeyDown(object a_s, KeyEventArgs a_e)
	{
		if (Keyboard.Modifiers == ModifierKeys.Alt && a_e.SystemKey is Key.Up or Key.Down) {
			// (marked as handled before the save is waited for: the key goes no further meanwhile)
			a_e.Handled = true;
			Move(_vm.SelectedStep, a_e.SystemKey == Key.Up ? -1 : +1);
		} else if (a_e.Key == Key.Space && _vm.SelectedStep is { } row && !_vm.IsRunning) {
			a_e.Handled = true;
			row.On = !row.On;
			await _vm.SetOn(row, row.On);
		}
	}

	void Move(StepRow? a_row, int a_delta)
	{
		if (a_row != null) {
			MoveTo(a_row, _vm.Steps.IndexOf(a_row) + a_delta);
		}
	}

	// his rule: players may reorder, behind a warning window saying things could go wrong out of order - the buttons, the
	// keys and a drag all come through here
	async void MoveTo(StepRow a_row, int a_to)
	{
		if (_vm.IsRunning || a_to < 0 || a_to >= _vm.Steps.Count || a_to == _vm.Steps.IndexOf(a_row)) {
			return;
		}
		var warn = _vm.WarningsIfMovedTo(a_row, a_to);
		if (warn.Count > 0 && !Ask.Show(this, "Out of the recommended order",
			    "Moving this step breaks the order the tools' own instructions give. Things could go wrong if they run out of order:\n\n• " +
			    string.Join("\n• ", warn) + "\n\nMove it anyway?", "Move anyway", "Keep the order")) {
			return;
		}
		await _vm.MoveTo(a_row, a_to);
		StepList.Focus();
	}

	// ---------------------------------------------------------------- a step dragged to another place
	// His list of 2026-11-16: a step is dragged in the list to wherever the player wants it. The handlers sit on the list
	// itself (never in its row template: WPF numbers handlers wrongly there). A press on a row's tick box, or a move shorter
	// than Windows' own drag distance, is no drag; nothing moves while a run goes.
	const string DragFormat = "ToolRunner.Step";
	Point? _pressAt;
	StepRow? _pressed;
	StepRow? _dragging;
	StepRow? _marked;

	static T? Up<T>(DependencyObject? a_from) where T : DependencyObject
	{
		for (var d = a_from; d != null;
		     d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) {
			if (d is T hit) {
				return hit;
			}
		}
		return null;
	}

	void StepList_Press(object a_s, MouseButtonEventArgs a_e)
	{
		var from = a_e.OriginalSource as DependencyObject;
		var onControl = Up<System.Windows.Controls.Primitives.ButtonBase>(from) != null || Up<System.Windows.Controls.Primitives.ScrollBar>(from) != null;
		_pressed = _vm.IsRunning || onControl ? null : Up<ListBoxItem>(from)?.DataContext as StepRow;
		_pressAt = _pressed == null ? null : a_e.GetPosition(StepList);
	}

	void StepList_Release(object a_s, MouseButtonEventArgs a_e) => (_pressed, _pressAt) = (null, null);

	void StepList_Drag(object a_s, MouseEventArgs a_e)
	{
		if (_pressed is not { } row || _pressAt is not { } at || a_e.LeftButton != MouseButtonState.Pressed) {
			return;
		}
		var now = a_e.GetPosition(StepList);
		if (Math.Abs(now.X - at.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - at.Y) < SystemParameters.MinimumVerticalDragDistance) {
			return;
		}
		(_pressed, _pressAt) = (null, null);
		_vm.SelectedStep = row;
		_dragging = row;
		try {
			// the row itself stays here (_dragging): what travels is only a mark that this is a step of this list
			DragDrop.DoDragDrop(StepList, new DataObject(DragFormat, row.Key), DragDropEffects.Move);
		} finally {
			_dragging = null;
			Mark(null, false);
		}
	}

	// the line that shows where the dragged step would land: above or below the row under the pointer
	void Mark(StepRow? a_row, bool a_below)
	{
		if (_marked != null && !ReferenceEquals(_marked, a_row)) {
			_marked.DropAbove = _marked.DropBelow = false;
		}
		_marked = a_row;
		if (a_row != null) {
			a_row.DropAbove = !a_below;
			a_row.DropBelow = a_below;
		}
	}

	// the row under the pointer and whether the pointer is in its lower half; under the last row = after the last
	(StepRow Row, bool Below)? Target(DragEventArgs a_e)
	{
		if (Up<ListBoxItem>(a_e.OriginalSource as DependencyObject) is { DataContext: StepRow over } item) {
			return (over, a_e.GetPosition(item).Y > item.ActualHeight / 2);
		}
		return _vm.Steps.Count > 0 ? (_vm.Steps[^1], true) : null;
	}

	void StepList_DragOver(object a_s, DragEventArgs a_e)
	{
		a_e.Handled = true;
		if (_dragging == null || !a_e.Data.GetDataPresent(DragFormat) || _vm.IsRunning || Target(a_e) is not { } t) {
			a_e.Effects = DragDropEffects.None;
			Mark(null, false);
			return;
		}
		a_e.Effects = DragDropEffects.Move;
		Mark(t.Row, t.Below);
	}

	void StepList_DragLeave(object a_s, DragEventArgs a_e) => Mark(null, false);

	void StepList_Drop(object a_s, DragEventArgs a_e)
	{
		a_e.Handled = true;
		var target = Target(a_e);
		Mark(null, false);
		if (_dragging is not { } row || !a_e.Data.GetDataPresent(DragFormat) || target is not { } t || _vm.IsRunning) {
			return;
		}
		var to = DropPlace(_vm.Steps.IndexOf(row), _vm.Steps.IndexOf(t.Row), t.Below);
		// once the drag itself has ended: the order warning is a window of its own
		Dispatcher.BeginInvoke(DispatcherPriority.Background, () => MoveTo(row, to));
	}

	// Where a row dragged from a_from lands when it is dropped on the row at a_over (on its lower half: a_below) - its
	// place in the list afterwards. Taking the row out first moves every row after it up by one.
	public static int DropPlace(int a_from, int a_over, bool a_below)
	{
		var to = a_over + (a_below ? 1 : 0);
		return to > a_from ? to - 1 : to;
	}

	// ---------------------------------------------------------------- the log
	// the log grows by appending (re-setting a long text every second re-lays the whole box); kept under 200,000 characters
	// the box's length, counted as text is added: reading the box's whole text to measure it copies it (up to 400 KB, a fresh
	// large-object allocation) every second of a run
	int _logChars;

	void OnLog(string a_add, bool a_reset)
	{
		if (a_reset) {
			_logChars = 0;
		}
		_logChars += a_add.Length;
		var trim = _logChars > 200_000;
		void Into(LogView a_box, bool a_follow)
		{
			if (a_reset) {
				a_box.Clear();
			}
			if (a_add.Length > 0) {
				a_box.AppendLines(a_add);
				if (trim) {
					a_box.KeepLast(150_000);
				}
			}
			if (a_follow) {
				a_box.ScrollToEnd();
			}
		}
		Into(LogBox, Follow.IsChecked == true);
		// the log's own window shows the same text
		if (_logOut is { } outBox) {
			Into(outBox, _followOut?.IsChecked == true);
		}
		if (trim) {
			// what the box kept, whole lines only, is the count from here
			_logChars = LogBox.Length;
		}
	}

	bool LogHere => LogBox.Visibility == Visibility.Visible;

	// The log shown in the main window, or hidden there (his list of 2026-11-16: "the progress box above should fill the
	// space if the log box is minimized"). Hidden, its row gives its room to the cards above: the run's card grows into
	// what the other cards leave, and its lists lose the heights they were held to so the log kept its room.
	void ShowLogHere(bool a_show)
	{
		LogBox.Visibility = a_show ? Visibility.Visible : Visibility.Collapsed;
		Follow.Visibility = a_show ? Visibility.Visible : Visibility.Collapsed;
		LogHide.Content = a_show ? "Hide" : "Show";
		LogHide.ToolTip = a_show ? "Hides the log here; the cards above take its room" : "Shows the log here again";
		NowRow.Height = a_show ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
		// With the cards' room theirs to fill, they are at least as tall as that room (the run's card takes what is over);
		// holding more than fits, they scroll as one. With the log shown they are as tall as they need
		if (a_show) {
			System.Windows.Data.BindingOperations.ClearBinding(Cards, MinHeightProperty);
			Cards.MinHeight = 0;
		} else {
			Cards.SetBinding(MinHeightProperty, new System.Windows.Data.Binding(nameof(ScrollViewer.ViewportHeight)) { Source = CardsScroll });
		}
		SummaryScroll.MaxHeight = a_show ? 172 : double.PositiveInfinity;
		IssuesList.MaxHeight = a_show ? 220 : 420;
		LastScroll.MaxHeight = a_show ? 200 : 400;
	}

	// The off-screen pictures' check of the right column (CardsOverLog lays it out): null when the log's heading is whole and
	// inside the column and the run's START is in the cards' view or can be scrolled into it, else what is wrong (a card that
	// was squeezed to nothing has no height to show it in).
	public string? RunControlsProblem()
	{
		if (LogHeadProblem() is { } head) {
			return head;
		}
		if (StartButton.ActualHeight <= 0 || NowCard.ActualHeight < StartButton.ActualHeight) {
			return $"the run's card has no room for START (card {NowCard.ActualHeight:0}, button {StartButton.ActualHeight:0})";
		}
		if (CardsScroll.ViewportHeight < StartButton.ActualHeight) {
			return $"the cards' view is too low to show START ({CardsScroll.ViewportHeight:0})";
		}
		var top = StartButton.TransformToAncestor(Cards).Transform(new Point(0, 0)).Y;
		return top + StartButton.ActualHeight <= CardsScroll.ExtentHeight + 0.5 ? null : $"START lies outside the cards ({top:0} of {CardsScroll.ExtentHeight:0})";
	}

	// the heading's part of that check, on its own (asked in any state of the window)
	public string? LogHeadProblem()
	{
		UpdateLayout();
		var top = LogHead.TransformToAncestor(RightColumn).Transform(new Point(0, 0)).Y;
		return LogHead.ActualHeight >= LogHide.ActualHeight && top >= -0.5 && top + LogHead.ActualHeight <= RightColumn.ActualHeight + 0.5 ? null :
			$"the log's heading is cut off (at {top:0}, {LogHead.ActualHeight:0} high, its buttons {LogHide.ActualHeight:0}, in a column of {RightColumn.ActualHeight:0})";
	}

	// the off-screen pictures only: the log hidden or shown in the main window, and a long list of made-up findings
	public void DemoLogHidden(bool a_hidden) => ShowLogHere(!a_hidden);

	public void DemoFindings(int a_count)
	{
		_vm.Issues.Clear();
		for (var i = 1; i <= a_count; ++i) {
			_vm.Issues.Add($"{Theme.Glyph(i % 4 == 0 ? "IssueBlocks" : "IssueNote")}  A made-up finding, number {i}: long enough to wrap in a narrow window, as a real one about a tool or a setting would");
		}
	}

	void LogHide_Click(object a_s, RoutedEventArgs a_e)
	{
		var show = !LogHere;
		ShowLogHere(show);
		UiState.Set("log.hidden", !show);
		A11y.Say(this, show ? "The log is shown." : "The log is hidden.");
	}

	void LogPop_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_logWindow is { } open) {
			open.Close();
		} else {
			PopLog();
		}
	}

	// The log in a window of its own (his list: "popped out and expanded so users can fully read and scroll through it in a
	// new window"): sized and moved as the player likes, on another screen too. The log hides in the main window while it is
	// out and comes back as it was when that window closes.
	public PanelWindow PopLog()
	{
		if (_logWindow is { } open) {
			Front(open);
			return open;
		}
		var w = new PanelWindow(this, "log", "LOG", 960, 640);
		var box = new LogView();
		box.AppendLines(LogBox.Text);
		box.SetResourceReference(FontFamilyProperty, "MonoFont");
		box.SetResourceReference(FontSizeProperty, "SizeLog");
		box.SetResourceReference(BackgroundProperty, "Panel2");
		box.SetResourceReference(ForegroundProperty, "Text");
		box.SetResourceReference(BorderBrushProperty, "Line");
		AutomationPropertiesName(box, "Run log");
		var follow = new CheckBox { Content = "Follow", IsChecked = true, Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Keeps the newest line in view" };
		var detailed = new CheckBox { Content = "Detailed", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center,
			ToolTip = "Every line of the run, for finding a fault. Unticked: the run step by step in plain words." };
		detailed.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, new System.Windows.Data.Binding(nameof(MainViewModel.DetailedLog)) { Source = _vm, Mode = System.Windows.Data.BindingMode.TwoWay });
		var wrap = new CheckBox { Content = "Wrap lines", Margin = new Thickness(0, 0, 14, 0), VerticalAlignment = VerticalAlignment.Center };
		wrap.Click += (_, _) => box.Wrap = wrap.IsChecked == true;
		var copy = new Button { Content = "Copy all", Style = (Style)FindResource("Ghost"), ToolTip = "The whole log shown here, to the clipboard" };
		copy.Click += (_, _) => {
			try {
				Clipboard.SetText(box.Text);
				_vm.Say("The log is on the clipboard.", "good");
			} catch (System.Runtime.InteropServices.ExternalException) {
				_vm.Say("The clipboard is in use by another program - try again.", "warn");
			}
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		foreach (var c in new UIElement[] { detailed, follow, wrap, copy }) {
			bar.Children.Add(c);
		}
		// his checklist 635: search the log, and jump to the first line the run marked as a problem (Follow lets go, so the line
		// found stays in view while the run writes on)
		var find = new TextBox { MinWidth = 200, Margin = new Thickness(0, 0, 8, 0), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Find in the log (Enter: the next one)" };
		AutomationPropertiesName(find, "Find in the log");
		var next = new Button { Content = "Find next", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		var problem = new Button { Content = "First problem", Style = (Style)FindResource("Ghost"), ToolTip = "The first line the run marked as a failure, else as a warning" };
		var found = new TextBlock { Style = (Style)FindResource("MutedText"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
		void Reveal(int a_at, int a_length, string a_none)
		{
			if (a_at < 0) {
				found.Text = a_none;
				return;
			}
			found.Text = "";
			follow.IsChecked = false;
			box.Reveal(a_at, a_length);
		}
		void FindNext()
		{
			var q = find.Text;
			Reveal(LogSearch.Next(box.Text, q, box.FoundAt + 1), q.Length, q == "" ? "" : "Not in the log.");
		}
		next.Click += (_, _) => FindNext();
		find.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) {
				FindNext();
				find.Focus();
				e.Handled = true;
			}
		};
		void ShowFirstProblem()
		{
			var at = LogSearch.FirstProblem(box.Text);
			var end = at < 0 ? -1 : box.Text.IndexOf('\n', at);
			Reveal(at, at < 0 ? 0 : (end < 0 ? box.Text.Length : end) - at, "No line of this log is marked as a problem.");
		}
		problem.Click += (_, _) => {
			// the simple log marks no line (only the detailed one says "!" and "!!!"): the detailed log is shown first, and the
			// search runs once its text is in (CodeRabbit on pull request #38)
			if (!_vm.DetailedLog) {
				_vm.DetailedLog = true;
				Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, ShowFirstProblem);
				return;
			}
			ShowFirstProblem();
		};
		var findBar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		foreach (var c in new UIElement[] { find, next, problem, found }) {
			findBar.Children.Add(c);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		DockPanel.SetDock(findBar, Dock.Top);
		dock.Children.Add(bar);
		dock.Children.Add(findBar);
		dock.Children.Add(box);
		w.Body = dock;
		_logWindow = w;
		_logOut = box;
		_followOut = follow;
		_logWasHere = LogHere;
		ShowLogHere(false);
		LogHide.IsEnabled = false;
		LogPop.Content = "_Bring back";
		LogPop.ToolTip = "Closes the log's own window and shows the log here again (Ctrl+L brings that window forward)";
		w.PreviewKeyDown += OnKeys;
		w.Closed += (_, _) => {
			_logWindow = null;
			_logOut = null;
			_followOut = null;
			LogHide.IsEnabled = true;
			LogPop.Content = "_Pop out";
			LogPop.ToolTip = "The log in a window of its own: size it, move it to another screen (Ctrl+L)";
			if (!_closingAll) {
				ShowLogHere(_logWasHere);
			}
		};
		w.Show();
		box.ScrollToEnd();
		return w;
	}

	// ---------------------------------------------------------------- panels, each in a window of its own
	void Settings_Click(object a_s, RoutedEventArgs a_e) => ShowPanel("settings");

	// every panel window closed (the off-screen pictures between two of them)
	public void ClosePanels()
	{
		foreach (var w in _panels.Values.ToList()) {
			w.Close();
		}
		_logWindow?.Close();
	}

	// a window the player asked for is brought forward (never for the off-screen pictures: nothing of theirs comes to the front)
	static void Front(Window a_w)
	{
		if (App.Headless) {
			return;
		}
		if (a_w.WindowState == WindowState.Minimized) {
			a_w.WindowState = WindowState.Normal;
		}
		a_w.Activate();
	}

	// the window a question about a panel's content belongs to: that panel's own, else this one
	Window Over(string a_group) => _panels.TryGetValue(a_group, out var w) ? w : this;

	// settings typed but not saved: asked before they are thrown away
	bool LeaveSettings() =>
		!_panels.ContainsKey("settings") || !SettingsPanel.HasChanges ||
		Ask.Show(Over("settings"), "Settings not saved", "You changed settings and did not press Save. Leave without saving them?", "Leave without saving", "Keep editing");

	// the off-screen pictures only: the live view stops and a made-up run is shown (a_frame moves it on a minute a frame)
	public void Demo(bool a_waiting, int a_frame = 0)
	{
		StopLooking();
		_vm.Demo(a_waiting, a_frame);
	}

	// the off-screen pictures only: the made-up run ended (its summary), and the window as a first start shows it
	public void DemoEnded(bool a_failed)
	{
		StopLooking();
		_vm.DemoEnded(a_failed);
	}

	public void DemoFirstStart()
	{
		StopLooking();
		_vm.DemoFirstStart();
	}

	public void DemoWaitsOutside()
	{
		StopLooking();
		_vm.DemoWaitsOutside();
	}

	// the page's pictures only: the dashboard with nothing running, and past runs of the made-up setup
	public void DemoReady()
	{
		StopLooking();
		_vm.DemoReady();
	}

	void StopLooking()
	{
		_demo = true;
		_timer.Stop();
	}

	public void DemoHistory(IEnumerable<HistoryRow> a_rows) => _vm.DemoHistory(a_rows);

	// History, Settings and Themes each open in a window of their own (his list of 2026-11-16: "pop out boxes like queue is
	// instead of stuck to the right of the app", resizable). The History window has two views, the runs and the MO2
	// Changelog. Pressing a panel's button again brings its window forward as it is; a_fresh builds its content anew (Save,
	// Undo changes). The main window stays usable beside them.
	// a_quiet: an open panel's content is made anew where it stands - it is not brought forward and does not take the keyboard
	public PanelWindow ShowPanel(string a_which, bool a_fresh = false, bool a_quiet = false)
	{
		var group = a_which == "changes" ? "history" : a_which;
		var isNew = !_panels.TryGetValue(group, out var w);
		if (w != null && !a_fresh && (group != "history" || a_which == _historyView)) {
			Front(w);
			return w;
		}
		if (w == null) {
			var (width, height) = group switch { "settings" => (900.0, 720.0), "themes" => (780.0, 700.0), _ => (780.0, 680.0) };
			w = new PanelWindow(this, group, "", width, height);
			_panels[group] = w;
			w.PreviewKeyDown += OnKeys;
			w.Closing += (_, e) => {
				if (group == "settings" && !_closingAll && !LeaveSettings()) {
					e.Cancel = true;
				}
			};
			w.Closed += (_, _) => {
				_panels.Remove(group);
				if (group == "settings") {
					SettingsPanel.Closed();
				}
			};
		}
		if (group == "history") {
			_historyView = a_which;
		}
		w.Head(a_which switch { "history" => "HISTORY", "changes" => "MO2 CHANGELOG", "themes" => "THEMES", _ => "SETTINGS" });
		// while the setup is renamed its settings are not shown: what was typed there would belong to the old file, whose Save
		// is refused once the rename is done. The rename shows them when it ends
		var held = a_which == "settings" && _vm.Renaming;
		w.Body = a_which switch {
			"history" => WithSwitch(HistoryPanel(), a_which),
			"changes" => WithSwitch(ChangesPanel(null), a_which),
			"themes" => ThemeGallery.Build(this, _vm),
			_ when held => RenameNotice(),
			_ => SettingsPanel.Build(this, _vm),
		};
		if (w.Body is DependencyObject drawn) {
			WindowWords.Tree(drawn);
		}
		if (isNew) {
			w.Show();
			A11y.Say(w, "Opened: " + w.Title + ". Escape closes it.");
		} else if (!a_quiet) {
			Front(w);
		}
		if (!a_quiet || isNew) {
			w.TakeKeyboard(a_which == "settings" && !held ? SettingsPanel.FocusFind : null);
		}
		return w;
	}

	TextBlock RenameNotice()
	{
		var t = new TextBlock { Text = "The setup is being renamed. Its settings show here as soon as that is done.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) };
		t.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
		return t;
	}

	// Another setup was picked while Settings stands open: it shows that setup's settings from here - unless something typed
	// there is not saved yet. That stays, and its Save says whose settings these are and writes them to no other setup.
	void SettingsFollow()
	{
		if (_panels.ContainsKey("settings") && !SettingsPanel.HasChanges && !SettingsPanel.IsFor(_vm.Setup)) {
			ShowPanel("settings", true, true);
		}
	}

	// the off-screen pictures only: the changelog view filled with made-up rows (nothing read or written)
	public PanelWindow ShowChangesDemo(IReadOnlyList<Mo2Changes.Change> a_rows)
	{
		var w = ShowPanel("history");
		_historyView = "changes";
		w.Head("MO2 CHANGELOG");
		w.Body = WithSwitch(ChangesPanel(a_rows), "changes");
		return w;
	}

	// the history window's two views: the runs with their logs, and what changed in Mod Organizer
	DockPanel WithSwitch(FrameworkElement a_body, string a_which)
	{
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
		foreach (var (key, text, tip) in new[] { ("history", "Runs", "Ctrl+H"), ("changes", "MO2 Changelog", "Ctrl+M") }) {
			var b = new Button { Content = text, Style = (Style)FindResource(key == a_which ? "Primary" : "Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = tip };
			b.Click += (_, _) => ShowPanel(key);
			bar.Children.Add(b);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(bar, Dock.Top);
		dock.Children.Add(bar);
		dock.Children.Add(a_body);
		return dock;
	}

	// what Mod Organizer changed (mods added, removed, ticked, unticked, moved - per profile), newest first; opening it looks at
	// Mod Organizer, unless a run is going or did not finish (its changes would read as made outside the runner)
	FrameworkElement ChangesPanel(IReadOnlyList<Mo2Changes.Change>? a_demo)
	{
		var setup = _vm.Setup;
		if (setup == null && a_demo == null) {
			return new TextBlock { Text = "Make or pick a setup first.", TextWrapping = TextWrapping.Wrap };
		}
		var note = new TextBlock { Style = (Style)FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) };
		var filter = new TextBox { MinWidth = 240, Margin = new Thickness(0, 0, 10, 0), VerticalContentAlignment = VerticalAlignment.Center };
		AutomationPropertiesName(filter, "Find a mod or a profile");
		var look = new Button { Content = "Look now", Style = (Style)FindResource("Ghost"), ToolTip = "Notes what changed in Mod Organizer since the last look" };
		var tools = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
		tools.Children.Add(new TextBlock { Text = "Find", Foreground = Brushes2.Get("Muted"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
		tools.Children.Add(filter);
		tools.Children.Add(look);
		var list = new ListBox { Background = Brushes2.Get("Panel2"), BorderBrush = Brushes2.Get("Line") };
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		AutomationPropertiesName(list, "MO2 Changelog");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock TextWrapping='Wrap'><Run Text='{Binding Mod, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding What, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Detail}' TextWrapping='Wrap' Opacity='0.8' FontSize='{DynamicResource SizeSmall}'/></StackPanel></DataTemplate>");
		var rows = new List<ChangeRow>();
		void Fill()
		{
			var f = filter.Text.Trim();
			list.ItemsSource = f == "" ? rows : rows.Where(r => r.Mod.Contains(f, StringComparison.OrdinalIgnoreCase) || r.Detail.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
		}
		void Show(IEnumerable<Mo2Changes.Change> a_shown) =>
			rows = a_shown.Reverse().Select(r => new ChangeRow(r.Mod, r.What,
				$"{r.When.ToString("MMM d, HH:mm", InvariantCulture)}   ·   {(r.Profiles.Count == 0 ? "Mod Organizer's mods" : string.Join(", ", r.Profiles))}   ·   {r.Who}")).ToList();
		async void Load(bool a_look)
		{
			look.IsEnabled = false;
			note.Text = a_look ? "Looking at Mod Organizer ..." : "Reading ...";
			try {
				var (shown, since, found) = await Task.Run(() => {
					var mo2 = Mo2Instance.Load(setup!.Mo2Exe, setup.Mo2Instance);
					var dir = Mo2Changes.Dir(AppPaths.Data, mo2);
					// a run's changes in the middle of it (or of one that did not finish) are never noted as made outside the runner:
					// the look holds the run lock from its check to its end (null = a run goes or did not finish)
					var n = a_look ? Mo2Changes.RecordOutside(mo2, AppPaths.Data) : -1;
					return (Mo2Changes.Shown(Mo2Changes.Read(dir), setup.ChangesHideProfiles, setup.ChangesHideMods), Mo2Changes.WatchedSince(dir), n);
				});
				Show(shown);
				note.Text = (since == null ? "Not watched yet: the first look starts it." : $"Watched since {since.Value.ToString("MMM d, yyyy HH:mm", InvariantCulture)}.") +
					" A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts." +
					(found > 0 ? $" {found} new." : found == 0 ? " Nothing new." : found == null ? " No new look while a run is going or did not finish." : "") +
					(rows.Count == 0 && since != null ? " No changes noted yet." : "");
				Fill();
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException) {
				note.Text = "Could not read Mod Organizer: " + e.Message;
			} finally {
				look.IsEnabled = a_demo == null;
			}
		}
		look.Click += (_, _) => Load(true);
		filter.TextChanged += (_, _) => Fill();
		if (a_demo != null) {
			Show(a_demo);
			note.Text = "Watched since Oct 20, 2026 18:00. A change made outside the runner shows with the time it was noticed - when this view opens, on Look now, or when a run starts.";
			Fill();
			look.IsEnabled = false;
		} else {
			Load(true);
		}
		var dock = new DockPanel();
		DockPanel.SetDock(note, Dock.Top);
		DockPanel.SetDock(tools, Dock.Top);
		dock.Children.Add(note);
		dock.Children.Add(tools);
		dock.Children.Add(list);
		return dock;
	}

	Grid HistoryPanel()
	{
		var grid = new Grid();
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) });
		grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
		grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) });
		var list = new ListBox { ItemsSource = _vm.History };
		// the theme's colors as live references: a theme or Windows' high contrast changed while History is open repaints it too
		list.SetResourceReference(BackgroundProperty, "Panel2");
		list.SetResourceReference(BorderBrushProperty, "Line");
		// a long verdict wraps instead of running off the right edge
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		AutomationPropertiesName(list, "Runs");
		list.ItemTemplate = (DataTemplate)System.Windows.Markup.XamlReader.Parse(
			"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Margin='2,4'>" +
			"<TextBlock><Run Text='{Binding When, Mode=OneWay}' FontWeight='SemiBold'/><Run Text='   '/><Run Text='{Binding Setup, Mode=OneWay}'/><Run Text='   '/><Run Text='{Binding Took, Mode=OneWay}'/></TextBlock>" +
			"<TextBlock Text='{Binding Verdict}' TextWrapping='Wrap' Opacity='0.8' FontSize='{DynamicResource SizeSmall}'/></StackPanel></DataTemplate>");
		var log = new TextBox { IsReadOnly = true, FontFamily = (System.Windows.Media.FontFamily)FindResource("MonoFont"), FontSize = (double)FindResource("SizeLog"), Background = Brushes2.Get("Panel2"),
			Foreground = Brushes2.Get("Text"), VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
		AutomationPropertiesName(log, "The run's log");
		var open = new Button { Content = "Open folder", Style = (Style)FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0), ToolTip = "This run's folder: its logs and its record" };
		// his checklist 482: one zip for a bug report, names masked; made on the player's press, never sent anywhere
		var report = new Button { Content = "Bug report ...", Style = (Style)FindResource("Ghost"),
			ToolTip = "Puts this run's logs, its setup and the versions of everything into one zip, with your Windows name and PC name masked - for a GitHub issue" };
		report.Click += async (_, _) => {
			var row = list.SelectedItem as HistoryRow;
			report.IsEnabled = false;
			try {
				var zip = await Task.Run(() => BugReport.Make(AppPaths.Data, row == null ? null : Journal.Load(row.Folder), Path.Combine(AppPaths.Data, "Bug reports"), AppVersion));
				if (Ask.Show(Over("history"), "Bug report made", $"It is ready: {Path.GetFileName(zip)}\n\nIt holds {(row == null ? "the program's own notes" : "this run's logs and record")}, the setup it ran and the " +
				    "versions of Windows, Mod Organizer, the game and each tool. Your Windows user name and your PC's name are masked. Nothing was sent anywhere.\n\n" +
				    "To report the bug: open an issue on the program's GitHub page, say what happened and what you expected, and attach this zip.",
				    "Show the zip and open the issue page", "Close")) {
					System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{zip}\"") { UseShellExecute = true });
					if (MainViewModel.OpenPage(BugReport.Issues) is { } why) {
						_vm.Say("The issue page did not open: " + why, "bad");
					}
				}
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException or System.ComponentModel.Win32Exception) {
				_vm.Say("The bug report could not be made: " + e.Message, "bad");
			} finally {
				report.IsEnabled = true;
			}
		};
		// his checklist 454: asked first; the box names what it does, and a run that cannot be undone says why
		var undo = new Button { Content = "Undo this run ...", Style = (Style)FindResource("Ghost"), Margin = new Thickness(8, 0, 0, 0),
			ToolTip = "Puts each output and your mod and plugin lists back as they were before this run - only the last run on that profile" };
		undo.Click += async (_, _) => {
			if (list.SelectedItem is not HistoryRow row) {
				_vm.Say("Pick a run first.", "warn");
				return;
			}
			undo.IsEnabled = false;
			try {
				try {
					// it reads every run record: off the window's thread
					if (await Task.Run(() => RunUndo.WhyNot(Journal.Load(row.Folder), Engine.Runs())).ConfigureAwait(true) is { } why) {
						_vm.Say("It cannot be undone: " + why + ".", "bad");
						return;
					}
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or System.Text.Json.JsonException) {
					_vm.Say("It cannot be undone: " + e.Message, "bad");
					return;
				}
				if (!Ask.Show(Over("history"), "Undo this run?", "Each output goes back to what it was before this run, and your mod and plugin lists too. Mod Organizer closes for it. The mods you " +
					"ticked, unticked or installed since stay as you left them; a change of order goes back with the lists. This run's outputs stay in its backup.", "Undo the run", "Keep it")) {
					return;
				}
				await _vm.Undo(row.Folder).ConfigureAwait(true);
			} finally {
				undo.IsEnabled = true;
			}
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 10) };
		buttons.Children.Add(open);
		buttons.Children.Add(report);
		buttons.Children.Add(undo);
		list.SelectionChanged += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				var p = MainViewModel.LogFile(r.Folder, _vm.DetailedLog);
				try {
					log.Text = File.Exists(p) ? Tail(p, 150_000) : "(no log)";
					log.ScrollToEnd();
				} catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
					log.Text = e.Message;
				}
			}
		};
		open.Click += (_, _) => {
			if (list.SelectedItem is HistoryRow r) {
				MainViewModel.OpenFolder(r.Folder);
			}
		};
		if (_vm.History.Count == 0) {
			log.Text = "No runs of this setup yet.";
		}
		// an empty list says why it is empty (a blank box reads as broken)
		var empty = new TextBlock { Text = "No runs of this setup yet - its runs show here, newest first, each with its log.", Style = (Style)FindResource("MutedText"),
			Margin = new Thickness(14), IsHitTestVisible = false, Visibility = _vm.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed };
		// it follows the list: a first run that ends while History is open takes the hint away (the handler goes with the panel, so
		// panels opened again and again leave nothing hanging on the list)
		void Follow(object? a_s, System.Collections.Specialized.NotifyCollectionChangedEventArgs a_e) =>
			empty.Visibility = _vm.History.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
		_vm.History.CollectionChanged += Follow;
		empty.Unloaded += (_, _) => _vm.History.CollectionChanged -= Follow;
		Grid.SetRow(list, 0);
		Grid.SetRow(empty, 0);
		Grid.SetRow(buttons, 1);
		Grid.SetRow(log, 2);
		grid.Children.Add(list);
		grid.Children.Add(empty);
		grid.Children.Add(buttons);
		grid.Children.Add(log);
		if (_vm.History.Count > 0) {
			list.SelectedIndex = 0;
		}
		return grid;
	}

	// a log's last part (a whole long log in one box freezes the window)
	static string Tail(string a_path, int a_bytes)
	{
		using var fs = new FileStream(a_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		var start = Math.Max(0, fs.Length - a_bytes);
		fs.Seek(start, SeekOrigin.Begin);
		var bytes = new byte[fs.Length - start];
		fs.ReadExactly(bytes);
		var text = System.Text.Encoding.UTF8.GetString(bytes);
		return start == 0 ? text : "(the start of this log is in its folder)\n" + text[(text.IndexOf('\n') + 1)..];
	}

	static void AutomationPropertiesName(DependencyObject a_o, string a_name) => System.Windows.Automation.AutomationProperties.SetName(a_o, a_name);

	// his word of 2026-11-21: each tool of the setup set up by hand in its own window, whenever wanted
	bool _toolsAsked;

	async void Tools_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.Setup is not { } setup) {
			_vm.Say("Make or pick a setup first.", "bad");
			return;
		}
		if (_toolsAsked) {
			return;
		}
		_toolsAsked = true;
		try {
			// (read from a copy made here, on the window's thread: the worker never walks the setup's own step list, which a
			// step dragged meanwhile rewrites under it)
			var asItIs = setup.Clone();
			var tools = await Task.Run(() => ToolSettings.ToSetUp(asItIs));
			ToolsDialog.Run(this, _vm, asItIs, tools, false);
		} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) {
			_vm.Say("This setup's tools could not be read: " + e.Message, "bad");
		} finally {
			_toolsAsked = false;
		}
	}

	// the off-screen pictures: that box as it shows before a first run, with made-up tools, one of them set up already
	public Window DemoToolsBox() => ToolsDialog.Make(this, _vm, _vm.Setup ?? Setup.FromJson([], "My Profile.json"), [
		new("xlodgen", "xLODGen", null), new("texgen", "TexGen", new ToolSettings.Learned { At = new DateTime(2026, 10, 27, 20, 14, 0, DateTimeKind.Local), Files = [new()] }),
		new("dyndolod", "DynDOLOD", null), new("pandora", "Pandora", null)], true, out _);

	// ---------------------------------------------------------------- a new setup
	void Queue_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.IsRunning) {
			_vm.Say("A run is going already.", "bad");
			return;
		}
		if (QueueDialog.Run(this, _vm.Setups.Select(s => s.File).ToList()) is { } q) {
			_ = _vm.StartQueue(q.Files, q.KeepGoing);
		}
	}

	void NewSetup_Click(object a_s, RoutedEventArgs a_e)
	{
		if (_vm.IsRunning) {
			return;
		}
		if (_vm.MovingData) {
			_vm.Say(_vm.WhyNotWriting("made")!, "bad");
			return;
		}
		var made = NewSetupDialog.Run(this);
		if (made != null) {
			_vm.LoadSetups(made);
			_vm.Say("Setup made. Press Check (F5) to see that every tool is found, then Start.", "good");
		}
	}

	bool _stopping;
	bool _closeNow;
	bool _closeWhenFree;

	// the window goes once the run has stopped and put the profile back; a run that does not stop in 10 minutes keeps the window
	// a run going, a start not yet answered (the engine still checking, no run record yet), or a queue between two setups
	bool StillGoing() => Engine.Active() is not null || QueueRun.Read(AppPaths.Data) != null || _vm.Starting;

	async Task CloseWhenStopped(Journal? a_run)
	{
		var until = DateTime.Now.AddMinutes(10);
		// every run that appears meanwhile is stopped too: the one a start was still checking for, a queue's next setup (a stopped
		// run ends the queue)
		var stopped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (a_run != null) {
			stopped.Add(a_run.Folder);
		}
		while (DateTime.Now < until && StillGoing()) {
			// a stop that could not be written is written again at the next beat
			if (Engine.Active() is { } next && stopped.Add(next.Folder) && MainViewModel.TrySend(next.Folder, "stop") != null) {
				stopped.Remove(next.Folder);
			}
			await Task.Delay(1000);
		}
		_stopping = false;
		if (StillGoing()) {
			_vm.Say("The run did not stop in 10 minutes, so the window stays open. Press Stop now again.", "bad");
			return;
		}
		_closeNow = true;
		Close();
	}

	// what the window remembers is being written as it closes / is written, and the window goes
	bool _keeping;
	bool _kept;

	// The window is out of sight already: a setup that is still being written is waited for (a change the window took a
	// moment ago - a tick, a place, use old output - is in its file before the program ends), what the window remembers is
	// written away from its thread, then it closes for good
	async Task KeepThenClose()
	{
		try {
			try {
				await MainViewModel.PendingSetupSave;
			} catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException) {
				// (the write says by itself why it failed; nothing of it holds the window open)
			}
			await Task.Run(UiState.SaveAtClose);
		} finally {
			_keeping = false;
			_kept = true;
			Close();
		}
	}

	protected override void OnClosing(System.ComponentModel.CancelEventArgs a_e)
	{
		// the second pass, after the write: nothing is asked again, the window goes
		if (_kept) {
			base.OnClosing(a_e);
			return;
		}
		if (_keeping) {
			a_e.Cancel = true;
			base.OnClosing(a_e);
			return;
		}
		if (!LeaveSettings()) {
			a_e.Cancel = true;
		} else if (_vm.InHand != "") {
			// a put-back, an undo, a tool opened from Settings: this window's own work, cut off half way by closing it. The window
			// stays until it is done and then closes by itself
			_vm.Say($"Not closed yet: {_vm.InHand}. The window closes by itself when that is done.", "warn");
			_closeWhenFree = true;
			a_e.Cancel = true;
		} else if (!_closeNow && StillGoing()) {
			// a run never goes on unseen behind a closed window (it closed his Mod Organizer a minute after the window went), not
			// even one still starting: closing stops it - the tool closed, the profile put back - or the window stays open
			var run = Engine.Active();
			if (_stopping) {
				_vm.Say("Still stopping the run - the window closes once your profile is back.", "warn");
			} else if (Ask.Show(this, run == null ? "A run is starting" : "A run is going",
				run == null
					? "Closing the window stops the run as soon as it starts (nothing has been changed yet), and a queue starts no further setup. The window closes once that is done."
					: "Closing the window stops the run: the tool it is running is closed and your profile put back, as with Stop now. The window closes once that is done.",
				"Stop the run and close", "Keep the window open")) {
				if (run != null && MainViewModel.TrySend(run.Folder, "stop") is { } why) {
					_vm.Say("The run did not get the stop: " + why + " - the window stays open; close it again to try once more.", "bad");
				} else {
					_vm.Say("Stopping the run - the window closes once your profile is back.", "warn");
					_stopping = true;
					_ = CloseWhenStopped(run);
				}
			}
			a_e.Cancel = true;
		}
		// it really closes: its panel windows go with it without a question of their own, and its size is kept
		if (!a_e.Cancel) {
			_closingAll = true;
			KeepSize();
			// its panel windows now, so that their sizes are kept before the write below (closed only as this window goes,
			// they were kept in memory alone, with nothing left to write them in time)
			ClosePanels();
			if (UiState.Unsaved || !MainViewModel.PendingSetupSave.IsCompleted) {
				// What the window remembers goes into its file before the program ends, and never on this thread: the window
				// is out of sight at once and closes when the write is done (on a slow disk it stood there frozen, and the
				// write held this thread) - and so does a setup that is still on its way into its file. An end that cannot
				// be put off - Windows logging off - is covered by App.OnExit
				a_e.Cancel = true;
				_keeping = true;
				Hide();
				_ = KeepThenClose();
			}
		}
		base.OnClosing(a_e);
	}

	protected override void OnSourceInitialized(EventArgs a_e)
	{
		base.OnSourceInitialized(a_e);
		var helper = new WindowInteropHelper(this);
		_tray = new Tray(helper.Handle);
		// a process that ends without closing the window (a crash, Windows logging off) takes its icon with it
		AppDomain.CurrentDomain.ProcessExit += (_, _) => _tray?.Dispose();
		HwndSource.FromHwnd(helper.Handle)?.AddHook((nint _, int a_msg, nint _, nint _, ref bool _) => {
			if ((uint)a_msg == Tray.TaskbarCreated) {
				_tray?.Readd();
			}
			return 0;
		});
	}

	protected override void OnClosed(EventArgs a_e)
	{
		Theme.Applied -= _vm.Repaint;
		_demo = true;
		_timer.Stop();
		_watch?.Dispose();
		_tray?.Dispose();
		base.OnClosed(a_e);
	}
}

public sealed record ChangeRow(string Mod, string What, string Detail)
{
	public override string ToString() => $"{Mod} {What}, {Detail}";
}

// a themed yes / no window (never a plain system box)
public static class Ask
{
	public static bool Show(Window a_owner, string a_title, string a_text, string a_yes, string a_no)
	{
		var result = false;
		var w = Make(a_owner, a_title, a_text, a_yes, a_no, a_yesPressed => result = a_yesPressed);
		w.ShowDialog();
		return result;
	}

	// A name typed in (a setup copied or renamed): null when the player went back. a_bad says what is wrong with a name, in words
	// (null = good); the box stays until a good one is typed or it is closed
	public static string? Input(Window a_owner, string a_title, string a_text, string a_value, string a_yes, Func<string, string?> a_bad)
	{
		string? result = null;
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = a_title, Width = Math.Min(520, room.Width), SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false, MinWidth = UiState.Least(360, room.Width),
			FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		var box = new TextBox { Text = a_value, Margin = new Thickness(0, 10, 0, 0) };
		System.Windows.Automation.AutomationProperties.SetName(box, a_title);
		var why = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Foreground = Brushes2.Get("Bad") };
		var yes = new Button { Content = a_yes, Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var no = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		void Judge() => why.Text = a_bad(box.Text) ?? "";
		box.TextChanged += (_, _) => Judge();
		yes.Click += (_, _) => {
			Judge();
			if (why.Text == "") {
				result = box.Text.Trim();
				w.Close();
			}
		};
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(yes);
		buttons.Children.Add(no);
		var body = new StackPanel { Margin = new Thickness(22) };
		body.Children.Add(new TextBlock { Text = a_title, Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 12), FontSize = (double)a_owner.FindResource("SizeHeading1") });
		body.Children.Add(new TextBlock { Text = a_text, TextWrapping = TextWrapping.Wrap });
		body.Children.Add(box);
		body.Children.Add(why);
		body.Children.Add(buttons);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = body };
		w.Loaded += (_, _) => {
			box.Focus();
			box.SelectAll();
			A11y.Say(w, a_title + ". " + a_text, true);
		};
		w.ShowDialog();
		return result;
	}

	// a notice with one button (Enter and Esc both close it)
	public static void Tell(Window a_owner, string a_title, string a_text, string a_ok) => Make(a_owner, a_title, a_text, a_ok, null, _ => { }).ShowDialog();

	// the box itself (the off-screen pictures draw it without waiting on it)
	public static Window Make(Window a_owner, string a_title, string a_text, string a_yes, string? a_no, Action<bool> a_done)
	{
		// its sizes held to the screen (a least width above the screen's would win over the width it is given)
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = a_title, Width = Math.Min(560, room.Width), SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false, MinWidth = UiState.Least(380, room.Width),
			FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		var yes = new Button { Content = a_yes, Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, a_no == null ? 0 : 10, 0) };
		// a notice has only its one button: it is the safe answer too
		var no = new Button { Content = a_no ?? "", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = a_no != null, IsDefault = a_no != null };
		if (a_no == null) {
			yes.IsCancel = yes.IsDefault = true;
		}
		yes.Click += (_, _) => {
			a_done(true);
			w.Close();
		};
		no.Click += (_, _) => w.Close();
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
		buttons.Children.Add(yes);
		if (a_no != null) {
			buttons.Children.Add(no);
		}
		// its heading on top, its buttons at the bottom, its text in between taking whatever room the box is given
		var body = new DockPanel { Margin = new Thickness(22) };
		var head = new TextBlock { Text = a_title, Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 12), FontSize = (double)a_owner.FindResource("SizeHeading1") };
		DockPanel.SetDock(head, Dock.Top);
		DockPanel.SetDock(buttons, Dock.Bottom);
		body.Children.Add(head);
		body.Children.Add(buttons);
		// a long text (a run's preview) scrolls inside the box instead of growing past the screen
		var scroll = new ScrollViewer { MaxHeight = 460, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = true,
			Content = new TextBlock { Text = a_text, TextWrapping = TextWrapping.Wrap } };
		System.Windows.Automation.AutomationProperties.SetName(scroll, a_title);
		body.Children.Add(scroll);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = body };
		// the safe answer has the keyboard; a screen reader hears the whole question, not only the button
		w.Loaded += (_, _) => {
			// it opened as tall as its text needs (a long one held to the scroller's limit); from here its size is the
			// player's to change, and the text follows the box
			w.MinHeight = Math.Min(w.ActualHeight, 200);
			w.SizeToContent = SizeToContent.Manual;
			scroll.MaxHeight = double.PositiveInfinity;
			(a_no == null ? yes : no).Focus();
			A11y.Say(w, a_title + ". " + a_text, true);
		};
		return w;
	}
}
