using Microsoft.Extensions.DependencyInjection;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

// The Linux host's side of the accounts that move with the PC (SWITCHBOARD.md stage 6 slice 13): the runner the move host drives the providers through, and the one place a
// provider's login after a move is made, beside the start's own restore and under the same lock, so an account is never logged in twice at once.
internal sealed partial class LinuxHostStartup
{
    // Fallbacks for a composition without a move host; a composed server uses the move host's locks for every lifecycle entry point.
    private readonly SemaphoreSlim messengerLogin = new(1, 1), instagramLogin = new(1, 1);
    private Func<Task<TimeSpan>>? pacing;

    /// <summary>The accounts that moved here, or null on a host built without it (the harness).</summary>
    public AccountMoveHost? Move { get; private set; }

    internal void AttachMove(AccountMoveHost move, LinuxAccountRunner runner)
    {
        Move = move;
        runner.Restorer = RestoreMovedAsync;
        // The backends themselves ask the gate under their own locks, so not even their internal reconnects (the recovery after a helper stopped) can sign in an account that is staged or held.
        Services.GetRequiredService<MessengerBackend>().MayConnect = () => move.Allows(MoveProviders.Messenger);
        Services.GetRequiredService<InstagramBackend>().MayStart = () => move.Allows(MoveProviders.Instagram);
    }

    /// <summary>The server's restore ledger, asked before a moved account is logged in: at most four restores an hour, as the start's are.</summary>
    internal void UsePacing(Func<Task<TimeSpan>> permit) => pacing = permit;

    private SemaphoreSlim LoginGate(string provider) => Move?.LifecycleGate(provider) ?? (provider == RestoreOnce.Messenger ? messengerLogin : instagramLogin);

    /// <summary>
    /// A moved account's first login, once: Messenger with the backend's own tries (<c>RestoreAsync</c>), Instagram a single resume with no retry loop. Both go through the ledger; a
    /// restore it holds back is made when the hour allows, in the background, and the answer is <c>paced</c>.
    /// </summary>
    internal async Task<AccountStart> RestoreMovedAsync(string provider, CancellationToken cancellationToken)
    {
        await LoginGate(provider).WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Move is null || !Move.Allows(provider)) return new AccountStart("not_connected", "not_allowed");
            if (pacing is not null && await pacing().ConfigureAwait(false) is var wait && wait > TimeSpan.Zero)
            {
                log($"account move login held provider={provider} wait_s={(long)Math.Ceiling(wait.TotalSeconds)}");
                _ = Task.Run(async () =>
                {
                    try { await Task.Delay(wait).ConfigureAwait(false); await RestoreMovedAsync(provider, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception) { }
                });
                return new AccountStart("paced");
            }
            try
            {
                if (provider == MoveProviders.Messenger)
                {
                    var messenger = Services.GetRequiredService<IMessengerAccount>();
                    if (messenger.State.Connected) return new AccountStart("connected");
                    var restored = await messenger.RestoreAsync(() => cancellationToken.IsCancellationRequested).ConfigureAwait(false);
                    return restored.Conversations is not null ? new AccountStart("connected")
                        : new AccountStart("not_connected", restored.Refusal is { } refusal && ProtocolDispatcher.IsCodeWord(refusal) ? refusal : "login_failed");
                }
                var instagram = Services.GetRequiredService<IInstagramAccount>();
                if (instagram.State.Connected) return new AccountStart("connected");
                // The limits that came with the account (its hour of background reads), read before it starts.
                Services.GetRequiredService<InstagramBackend>().ReloadGuard();
                // One try: an account the service questions is not asked again by a loop.
                var joined = await instagram.ResumeAsync(cancellationToken).ConfigureAwait(false);
                return joined >= 0 ? new AccountStart("connected") : new AccountStart("not_connected", "saved_session_unreadable");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception failure)
            {
                log($"account move login failed provider={provider} error={failure.GetType().Name}");
                return new AccountStart("not_connected", failure is InvalidOperationException && ProtocolDispatcher.IsCodeWord(failure.Message) ? failure.Message : "login_failed");
            }
        }
        finally { LoginGate(provider).Release(); }
    }
}

/// <summary>The move host's way to the running providers: what is connected, how a helper is closed, and the first login.</summary>
internal sealed class LinuxAccountRunner(IServiceProvider services) : IAccountRunner
{
    /// <summary>Set once the host exists: <see cref="LinuxHostStartup.RestoreMovedAsync"/>.</summary>
    public Func<string, CancellationToken, Task<AccountStart>>? Restorer { get; set; }

    public bool EncryptedReady(string provider) => provider != MoveProviders.Messenger || services.GetRequiredService<IMessengerAccount>().State.Encrypted == MessengerEncryption.Ready;

    public bool Connected(string provider) => provider == MoveProviders.Messenger
        ? services.GetRequiredService<IMessengerAccount>().State.Connected
        : services.GetRequiredService<IInstagramAccount>().State.Connected;

    public Task StopAsync(string provider, CancellationToken cancellationToken) => provider == MoveProviders.Messenger
        ? services.GetRequiredService<MessengerBackend>().StopForMoveAsync(cancellationToken)
        : services.GetRequiredService<InstagramBackend>().StopForMoveAsync(cancellationToken);

    public Task<AccountStart> StartAsync(string provider, CancellationToken cancellationToken) =>
        Restorer is { } restore ? restore(provider, cancellationToken) : Task.FromResult(new AccountStart("not_connected", "not_ready"));
}
