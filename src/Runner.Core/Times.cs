namespace ToolRunner;

public static class Times
{
	// The time between two clock readings. Two local times taken apart are an hour out across a daylight-saving change (a
	// night run through the hour the clock repeats or skips); the same two instants in UTC are right
	public static TimeSpan Between(DateTime a_from, DateTime a_to) => a_to.ToUniversalTime() - a_from.ToUniversalTime();

	// whole minutes, never below nothing: 59.6 minutes are 60
	public static long WholeMinutes(TimeSpan a_t) => (long)Math.Round(Math.Max(0, a_t.TotalMinutes), MidpointRounding.AwayFromZero);

	// "45 min" / "1 h 05 min", from whole minutes: 59.6 minutes read "1 h 00 min", never "60 min"
	public static string HoursAndMinutes(TimeSpan a_t)
	{
		var minutes = Math.Max(1, WholeMinutes(a_t));
		return minutes < 60 ? $"{minutes} min" : $"{minutes / 60} h {minutes % 60:00} min";
	}
}
