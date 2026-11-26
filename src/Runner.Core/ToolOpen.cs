namespace ToolRunner;

// One tool's window opened through Mod Organizer to learn its settings - the way the tools box and the settings panel both do
// it. Mod Organizer is closed before the tool opens and is never closed under a game, a download, a repack or a program of
// the player's: left to itself that wait can go on for half an hour with nothing said and nothing to stop it. So what
// holds it is asked about first (wait for it, or not now), what the wait is
// on is told as it goes, and the wait can always be stopped - before the tool is up at once, after it only on the player's word,
// since what the tool saves is kept only once it has closed.
public sealed class ToolOpen : IDisposable
{
	// the offline tests answer for Mod Organizer and for the tool
	public static Func<Setup, CancellationToken, string?> BusyLook { get; set; } = ToolSettings.WhyNotNow;
	public delegate ToolSettings.Learned LearnCall(Setup a_setup, string a_step, RunLog a_log, CancellationToken a_ct, Action<string> a_stillOn, Action a_opened);
	public static LearnCall LearnWith { get; set; } = (s, step, log, ct, still, opened) => ToolSettings.Learn(s, step, log, ct, still, opened);

	public enum Ending { Learned, NotNow, Stopped, Failed }

	// how it ended: the settings kept, the player chose not to wait, the player stopped the wait, or why it failed
	public sealed record Result(Ending How, ToolSettings.Learned? Learned, string? Why);

	readonly CancellationTokenSource _stop = new();
	// the tool's opening and a stop decided on it are one step each, never one between the other's look and act
	readonly Lock _gate = new();
	volatile bool _opened;
	volatile string? _stillOn;

	// the tool's own window is up (from then on, stopping leaves it open and keeps nothing of it)
	public bool Opened => _opened;

	public bool Stopping => _stop.IsCancellationRequested;

	// the mod switched on to open the tool, when it could not be switched off again (null: none, or it went off)
	public string? StillOn => _stillOn;

	// the wait given up; Go returns Stopped as soon as the step it is in looks
	public void Stop()
	{
		try {
			_stop.Cancel();
		} catch (ObjectDisposedException) {
		}
	}

	// the wait stopped at once while the tool is not open yet (true); false: it is open, so the player is asked before Stop
	public bool StopIfNotOpen()
	{
		lock (_gate) {
			if (_opened) {
				return false;
			}
			Stop();
			return true;
		}
	}

	void MarkOpened()
	{
		lock (_gate) {
			_opened = true;
		}
	}

	// a_askWait: told what holds Mod Organizer open; true = wait for it here. a_heard: each line the opening writes (what it waits
	// on, that the tool is open), from the thread it runs on
	public async Task<Result> Go(Setup a_setup, string a_step, Func<string, bool> a_askWait, Action<string> a_heard)
	{
		string? busy;
		try {
			busy = await Task.Run(() => BusyLook(a_setup, _stop.Token)).ConfigureAwait(true);
		} catch (OperationCanceledException) when (_stop.IsCancellationRequested) {
			return new Result(Ending.Stopped, null, "stopped waiting - it was not opened");
		} catch (Exception e) when (e is not OutOfMemoryException) {
			return new Result(Ending.Failed, null, e.Message);
		}
		// stopped while it looked (the box closed): nothing is asked and nothing opened
		if (_stop.IsCancellationRequested) {
			return new Result(Ending.Stopped, null, "stopped waiting - it was not opened");
		}
		if (busy != null && !a_askWait(busy)) {
			return new Result(Ending.NotNow, null, busy);
		}
		using var log = RunLog.Listening((text, level) => {
			if (level != Level.Detail) {
				a_heard(text);
			}
		});
		try {
			var learned = await Task.Run(() => LearnWith(a_setup, a_step, log, _stop.Token, m => _stillOn = m, MarkOpened), CancellationToken.None).ConfigureAwait(true);
			return new Result(Ending.Learned, learned, null);
		} catch (OperationCanceledException) when (_stop.IsCancellationRequested) {
			return new Result(Ending.Stopped, null, _opened ? "stopped waiting for it - its window stays open, and nothing it saves is kept with this setup" : "stopped waiting - it was not opened");
		} catch (Exception e) when (e is not OutOfMemoryException) {
			return new Result(Ending.Failed, null, e.Message);
		}
	}

	public void Dispose() => _stop.Dispose();
}
