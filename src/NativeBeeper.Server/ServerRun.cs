using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App.Services;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server;

/// <summary>One run of the server: the composition it starts once the window is pumping, and the stop every path ends in (a quit, Restart Manager, the end of the session).</summary>
internal sealed class ServerRun(ServerOptions options) : IServerLifecycle
{
    private HostStartup? startup;
    private ServerPipe? pipe;
    private OwnerWatch? owner;
    private Task starting = Task.CompletedTask;
    private readonly CancellationTokenSource stop = new();
    private int exitCode;

    /// <summary>How long a server whose owner has gone waits for a client to come back before it stops. Set before <see cref="Start"/>.</summary>
    public TimeSpan OwnerGrace { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Non-zero when the run ended because it could not start (the pipe was taken); the process returns it.</summary>
    public int ExitCode => Volatile.Read(ref exitCode);

    /// <summary>Composes the host off the shell's thread, so the window answers a quit while a slow start is under way.</summary>
    public void Start(ServerShell shell)
    {
        // Before the composition, so an owner that is already gone is found at once; the grace is what waits for a client.
        if (options.OwnerPid is { } ownerPid)
        {
            owner = new OwnerWatch(OwnerWatch.WatchProcess(ownerPid, AppLog.Write), () => pipe?.Connections ?? 0, shell.Stop, AppLog.Write) { Grace = OwnerGrace };
            owner.Start();
        }
        var queued = new StepClock(AppLog.Write);
        starting = Task.Run(async () =>
        {
            var clock = Stopwatch.StartNew();
            try
            {
                queued.Took("start");
                var composed = HostStartup.Compose(AppLog.Write, shell.Sta ?? throw new InvalidOperationException("no_sta"), options.Restore);
                Volatile.Write(ref startup, composed);
                AppLog.Write($"server ready ms={clock.ElapsedMilliseconds}");
                // A stop that came while composing opens nothing.
                if (stop.IsCancellationRequested) return;
                if (!await ServeAsync(composed, shell)) return;
                // A stop that came while composing starts nothing: only a restore connects the providers, and none begins once stopping has.
                if (options.Restore && !stop.IsCancellationRequested) await composed.RestoreAsync(stop.Token);
                // A restore that will not run: a client that asked for one while the door was open is answered, not left waiting.
                else composed.EndRestores();
            }
            catch (Exception failure)
            {
                // The type alone: an exception's message can carry what was being opened.
                AppLog.Write($"server start failed type={failure.GetType().Name}");
                shell.Stop("start_failed");
            }
        });
    }

    // The clients' door, open before the providers connect so a client sees the state as the restore changes it. False when the pipe's name was
    // taken, which has stopped the server.
    private async Task<bool> ServeAsync(HostStartup composed, ServerShell shell)
    {
        var step = new StepClock(AppLog.Write);
        var shutdown = new ServerShutdown(options.OwnerPid is not null, RefuseQuit, shell.Stop, AppLog.Write);
        var token = new ClientToken(AppPaths.Combine("Server"), composed.Services.GetRequiredService<IStateProtector>());
        step.Took("token");
        // The stop is the pipe's own question too: a connection accepted after the stop began is never served (it is told the server is closing).
        var served = new ServerPipe(PipeEndpoint.PipeNameForCurrentUser(AppPaths.InstanceSuffix), token, composed.Host.Services with { Shutdown = shutdown },
            composed.Host.Journal, composed.Host.Version, AppLog.Write, () => stop.IsCancellationRequested);
        served.ConnectionsChanged += () => owner?.ConnectionsChanged();
        var opened = await served.StartAsync();
        step.Took("pipe");
        if (!opened)
        {
            Volatile.Write(ref exitCode, 3);
            shell.Stop("pipe_taken");
            return false;
        }
        Volatile.Write(ref pipe, served);
        // A stop that began while the door was opening: the stop looked for the pipe before it was there, so it is told now.
        if (stop.IsCancellationRequested) served.BeginDraining();
        return true;
    }

    // Mutations the providers have made and not yet heard back from, and requests the host has begun and not yet answered (a send, a sign-in, a
    // restore a client asked for): a quit now would leave their outcome unknown. The journal's count is what the host itself is waiting on; the
    // helpers' is what has reached a provider.
    public string? RefuseQuit() => HelperConnection.MutationsInFlight > 0 || Volatile.Read(ref startup)?.Host.Journal.RunningCount > 0 ? "busy" : null;

    public void SessionQuery()
    {
        AppLog.Write("session end query");
        RunMarker.MarkSessionEnding(RunMarkerPath);
    }

    public void SessionCancel()
    {
        RunMarker.MarkRunning(RunMarkerPath);
        AppLog.Write("session end cancelled");
    }

    public async Task StopAsync(string reason, ShutdownBudget budget)
    {
        var clock = Stopwatch.StartNew();
        AppLog.Write($"server stopping reason={reason}");
        // Before anything is waited for: a restore under way starts no more helpers and ends at its next check, so the wait below is short and
        // the shutdown never races a provider that is still starting. The providers are closed after the restore, not under it.
        stop.Cancel();
        // The very first act: from here a request on any connection is refused (unavailable, before the journal sees it), so nothing new starts while the
        // restore is waited for below. The ones already accepted go on to their answers. A connection accepted after this is told the same.
        Volatile.Read(ref pipe)?.BeginDraining();
        HelperConnection.RefuseStarts = true;
        owner?.Dispose();
        try { await starting.WaitAsync(budget.Sends); }
        catch (Exception failure) when (failure is TimeoutException or OperationCanceledException) { AppLog.Write("server stop restore_still_running"); }
        catch (Exception) { }
        // After the start has ended, so a pipe still being opened is not missed. No new client; then the ones connected are given the time sends get
        // to have the answers to what they already asked written (the request that began this stop is one of them); and only then are the providers
        // closed, so nothing a client sent is cut off by the providers going first.
        if (Volatile.Read(ref pipe) is { } served)
        {
            try { await served.StopAsync(budget.Sends); }
            catch (Exception failure) { AppLog.Write($"server stop pipe_error={failure.GetType().Name}"); }
        }
        if (Volatile.Read(ref startup) is { } composed)
        {
            try
            {
                composed.Host.StopPublishing();
                await BackendShutdown.StopAsync(composed.Services, budget);
            }
            catch (Exception failure) { AppLog.Write($"server stop error={failure.GetType().Name}"); }
            try { composed.Dispose(); } catch (Exception) { }
        }
        RunMarker.End(RunMarkerPath);
        AppLog.Write($"server stopped ms={clock.ElapsedMilliseconds}");
        Program.FlushLogs();
    }

    private static string RunMarkerPath => AppPaths.Combine("server-run.marker");
}
