namespace NativeBeeper.Presentation.Services;

/// <summary>
/// The timer Send later and message reminders share. It wakes a pass at the earliest moment something is due, and never
/// later than <see cref="Longest"/> after it was last set, so a changed clock or a PC that slept is noticed within that
/// time rather than at a moment worked out before it.
/// </summary>
/// <remarks>
/// Only a timer: it keeps nothing and decides nothing. A moment that passed while nothing ran (the app closed, the PC
/// asleep) is simply due at the next pass, and the pass decides what that means — a reminder is still delivered, a
/// scheduled message long overdue is held rather than sent. Passes never overlap, and run on the UI context when a
/// dispatcher is given, because the outbox they hand sends to is owned there. Times come from the clock it is given, so
/// the offline checks move time by hand instead of waiting for it.
/// </remarks>
public sealed class LocalAlarm(TimeProvider clock, Func<Task> pass, IUiDispatcher? dispatcher = null) : IDisposable
{
    public static readonly TimeSpan Longest = TimeSpan.FromMinutes(1);
    private readonly Lock gate = new();
    private ITimer? timer;
    private Task running = Task.CompletedTask;
    private bool again, stopped;

    /// <summary>The latest pass, finished or not. The offline checks await it after moving the clock.</summary>
    public Task Idle { get { lock (gate) return running; } }

    /// <summary>Wakes at <paramref name="next"/>, or after <see cref="Longest"/> when that is sooner or nothing is due.</summary>
    public void WakeAt(DateTimeOffset? next)
    {
        var wait = next is { } at ? at - clock.GetUtcNow() : Longest;
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        if (wait > Longest) wait = Longest;
        lock (gate)
        {
            if (stopped) return;
            timer ??= clock.CreateTimer(_ => RunAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            timer.Change(wait, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Runs a pass now. One already running runs once more when it ends instead of a second alongside it.</summary>
    public Task RunAsync()
    {
        lock (gate)
        {
            if (stopped) return running;
            if (!running.IsCompleted) { again = true; return running; }
            running = LoopAsync();
            return running;
        }
    }

    private async Task LoopAsync()
    {
        // Off the caller's stack: a timer callback, or whoever asked, returns at once.
        await Task.Yield();
        while (true)
        {
            lock (gate) { again = false; if (stopped) return; }
            try { await (dispatcher is null ? pass() : dispatcher.InvokeAsync(pass)); }
            // A failed pass must not end the schedule; the next wake tries again.
            catch (Exception) { WakeAt(null); }
            lock (gate) if (!again || stopped) return;
        }
    }

    /// <summary>No pass starts after this. One already running finishes.</summary>
    public void Stop()
    {
        lock (gate) { stopped = true; timer?.Dispose(); timer = null; }
    }

    public void Dispose() => Stop();
}
