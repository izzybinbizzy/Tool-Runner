using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ToolRunner.App;

// Ctrl+K (his checklist 635): type a few letters, pick what to do - any button of the window, a setup to show, a step's settings,
// a run of one step, or a search in the settings for the words typed. Enter does the picked one, Escape closes the box.
public static class CommandBox
{
	public sealed record Item(string Label, Action Do)
	{
		public override string ToString() => Label;
	}

	// The labels that hold every word typed, the ones that start with what was typed first, each group in the order given
	public static List<T> Match<T>(IReadOnlyList<T> a_items, Func<T, string> a_label, string a_q)
	{
		var words = a_q.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
		if (words.Length == 0) {
			return [.. a_items];
		}
		var hits = a_items.Where(i => words.All(w => a_label(i).Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
		return [.. hits.Where(i => a_label(i).StartsWith(a_q.Trim(), StringComparison.OrdinalIgnoreCase)), .. hits.Where(i => !a_label(i).StartsWith(a_q.Trim(), StringComparison.OrdinalIgnoreCase))];
	}

	// a_find: what "find a setting" does with the words typed
	public static void Run(Window a_owner, IReadOnlyList<Item> a_items, Action<string> a_find) => Make(a_owner, a_items, a_find, out _).ShowDialog();

	// the box itself (the off-screen pictures draw it without waiting on it); a_type: types into its search box
	public static Window Make(Window a_owner, IReadOnlyList<Item> a_items, Action<string> a_find, out Action<string> a_type)
	{
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = "Do something", Width = Math.Min(560, room.Width), Height = Math.Min(460, room.Height), WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
			MinWidth = UiState.Least(360, room.Width), MinHeight = UiState.Least(240, room.Height),
			FontFamily = (System.Windows.Media.FontFamily)a_owner.FindResource("UiFont"), FontSize = (double)a_owner.FindResource("SizeBody"),
		};
		var box = new TextBox { Margin = new Thickness(0, 0, 0, 6), VerticalContentAlignment = VerticalAlignment.Center };
		System.Windows.Automation.AutomationProperties.SetName(box, "Type what to do");
		var hint = new TextBlock { Text = "Type a few letters - a button, a setup, a step or a setting. Enter does it, Escape closes.", Style = (Style)a_owner.FindResource("MutedText"),
			TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };
		var list = new ListBox();
		list.SetResourceReference(Control.BackgroundProperty, "Panel2");
		list.SetResourceReference(Control.BorderBrushProperty, "Line");
		ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
		System.Windows.Automation.AutomationProperties.SetName(list, "What can be done");
		Item? chosen = null;
		void Fill()
		{
			var q = box.Text.Trim();
			var shown = Match(a_items, i => i.Label, q);
			if (q != "") {
				shown.Add(new Item(WindowWords.Format("Find in the settings: \"{0}\"", q), () => a_find(q)));
			}
			list.ItemsSource = shown;
			list.SelectedIndex = shown.Count > 0 ? 0 : -1;
		}
		void Choose()
		{
			if (list.SelectedItem is Item i) {
				chosen = i;
				w.Close();
			}
		}
		box.TextChanged += (_, _) => Fill();
		box.PreviewKeyDown += (_, e) => {
			var count = list.Items.Count;
			switch (e.Key) {
			case Key.Down when count > 0:
				list.SelectedIndex = (list.SelectedIndex + 1) % count;
				list.ScrollIntoView(list.SelectedItem);
				e.Handled = true;
				break;
			case Key.Up when count > 0:
				list.SelectedIndex = (list.SelectedIndex - 1 + count) % count;
				list.ScrollIntoView(list.SelectedItem);
				e.Handled = true;
				break;
			case Key.Enter:
				Choose();
				e.Handled = true;
				break;
			}
		};
		list.MouseDoubleClick += (_, _) => Choose();
		list.KeyDown += (_, e) => {
			if (e.Key == Key.Enter) {
				Choose();
				e.Handled = true;
			}
		};
		w.PreviewKeyDown += (_, e) => {
			if (e.Key == Key.Escape) {
				w.Close();
				e.Handled = true;
			}
		};
		var dock = new DockPanel { Margin = new Thickness(18) };
		DockPanel.SetDock(box, Dock.Top);
		DockPanel.SetDock(hint, Dock.Top);
		dock.Children.Add(box);
		dock.Children.Add(hint);
		dock.Children.Add(list);
		w.Content = new Border { Background = Brushes2.Get("Panel"), Child = dock };
		w.Loaded += (_, _) => box.Focus();
		// what was picked is done once the box has gone (a box it opens is then not stacked over this one)
		w.Closed += (_, _) => chosen?.Do();
		Fill();
		a_type = t => box.Text = t;
		return w;
	}
}
