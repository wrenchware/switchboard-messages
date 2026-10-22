using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// Google Messages paired by QR code on the server (SWITCHBOARD.md 5.4; chosen 2026-10-10): the phone scans a code the client shows (Google Messages &gt; Device pairing &gt;
/// QR code scanner), and the pairing that results holds no Google-account sign-in. Google binds such a sign-in to the browser that made it and refuses a copy at two hours, so
/// this is the way a server keeps a pairing alive; it needs no browser, no Xvfb and no keeper.
/// <para>
/// One at a time, under the same guard as every Google sign-in on this server (<see cref="SignInGuard"/>). Ended by the caller's cancel (the client's, or its connection closing),
/// <see cref="Cancel"/>, or <see cref="Limit"/>: the codes run out in three minutes, and this is the host's own bound beside them. Whatever ends it before the phone has accepted leaves
/// the pairing the server already had exactly as it was; the backend replaces it only when the new one has succeeded.
/// </para>
/// <para>
/// The code is a credential (it carries the pairing key and this device's request keys). Log lines are fixed words and a number: <c>google qr_pairing started</c>, <c>refreshed n=</c>,
/// <c>paired</c>, <c>cancelled reason=</c> (<c>client</c>, <c>cancel</c> or <c>timeout</c>), <c>failed reason=</c>, <c>busy</c>. Never a picture, never what it encodes.
/// </para>
/// </summary>
internal sealed class GoogleQrPairing(IGoogleMessagesAccount account, Action<string> log, SignInGuard? guard = null) : IQrPairingHost
{
    /// <summary>The longest one pairing runs. The helper's six codes last three minutes; this is the host's bound beside them.</summary>
    public TimeSpan Limit { get; init; } = TimeSpan.FromMinutes(5);

    private readonly object gate = new();
    private readonly SignInGuard signInGuard = guard ?? new SignInGuard();
    private Session? current;

    private sealed class Session(object owner)
    {
        public object Owner { get; } = owner;
        public CancellationTokenSource Stop { get; } = new();
    }

    public async Task PairAsync(QrPairingRequest request, IGoogleQrOutput output, CancellationToken cancellationToken)
    {
        using var lease = signInGuard.TryEnter();
        Session session;
        lock (gate)
        {
            if (lease is null || current is not null)
            {
                log("google qr_pairing busy");
                throw new InvalidOperationException("sign_in_busy");
            }
            session = current = new Session(request.Owner);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, session.Stop.Token);
        linked.CancelAfter(Limit);
        log("google qr_pairing started");
        try
        {
            await account.PairByQrAsync(new Counting(output, log), linked.Token).ConfigureAwait(false);
            log("google qr_pairing paired");
        }
        catch (OperationCanceledException)
        {
            log("google qr_pairing cancelled reason=" + (cancellationToken.IsCancellationRequested ? "client" : session.Stop.IsCancellationRequested ? "cancel" : "timeout"));
            throw;
        }
        // The backend's own failure: its kind, never its words (a sentence for the person, which the helper's code stands behind).
        catch (BackendException failure)
        {
            log("google qr_pairing failed reason=" + failure.Failure.ToString().ToLowerInvariant());
            throw;
        }
        catch (Exception failure)
        {
            log("google qr_pairing failed type=" + failure.GetType().Name);
            throw;
        }
        finally
        {
            lock (gate) current = null;
            session.Stop.Dispose();
        }
    }

    public void Cancel(object owner)
    {
        Session? session;
        lock (gate) session = current;
        if (session is null || !ReferenceEquals(session.Owner, owner)) return;
        try { session.Stop.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    // Passes each picture on and counts them: the first is the start, each after it a refresh. The number is the only thing said.
    private sealed class Counting(IGoogleQrOutput inner, Action<string> log) : IGoogleQrOutput
    {
        public void Code(ReadOnlyMemory<byte> png, int number)
        {
            if (number > 1) log($"google qr_pairing refreshed n={number}");
            inner.Code(png, number);
        }
    }
}
