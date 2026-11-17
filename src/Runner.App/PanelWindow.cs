using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ToolRunner.App;

// One of the window's panels in a window of its own: History, Settings, Themes, the log. It belongs to the main window (it
// stays above it and closes with it), can be sized and moved, and the main window stays usable beside it - a run can be
// watched with History or the log open on a second screen. Its size is remembered for the next time.
public sealed class PanelWindow : Window
{
	readonly string _key;
	readonly ContentControl _body = new() { Focusable = false };
	readonly TextBlock _head;

	public const double Least = 420;

	// a_key: the name its size is kept under. a_title: its heading, in capitals as the window's other headings
	public PanelWindow(Window a_owner, string a_key, string a_title, double a_width, double a_height)
	{
		_key = a_key;
		Owner = a_owner;
		ShowInTaskbar = false;
		ResizeMode = ResizeMode.CanResize;
		var room = SystemParameters.WorkArea;
		MinWidth = Math.Min(Least, room.Width);
		MinHeight = Math.Min(320, room.Height);
		Width = UiState.Fit(UiState.Number(a_key + ".width"), a_width, MinWidth, room.Width);
		Height = UiState.Fit(UiState.Number(a_key + ".height"), a_height, MinHeight, room.Height);
		WindowStartupLocation = WindowStartupLocation.CenterOwner;
		// the theme's own values as live references: a theme picked while it is open repaints it too
		SetResourceReference(BackgroundProperty, "Panel");
		SetResourceReference(ForegroundProperty, "Text");
		SetResourceReference(FontFamilyProperty, "UiFont");
		SetResourceReference(FontSizeProperty, "SizeBody");
		if (a_owner.Icon is { } icon) {
			Icon = icon;
		}
		// the off-screen pictures: drawn where nobody sees it, never in front
		if (App.Headless) {
			ShowActivated = false;
			WindowStartupLocation = WindowStartupLocation.Manual;
			Left = -30000;
			Top = -30000;
		}
		_head = new TextBlock { Style = (Style)a_owner.FindResource("H1") };
		var close = new Button { Content = "Close", Style = (Style)a_owner.FindResource("Ghost"), ToolTip = "Esc" };
		close.Click += (_, _) => Close();
		var top = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
		DockPanel.SetDock(close, Dock.Right);
		top.Children.Add(close);
		top.Children.Add(_head);
		var dock = new DockPanel { Margin = new Thickness(20) };
		DockPanel.SetDock(top, Dock.Top);
		dock.Children.Add(top);
		dock.Children.Add(_body);
		var root = new Border { Child = dock };
		root.SetResourceReference(Border.BackgroundProperty, "Panel");
		Content = root;
		Head(a_title);
		PreviewKeyDown += (_, e) => {
			if (e.Key == Key.Escape) {
				e.Handled = true;
				Close();
			}
		};
		Closed += (_, _) => {
			if (WindowState == WindowState.Normal) {
				UiState.Set(_key + ".width", ActualWidth);
				UiState.Set(_key + ".height", ActualHeight);
			}
		};
	}

	public FrameworkElement? Body
	{
		get => _body.Content as FrameworkElement;
		set => _body.Content = value;
	}

	public void Head(string a_title)
	{
		_head.Text = a_title;
		// the title bar in plain case: "HISTORY" -> "History"
		Title = a_title.Length > 1 ? a_title[..1] + a_title[1..].ToLowerInvariant().Replace("mo2", "MO2", StringComparison.Ordinal) : a_title;
		System.Windows.Automation.AutomationProperties.SetName(this, Title);
	}

	// the keyboard into the panel: what a_first focuses, else the first thing in its own content, else its Close button
	public void TakeKeyboard(Action? a_first = null)
	{
		Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => {
			a_first?.Invoke();
			if (!IsKeyboardFocusWithin && !_body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))) {
				MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
			}
		});
	}

	// the off-screen pictures only: the panel's own scroller at a_y (never the one inside a text box)
	public void ScrollTo(double a_y)
	{
		static ScrollViewer? Find(DependencyObject a_d)
		{
			for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(a_d); ++i) {
				var c = System.Windows.Media.VisualTreeHelper.GetChild(a_d, i);
				if (c is System.Windows.Controls.Primitives.TextBoxBase or ListBox) {
					continue;
				}
				if ((c as ScrollViewer ?? Find(c)) is { } s) {
					return s;
				}
			}
			return null;
		}
		Find(_body)?.ScrollToVerticalOffset(a_y);
	}
}
