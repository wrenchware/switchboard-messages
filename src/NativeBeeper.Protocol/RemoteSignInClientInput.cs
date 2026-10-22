namespace NativeBeeper.Protocol;

// The client's half of a sign-in in the server's browser (SWITCHBOARD.md 5.4): what the window does with the person's pointer and keys before they cross. Plain code, so
// that it can be checked without a window. Like the host's end it keeps nothing: the person may be typing a Google password, so nothing here is logged, written or put in an
// exception, and the types print only their names.

/// <summary>Where the picture sits in the window and how a point in the window is a point in the page.</summary>
public static class RemoteSignInGeometry
{
    /// <summary>
    /// The largest rectangle with the page's proportions that fits a <paramref name="boxWidth"/> by <paramref name="boxHeight"/> area, centred in it. Zero-sized when either is not positive.
    /// </summary>
    public static (double X, double Y, double Width, double Height) Fit(double boxWidth, double boxHeight, int viewWidth, int viewHeight)
    {
        if (!(boxWidth > 0) || !(boxHeight > 0) || viewWidth <= 0 || viewHeight <= 0) return (0, 0, 0, 0);
        var scale = Math.Min(boxWidth / viewWidth, boxHeight / viewHeight);
        var width = viewWidth * scale;
        var height = viewHeight * scale;
        return ((boxWidth - width) / 2, (boxHeight - height) / 2, width, height);
    }

    /// <summary>
    /// A point on the shown picture (<paramref name="shownWidth"/> by <paramref name="shownHeight"/>, measured from its top left corner) as a point in the page's own pixels. Null when the
    /// picture has no size or the point is not a number; a point just outside is held to the edge, so a drag that leaves the picture still ends at it.
    /// </summary>
    public static (double X, double Y)? ToPage(double x, double y, double shownWidth, double shownHeight, int viewWidth, int viewHeight)
    {
        if (!(shownWidth > 0) || !(shownHeight > 0) || viewWidth <= 0 || viewHeight <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        return (Math.Clamp(x / shownWidth, 0, 1) * viewWidth, Math.Clamp(y / shownHeight, 0, 1) * viewHeight);
    }

    /// <summary>
    /// A point in the window's picture area (<paramref name="areaX"/>, <paramref name="areaY"/>, in the area's own units from its top left corner, whatever the display scale: the area's size is in
    /// the same units) as a point in the page's pixels: where <see cref="Fit"/> puts the picture, then <see cref="ToPage"/>. The window asks this of the area it laid the picture out in, not of
    /// a second element's size that could lag behind it, so the click and the picture can never be placed by two different numbers (the mouse that did nothing, 2026-10-10).
    /// </summary>
    public static (double X, double Y)? AreaToPage(double areaX, double areaY, double areaWidth, double areaHeight, int viewWidth, int viewHeight)
    {
        var (x, y, width, height) = Fit(areaWidth, areaHeight, viewWidth, viewHeight);
        return width > 0 && height > 0 ? ToPage(areaX - x, areaY - y, width, height, viewWidth, viewHeight) : null;
    }

    /// <summary>The pixel of a page <paramref name="size"/> pixels across that a position falls in: positions are the left or top edge of a pixel, so 0.9 and 0.0 are pixel 0, and the far edge (size) is the last pixel.</summary>
    public static int ToPixel(double position, int size) => size <= 0 ? 0 : (int)Math.Clamp(Math.Floor(position), 0, size - 1);
}

/// <summary>
/// What the person did, waiting to be sent. A pointer move replaces the move before it (only where the pointer is now matters), a run of typed text becomes one text event, and
/// everything else keeps its order; so a burst of events is one call, and what is sent is what happened, in order. Bounded: past <see cref="Capacity"/> the oldest pointer moves go
/// first, then the oldest events, so a slow connection never grows without limit.
/// </summary>
public sealed class RemoteSignInInputQueue
{
    public const int Capacity = 512;

    private readonly object gate = new();
    private readonly LinkedList<RemoteSignInInput> events = new();

    public override string ToString() => nameof(RemoteSignInInputQueue);

    public int Count { get { lock (gate) return events.Count; } }

    public void Add(RemoteSignInInput input)
    {
        lock (gate)
        {
            var last = events.Last?.Value;
            if (last is { Kind: RemoteSignIn.Mouse, Type: "mouseMoved" } && input is { Kind: RemoteSignIn.Mouse, Type: "mouseMoved" } && last.Buttons == input.Buttons && last.Button == input.Button)
                events.Last!.Value = input;
            else if (last is { Kind: RemoteSignIn.Text } && input is { Kind: RemoteSignIn.Text } && (last.Text?.Length ?? 0) + (input.Text?.Length ?? 0) <= RemoteSignIn.MaxTextCharacters)
                events.Last!.Value = last with { Text = last.Text + input.Text };
            else events.AddLast(input);
            while (events.Count > Capacity)
            {
                var move = events.First;
                while (move is not null && move.Value is not { Kind: RemoteSignIn.Mouse, Type: "mouseMoved" }) move = move.Next;
                events.Remove(move ?? events.First!);
            }
        }
    }

    /// <summary>Up to <paramref name="count"/> events, oldest first, removed from the queue.</summary>
    public IReadOnlyList<RemoteSignInInput> Take(int count = RemoteSignIn.MaxEvents)
    {
        lock (gate)
        {
            var taken = new List<RemoteSignInInput>(Math.Min(count, events.Count));
            while (taken.Count < count && events.First is { } first)
            {
                taken.Add(first.Value);
                events.RemoveFirst();
            }
            return taken;
        }
    }
}

/// <summary>
/// Sends the queue, one call at a time and in order, until it is disposed. A call that fails (the sign-in is over, the connection dropped) is not retried and not reported: what
/// the person did a moment ago has no meaning to a sign-in that has ended, and the sign-in's own call is what reports how it ended.
/// </summary>
public sealed class RemoteSignInInputPump : IDisposable
{
    private readonly RemoteSignInInputQueue queue = new();
    private readonly Func<IReadOnlyList<RemoteSignInInput>, CancellationToken, Task> send;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0);
    private readonly Task loop;

    public RemoteSignInInputPump(Func<IReadOnlyList<RemoteSignInInput>, CancellationToken, Task> send)
    {
        this.send = send;
        loop = Task.Run(RunAsync);
    }

    public override string ToString() => nameof(RemoteSignInInputPump);

    public void Post(RemoteSignInInput input)
    {
        if (stop.IsCancellationRequested) return;
        queue.Add(input);
        try { wake.Release(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                await wake.WaitAsync(stop.Token).ConfigureAwait(false);
                while (queue.Take() is { Count: > 0 } batch)
                {
                    try { await send(batch, stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
                    catch (Exception) { /* Dropped; see the type's summary. */ }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        stop.Cancel();
        try { loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
