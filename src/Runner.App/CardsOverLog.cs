using System.Windows;
using System.Windows.Controls;

namespace ToolRunner.App;

// The window's right column: the cards, the log's heading, the log - its three children, top to bottom. The heading is as
// tall as it needs and never the part that gives way. The cards take what they need of the rest and no more than the rest
// (what they hold beyond it scrolls), and the log gets what they leave; with the log hidden the cards get all of the rest.
// A grid's rows cannot say this: a row "as tall as its content" takes that height even where the column does not have it,
// and so pushed the heading - or, with the log hidden, the run's own card with Start and Stop - out of the window
public sealed class CardsOverLog : Panel
{
	UIElement? At(int a_place) => a_place < InternalChildren.Count ? InternalChildren[a_place] : null;

	protected override Size MeasureOverride(Size a_room)
	{
		var (cards, head, log) = (At(0), At(1), At(2));
		head?.Measure(new Size(a_room.Width, double.PositiveInfinity));
		var headHigh = head?.DesiredSize.Height ?? 0;
		var rest = Math.Max(0, a_room.Height - headHigh);
		cards?.Measure(new Size(a_room.Width, rest));
		var cardsHigh = cards?.DesiredSize.Height ?? 0;
		log?.Measure(new Size(a_room.Width, Math.Max(0, rest - cardsHigh)));
		var wide = Math.Max(cards?.DesiredSize.Width ?? 0, Math.Max(head?.DesiredSize.Width ?? 0, log?.DesiredSize.Width ?? 0));
		var high = headHigh + cardsHigh + (log?.DesiredSize.Height ?? 0);
		return new Size(Math.Min(wide, a_room.Width), Math.Min(high, a_room.Height));
	}

	protected override Size ArrangeOverride(Size a_size)
	{
		var (cards, head, log) = (At(0), At(1), At(2));
		var headHigh = Math.Min(head?.DesiredSize.Height ?? 0, a_size.Height);
		var rest = a_size.Height - headHigh;
		// the log hidden: all the room that is left is the cards' (the run's card grows into it)
		var cardsHigh = log is { Visibility: Visibility.Visible } ? Math.Min(cards?.DesiredSize.Height ?? 0, rest) : rest;
		cards?.Arrange(new Rect(0, 0, a_size.Width, cardsHigh));
		head?.Arrange(new Rect(0, cardsHigh, a_size.Width, headHigh));
		log?.Arrange(new Rect(0, cardsHigh + headHigh, a_size.Width, rest - cardsHigh));
		return a_size;
	}
}
