namespace NativeBeeper.Server;

// What the backends ask of their host that the server cannot give yet. Each is conservative (it says no, never guesses), and the composition
// logs `server port missing name=<word>` once for each, so a log shows what a headless run lacks. Delete each as the real one arrives.
// (The Google renewal's browser and sign-in host have arrived: WebViewGoogleBrowser over the shell's STA thread, and ServerSignInHost.)

/// <summary>
/// Holds a feed back until the host is allowed to connect. The event log runs its feed the moment a client subscribes, and Google's feed
/// connects the saved pairing by itself, so without <c>--restore</c> a client attached to a server on a real data root would have started the
/// very connection the flag keeps off. Opened by the restore, after it has started the other providers (SWITCHBOARD.md 1.7d).
/// </summary>
internal sealed class GatedRealtimeFeed(NativeBeeper.Core.Services.IRealtimeFeed inner) : NativeBeeper.Core.Services.IRealtimeFeed
{
    private readonly TaskCompletionSource open = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Open() => open.TrySetResult();

    public async Task RunAsync(Func<NativeBeeper.Core.Services.RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken)
    {
        await open.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await inner.RunAsync(receive, cancellationToken).ConfigureAwait(false);
    }
}
