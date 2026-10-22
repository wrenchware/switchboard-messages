using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

/// <summary>
/// The Messenger account port with the account move's gate in front (SWITCHBOARD.md stage 6 slice 13): while an account is being staged or has moved away, nothing may sign it in,
/// restore it, connect it or give it encryption, whoever asks (an old client, a new one, the window of a PC that moved). The refusal is the fixed word
/// <see cref="AccountFailureWords.AccountMovedAway"/>, thrown before the backend is reached, so no helper starts and nothing goes to the service. Sign-out is refused too: a
/// held account's files are what a move back takes home, and a sign-out would delete the only copy. Reads and the state pass through.
/// </summary>
internal sealed class GatedMessengerAccount(IMessengerAccount inner, IAccountGate gate) : IMessengerAccount
{
    private void Require()
    {
        if (!gate.Allows(MoveProviders.Messenger)) throw new InvalidOperationException(AccountFailureWords.AccountMovedAway);
    }

    /// <summary>The port behind the gate (the harness looks at what is really served).</summary>
    internal IMessengerAccount Inner => inner;

    public MessengerAccountState State => inner.State;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public Task<bool> HasUsableSavedSessionAsync(CancellationToken cancellationToken = default) => inner.HasUsableSavedSessionAsync(cancellationToken);
    private async Task<T> UnderLock<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        var lifecycle = gate.LifecycleGate(MoveProviders.Messenger);
        if (lifecycle is not null) await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await call().ConfigureAwait(false); }
        finally { lifecycle?.Release(); }
    }

    private Task UnderLock(Func<Task> call) => UnderLock(async () => { Require(); await call().ConfigureAwait(false); return true; });

    public Task<int> ConnectAsync(Dictionary<string, string>? cookies, CancellationToken cancellationToken = default) =>
        UnderLock(() => { Require(); return inner.ConnectAsync(cookies, cancellationToken); }, cancellationToken);
    public Task<AccountRestore> RestoreAsync(Func<bool> stopped) => UnderLock(() =>
    {
        // Not an error: a restore asks for what was saved, and a held or staged account has nothing to restore here.
        if (!gate.Allows(MoveProviders.Messenger)) return Task.FromResult(new AccountRestore(null, Skipped: true));
        return inner.RestoreAsync(stopped);
    });
    public Task<string> DescribeAsync(CancellationToken cancellationToken = default) => inner.DescribeAsync(cancellationToken);
    public Task<bool> RefreshEncryptedStateAsync(CancellationToken cancellationToken = default) => inner.RefreshEncryptedStateAsync(cancellationToken);
    public Task<string?> EnableEncryptedAsync(CancellationToken cancellationToken = default) =>
        UnderLock(() => { Require(); return inner.EnableEncryptedAsync(cancellationToken); }, cancellationToken);
    public Task<string?> EnableEncryptedAfterSignInAsync() => UnderLock(() => { Require(); return inner.EnableEncryptedAfterSignInAsync(); });
    public Task SignOutAsync() => UnderLock(inner.SignOutAsync);
}

/// <summary>
/// The helper restart with the same gate in front: restarting a helper reconnects it from the saved session, which a staged or held account must not do. Only the two providers that move
/// with the PC are gated; every other scope, and a restart that is not a reconnect, is the inner restarter's own.
/// </summary>
internal sealed class GatedHelperRestarter(IHelperRestarter inner, IAccountGate gate) : IHelperRestarter
{
    public async Task<HelperRestartResult> RestartAsync(string scope, bool signingIn, bool force, string? token = null, bool settle = false)
    {
        var provider = scope == ProviderScopes.Messenger ? MoveProviders.Messenger : scope == ProviderScopes.Instagram ? MoveProviders.Instagram : null;
        var lifecycle = provider is null ? null : gate.LifecycleGate(provider);
        if (lifecycle is not null) await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            return provider is not null && !gate.Allows(provider) ? new HelperRestartResult("refused", AccountFailureWords.AccountMovedAway, 0)
                : await inner.RestartAsync(scope, signingIn, force, token, settle).ConfigureAwait(false);
        }
        finally { lifecycle?.Release(); }
    }
}

/// <summary>The same for Instagram (<see cref="GatedMessengerAccount"/>): no resume, restore or sign-in while the account is staged or has moved away.</summary>
internal sealed class GatedInstagramAccount(IInstagramAccount inner, IAccountGate gate) : IInstagramAccount
{
    private void Require()
    {
        if (!gate.Allows(MoveProviders.Instagram)) throw new InvalidOperationException(AccountFailureWords.AccountMovedAway);
    }

    /// <summary>The port behind the gate.</summary>
    internal IInstagramAccount Inner => inner;

    public InstagramAccountState State => inner.State;
    public event EventHandler? Changed { add => inner.Changed += value; remove => inner.Changed -= value; }
    public event EventHandler? ReconnectAbandoned { add => inner.ReconnectAbandoned += value; remove => inner.ReconnectAbandoned -= value; }
    private async Task<T> UnderLock<T>(Func<Task<T>> call, CancellationToken cancellationToken = default)
    {
        var lifecycle = gate.LifecycleGate(MoveProviders.Instagram);
        if (lifecycle is not null) await lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await call().ConfigureAwait(false); }
        finally { lifecycle?.Release(); }
    }

    public Task<int> ResumeAsync(CancellationToken cancellationToken = default) => UnderLock(() => { Require(); return inner.ResumeAsync(cancellationToken); }, cancellationToken);
    public Task<int> ConnectAsync(Dictionary<string, string> cookies, CancellationToken cancellationToken = default) =>
        UnderLock(() => { Require(); return inner.ConnectAsync(cookies, cancellationToken); }, cancellationToken);
    public Task<AccountRestore> RestoreAsync(Func<bool> stopped) => UnderLock(() =>
    {
        if (!gate.Allows(MoveProviders.Instagram)) return Task.FromResult(new AccountRestore(null, Skipped: true));
        return inner.RestoreAsync(stopped);
    });
    public Task SignOutAsync() => UnderLock(async () => { Require(); await inner.SignOutAsync().ConfigureAwait(false); return true; });
    public bool AnswersFromMemory(string chatId, string messageId) => inner.AnswersFromMemory(chatId, messageId);
}
