using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// One box, before a setup's first run and whenever wanted after: every tool of the setup that has a window of its own, each
// with a button that opens that window through
// Mod Organizer. The player sets the tool up there, saves and closes it, and its settings are kept with the setup and used
// for every run (ToolSettings.Learn - nothing of a tool's window is copied here). The box shows by itself before a setup's
// first run, and from "Tools" at the top of the window whenever a tool's settings are to be changed.
public static class ToolsDialog
{
	sealed class Row
	{
		public required string Step { get; init; }
		public required string Name { get; init; }
		public required TextBlock State { get; init; }
		public required Button Open { get; init; }
		public bool SetUp { get; set; }
	}

	// what a tool's row says about it
	public static string StateOf(ToolSettings.Learned? a_learned) => a_learned == null
		? "not set up yet - it runs with whatever its own window last saved"
		: $"set up {a_learned.At.ToLocalTime().ToString("MMM d HH:mm", InvariantCulture)} - every run uses those settings";

	// what it says once its window has closed. a_error: why its settings were not kept (null: they were)
	public static string AfterItsWindow(string a_name, ToolSettings.Learned? a_learned, string? a_error) =>
		a_learned != null ? $"set up now ({a_learned.Files.Count} settings file(s) kept) - every run uses them" :
		a_error != null && a_error.Contains(ToolSettings.SavedNothing, StringComparison.Ordinal) ? $"left as it is: {a_name} saved nothing new, so it runs with the settings it had" :
		"not set up: " + (a_error ?? "its window gave no settings");

	// what a tool's row says once its opening has ended, however it ended
	public static string Outcome(string a_name, ToolOpen.Result a_r) => a_r.How switch {
		ToolOpen.Ending.NotNow => $"not opened: {a_r.Why} - close it, then set {a_name} up",
		ToolOpen.Ending.Stopped => a_r.Why ?? "stopped waiting",
		_ => AfterItsWindow(a_name, a_r.Learned, a_r.Why),
	};

	public static string Capital(string a_text) => a_text.Length == 0 ? a_text : char.ToUpperInvariant(a_text[0]) + a_text[1..];

	// What is added when the mod a tool's window was started from could not be switched off again afterwards ("" when it
	// could, or there was none): the tool's settings may be kept all the same, and the mod is still ticked in the player's
	// profile - said where they look, never only in a log nobody is shown
	public static string StillOn(string? a_mod) => a_mod == null ? ""
		: $" The mod \"{a_mod}\", switched on to open it, could not be switched off again: untick it in Mod Organizer (the next run, or the next tool set up here, switches it off too).";

	// true: go on (shown before a first run: the run starts); false: not now
	public static bool Run(MainWindow a_owner, MainViewModel a_vm, Setup a_setup, IReadOnlyList<ToolSettings.ToSet> a_tools, bool a_beforeFirstRun)
	{
		var w = Make(a_owner, a_vm, a_setup, a_tools, a_beforeFirstRun, out var went);
		w.ShowDialog();
		return went();
	}

	// the box itself (the off-screen pictures draw it without waiting on it)
	public static Window Make(MainWindow a_owner, MainViewModel a_vm, Setup a_setup, IReadOnlyList<ToolSettings.ToSet> a_tools, bool a_beforeFirstRun, out Func<bool> a_went)
	{
		var go = false;
		a_went = () => go;
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = "Set up your tools", Width = UiState.Fit(UiState.Number("tools.width"), 720, 480, room.Width),
			Height = UiState.Fit(UiState.Number("tools.height"), 560, 360, room.Height), MinWidth = UiState.Least(480, room.Width), MinHeight = UiState.Least(360, room.Height),
			WindowStartupLocation = WindowStartupLocation.CenterOwner, Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize,
			ShowInTaskbar = false, FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		w.Closed += (_, _) => {
			UiState.Set("tools.width", w.ActualWidth);
			UiState.Set("tools.height", w.ActualHeight);
		};
		var dock = new DockPanel { Margin = new Thickness(22) };
		var top = new StackPanel();
		DockPanel.SetDock(top, Dock.Top);
		dock.Children.Add(top);
		top.Children.Add(new TextBlock { Text = "SET UP YOUR TOOLS", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = (double)a_owner.FindResource("SizeHeading1") });
		top.Children.Add(new TextBlock {
			Text = (a_beforeFirstRun ? $"\"{a_setup.Name}\" has not run yet. " : "") +
			       "Each tool has settings of its own - which worlds it builds, its presets, its options. Open a tool here, set it up in its own window the way every run " +
			       "should use it, then save and close it: its settings are kept with this setup and used for every run. A tool you leave as it is runs with whatever its own " +
			       "window last saved. " + (a_beforeFirstRun ? "You can come back to this any time: Tools, at the top of the window." : "To change a tool's settings later, set it up again here."),
			Style = (Style)a_owner.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
		});

		var bottom = new StackPanel();
		DockPanel.SetDock(bottom, Dock.Bottom);
		dock.Children.Add(bottom);
		var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Foreground = Brushes2.Get("Text") };
		bottom.Children.Add(status);

		var rows = new List<Row>();
		var list = new StackPanel();
		foreach (var tool in a_tools) {
			var name = new TextBlock { Text = tool.Name, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
			var state = new TextBlock { Text = StateOf(tool.Learned), Style = (Style)a_owner.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
				Margin = new Thickness(12, 0, 12, 0) };
			var open = new Button { Content = tool.Learned == null ? "Set it up" : "Set up again", Style = (Style)a_owner.FindResource("Ghost"), VerticalAlignment = VerticalAlignment.Center,
				ToolTip = $"Opens {tool.Name} in its own window, through Mod Organizer (Mod Organizer is closed first if it is open)" };
			System.Windows.Automation.AutomationProperties.SetName(open, $"{open.Content}: {tool.Name}");
			var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			Grid.SetColumn(state, 1);
			Grid.SetColumn(open, 2);
			grid.Children.Add(name);
			grid.Children.Add(state);
			grid.Children.Add(open);
			list.Children.Add(grid);
			rows.Add(new Row { Step = tool.Step, Name = tool.Name, State = state, Open = open, SetUp = tool.Learned != null });
		}
		if (rows.Count == 0) {
			list.Children.Add(new TextBlock { Text = "None of this setup's switched-on steps has a tool with a window of its own to set up.", TextWrapping = TextWrapping.Wrap });
		}

		var turn = new Button { Content = "Set up each in _turn", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 10, 0),
			ToolTip = "Opens each tool that is not set up yet, one after the other: the next opens when you close the one before" };
		var start = new Button { Content = a_beforeFirstRun ? "_Continue to the run" : "Close", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true,
			IsCancel = !a_beforeFirstRun };
		var notNow = new Button { Content = "Not now", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 10, 0), IsCancel = true };

		// stops the wait for a tool (before its window is up, at once; after, on the player's word)
		var stop = new Button { Content = "_Stop waiting", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 10, 0), Visibility = Visibility.Collapsed,
			ToolTip = "Gives up waiting for the tool: one not open yet is not opened; one already open stays open, but nothing it saves is kept" };

		// one tool's own window is open (or being opened), or a walk through several is under way
		var toolOpen = false;
		var walking = false;
		var stopWalk = false;
		// the opening under way, and whether the box is to close once it has stopped
		ToolOpen? opening = null;
		var openingName = "the tool";
		var closeAfter = false;
		// while a run goes its tools are the run's: they are set up when it is done
		bool InUse() => a_vm.IsRunning || a_vm.Starting;
		void Buttons()
		{
			var free = !toolOpen && !InUse();
			foreach (var r in rows) {
				r.Open.IsEnabled = free && !walking;
			}
			turn.IsEnabled = rows.Count > 0 && !InUse() && (walking ? !stopWalk : !toolOpen);
			start.IsEnabled = !toolOpen && !walking;
			notNow.IsEnabled = !toolOpen && !walking;
			stop.Visibility = toolOpen ? Visibility.Visible : Visibility.Collapsed;
			stop.IsEnabled = toolOpen && opening is { Stopping: false };
		}

		// the player gives up the wait: before the tool's window is up it simply stops; once it is up, only when they say so
		bool StopWaiting(string a_name)
		{
			if (opening is not { Stopping: false } o) {
				return opening != null;
			}
			if (!o.StopIfNotOpen() && !Ask.Show(w, $"Stop waiting for {a_name}?", $"{a_name} is open. Its settings are kept once you save and close it there.\n\n" +
				    $"If you stop waiting, {a_name} stays open, but nothing it saves is kept with this setup.", "Stop waiting", "Keep waiting")) {
				return false;
			}
			o.Stop();
			status.Text = o.Opened ? $"Stopped waiting for {a_name}." : $"Stopping - {a_name} is not opened ...";
			A11y.Say(w, status.Text);
			Buttons();
			return true;
		}

		async Task SetUp(Row a_r)
		{
			toolOpen = true;
			using var o = new ToolOpen();
			opening = o;
			openingName = a_r.Name;
			Buttons();
			a_r.State.Text = $"opening {a_r.Name} ...";
			status.Text = "";
			ToolOpen.Result result;
			// the main window stays until the tool is closed and its mod is switched off again (WorkInHand)
			using (a_owner.Working($"{a_r.Name} is open to set it up")) {
				result = await o.Go(a_setup, a_r.Step,
					why => Ask.Show(w, $"{a_r.Name} cannot open yet", $"{Capital(why)}.\n\n{a_r.Name} opens through Mod Organizer, and Mod Organizer is closed first - " +
					                    $"never while that is open. Close it and set the tool up again, or wait here: {a_r.Name} opens by itself once it has ended, and you can stop " +
					                    "waiting at any time.", "Wait for it", "Not now"),
					line => w.Dispatcher.BeginInvoke(() => {
						// (a line that arrives after the opening ended never covers how it ended)
						if (opening == o) {
							a_r.State.Text = o.Opened ? $"{a_r.Name} is open - set it up, save and close it ..." : line;
							Buttons();
						}
					}));
			}
			opening = null;
			a_r.State.Text = Outcome(a_r.Name, result) + StillOn(o.StillOn);
			var learned = result.Learned;
			if (learned != null) {
				a_r.SetUp = true;
				a_r.Open.Content = "Set up again";
				System.Windows.Automation.AutomationProperties.SetName(a_r.Open, $"Set up again: {a_r.Name}");
			}
			A11y.Say(w, $"{a_r.Name}: {a_r.State.Text}");
			toolOpen = false;
			if (result.How is ToolOpen.Ending.NotNow or ToolOpen.Ending.Stopped) {
				// a walk through the tools stops with the one the player did not wait for
				stopWalk = true;
			}
			Buttons();
			if (closeAfter) {
				walking = false;
				w.Close();
			}
		}

		stop.Click += (_, _) => StopWaiting(openingName);

		foreach (var r in rows) {
			r.Open.Click += async (_, _) => await SetUp(r);
		}
		turn.Click += async (_, _) => {
			if (walking) {
				stopWalk = true;
				turn.Content = "Stops after this one ...";
				Buttons();
				return;
			}
			var todo = rows.Where(r => !r.SetUp).ToList();
			if (todo.Count == 0) {
				status.Text = "Every tool is set up already. To change one, use its own button.";
				return;
			}
			walking = true;
			stopWalk = false;
			turn.Content = "_Stop after this one";
			foreach (var r in todo) {
				if (stopWalk) {
					break;
				}
				await SetUp(r);
			}
			walking = false;
			turn.Content = "Set up each in _turn";
			status.Text = stopWalk ? "Stopped. The tools left are as they were." : "That was the last one.";
			A11y.Say(w, status.Text);
			Buttons();
		};
		start.Click += (_, _) => {
			go = a_beforeFirstRun;
			w.Close();
		};
		notNow.Click += (_, _) => w.Close();
		// Always closes: a wait for a tool not yet open is stopped and the box goes as soon as it has; a tool already open is asked
		// about first (its settings are kept only once it has closed), and on the player's word the box stops waiting and goes
		w.Closing += (_, e) => {
			if (!toolOpen) {
				return;
			}
			e.Cancel = true;
			if (StopWaiting(openingName)) {
				stopWalk = true;
				closeAfter = true;
			}
		};

		var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
		bar.Children.Add(start);
		if (a_beforeFirstRun) {
			bar.Children.Add(notNow);
		}
		bar.Children.Add(turn);
		bar.Children.Add(stop);
		bottom.Children.Add(bar);
		if (InUse()) {
			status.Text = "A run is going: set its tools up when the run is done.";
		}
		Buttons();
		// (the scrolling region is what a screen reader meets as a pane of its own: it carries the name)
		var tools = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Focusable = false, Content = list };
		System.Windows.Automation.AutomationProperties.SetName(tools, "The setup's tools");
		dock.Children.Add(tools);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = dock };
		w.Loaded += (_, _) => A11y.Say(w, "Set up your tools. " + (rows.Count == 0 ? "No tool to set up." : $"{rows.Count} tool(s), {rows.Count(r => !r.SetUp)} not set up yet."), true);
		return w;
	}
}
