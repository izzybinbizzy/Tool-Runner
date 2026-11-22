using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// A queue of setups: tick the ones to run, put them in order; each runs whole (its own check, its own put-back) after the one
// before ends. A stopped run ends the queue; a failed one too, unless "keep going" is ticked.
public static class QueueDialog
{
	public sealed record Picked(List<string> Files, bool KeepGoing);

	sealed class Row
	{
		public required string File { get; init; }
		public required CheckBox Box { get; init; }
		// the list's row: a handle to drag it by, then its tick box
		public required DockPanel Item { get; init; }
	}

	static Row MakeRow(string a_file)
	{
		var box = new CheckBox { Content = Path.GetFileNameWithoutExtension(a_file), Foreground = Brushes2.Get("Text"), VerticalAlignment = VerticalAlignment.Center };
		var handle = new TextBlock { Text = "≡", Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center, Cursor = System.Windows.Input.Cursors.SizeNS,
			ToolTip = "Drag to move this setup in the queue", Foreground = Brushes2.Get("Muted") };
		var item = new DockPanel { Background = System.Windows.Media.Brushes.Transparent };
		DockPanel.SetDock(handle, Dock.Left);
		item.Children.Add(handle);
		item.Children.Add(box);
		System.Windows.Automation.AutomationProperties.SetName(item, (string)box.Content);
		return new Row { File = a_file, Box = box, Item = item };
	}

	// Where a row dragged from a_from lands when it is dropped on the row at a_over (on its lower half: a_below) - the same rule
	// as a step dragged in the main window
	public static int DropPlace(int a_from, int a_over, bool a_below) => MainWindow.DropPlace(a_from, a_over, a_below);

	public static Picked? Run(Window a_owner, List<string> a_setups)
	{
		Picked? picked = null;
		// its least size too is held to the screen: a least height above the screen's would win over the height it is given
		var room = SystemParameters.WorkArea;
		var w = new Window {
			Owner = a_owner, Title = "Queue of setups", Width = UiState.Fit(UiState.Number("queue.width"), 560, 420, room.Width),
			Height = UiState.Fit(UiState.Number("queue.height"), 560, 380, room.Height), MinWidth = UiState.Least(420, room.Width), MinHeight = UiState.Least(380, room.Height),
			WindowStartupLocation = WindowStartupLocation.CenterOwner,
			Background = Brushes2.Get("Panel"), Foreground = Brushes2.Get("Text"), ResizeMode = ResizeMode.CanResize, ShowInTaskbar = false,
		};
		w.Closed += (_, _) => {
			UiState.Set("queue.width", w.ActualWidth);
			UiState.Set("queue.height", w.ActualHeight);
		};
		// the list takes whatever room the box is given; what stands under it is docked to the bottom
		var dock = new DockPanel { Margin = new Thickness(22) };
		var stack = new StackPanel();
		DockPanel.SetDock(stack, Dock.Top);
		dock.Children.Add(stack);
		stack.Children.Add(new TextBlock { Text = "QUEUE OF SETUPS", Style = (Style)a_owner.FindResource("H1"), Margin = new Thickness(0, 0, 0, 6), FontSize = 18 });
		stack.Children.Add(new TextBlock { Text = "Tick the setups to run and put them in order. Each one runs whole - its check, its tools, your profile put back - before the next starts.",
			Style = (Style)a_owner.FindResource("MutedText"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

		var rows = a_setups.Select(MakeRow).ToList();
		var list = new ListBox { MinHeight = 120, AllowDrop = true };
		// a click on a tick box is the box's own, so its row would stay unselected: the row follows the box's focus
		foreach (var r in rows) {
			r.Box.GotKeyboardFocus += (_, _) => list.SelectedItem = r.Item;
		}
		System.Windows.Automation.AutomationProperties.SetName(list, "Setups in the order they run");
		void Fill(int a_select)
		{
			list.Items.Clear();
			foreach (var r in rows) {
				list.Items.Add(r.Item);
			}
			list.SelectedIndex = a_select;
		}
		Fill(rows.Count > 0 ? 0 : -1);

		// his checklist 635: a setup is dragged to its place in the queue (by its handle or the row around its tick box - a press on
		// the tick box itself ticks it)
		Row? pressed = null, dragging = null;
		Point pressAt = default;
		// a_press: a press on a button (the tick box) is the button's, never the start of a drag
		Row? RowAt(object a_source, bool a_press = false)
		{
			for (var d = a_source as DependencyObject; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) {
				if (a_press && d is System.Windows.Controls.Primitives.ButtonBase) {
					return null;
				}
				if (rows.FirstOrDefault(r => ReferenceEquals(r.Item, d)) is { } hit) {
					return hit;
				}
				// the padding round a row is its container's, not the row's own (CodeRabbit on pull request #38): a drop there is
				// that row's too
				if (d is ListBoxItem container && rows.FirstOrDefault(r => ReferenceEquals(r.Item, container.Content)) is { } around) {
					return around;
				}
			}
			return null;
		}
		list.PreviewMouseLeftButtonDown += (_, e) => {
			pressed = RowAt(e.OriginalSource, true);
			pressAt = e.GetPosition(list);
		};
		list.PreviewMouseLeftButtonUp += (_, _) => pressed = null;
		list.PreviewMouseMove += (_, e) => {
			if (pressed is not { } row || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) {
				return;
			}
			var now = e.GetPosition(list);
			if (Math.Abs(now.X - pressAt.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(now.Y - pressAt.Y) < SystemParameters.MinimumVerticalDragDistance) {
				return;
			}
			pressed = null;
			dragging = row;
			list.SelectedItem = row.Item;
			try {
				DragDrop.DoDragDrop(list, new DataObject("ToolRunner.QueueRow", row.File), DragDropEffects.Move);
			} finally {
				dragging = null;
			}
		};
		list.DragOver += (_, e) => {
			e.Effects = dragging != null && e.Data.GetDataPresent("ToolRunner.QueueRow") ? DragDropEffects.Move : DragDropEffects.None;
			e.Handled = true;
		};
		list.Drop += (_, e) => {
			e.Handled = true;
			if (dragging is not { } row || !e.Data.GetDataPresent("ToolRunner.QueueRow")) {
				return;
			}
			// the row under the pointer, and whether the pointer is on its lower half; under the last row = after the last
			var hitRow = RowAt(e.OriginalSource);
			var over = hitRow ?? rows[^1];
			var below = hitRow == null || e.GetPosition(hitRow.Item).Y > hitRow.Item.ActualHeight / 2;
			var from = rows.IndexOf(row);
			var to = DropPlace(from, rows.IndexOf(over), below);
			if (to == from) {
				return;
			}
			rows.RemoveAt(from);
			rows.Insert(to, row);
			Fill(to);
		};
		// from here on `stack` is the part under the list
		stack = new StackPanel();
		DockPanel.SetDock(stack, Dock.Bottom);
		dock.Children.Add(stack);
		dock.Children.Add(list);

		void Move(int a_by)
		{
			var i = list.SelectedIndex;
			var j = i + a_by;
			if (i < 0 || j < 0 || j >= rows.Count) {
				return;
			}
			(rows[i], rows[j]) = (rows[j], rows[i]);
			Fill(j);
			list.Focus();
		}
		var moves = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
		var up = new Button { Content = "Move _up", Style = (Style)a_owner.FindResource("Ghost"), Margin = new Thickness(0, 0, 8, 0) };
		var down = new Button { Content = "Move _down", Style = (Style)a_owner.FindResource("Ghost") };
		up.Click += (_, _) => Move(-1);
		down.Click += (_, _) => Move(1);
		moves.Children.Add(up);
		moves.Children.Add(down);
		stack.Children.Add(moves);

		var keep = new CheckBox { Content = "_Keep going when a setup's run fails (a stopped run always ends the queue)", Foreground = Brushes2.Get("Text"), Margin = new Thickness(0, 14, 0, 0) };
		stack.Children.Add(keep);
		var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), Foreground = Brushes2.Get("Bad") };
		stack.Children.Add(note);

		var start = new Button { Content = "_Start the queue", Style = (Style)a_owner.FindResource("Primary"), Margin = new Thickness(0, 0, 10, 0), IsDefault = true };
		var cancel = new Button { Content = "Cancel", Style = (Style)a_owner.FindResource("Ghost"), IsCancel = true };
		start.Click += (_, _) => {
			var files = rows.Where(r => r.Box.IsChecked == true).Select(r => r.File).ToList();
			if (files.Count < 2) {
				note.Text = "Tick at least two setups (one setup is a normal Start).";
				return;
			}
			picked = new Picked(files, keep.IsChecked == true);
			w.Close();
		};
		var bar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 0) };
		bar.Children.Add(start);
		bar.Children.Add(cancel);
		stack.Children.Add(bar);
		w.Content = dock;
		w.ShowDialog();
		return picked;
	}
}
