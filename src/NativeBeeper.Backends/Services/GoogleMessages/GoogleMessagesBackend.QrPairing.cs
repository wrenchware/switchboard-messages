using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.GoogleMessages;

// Pairing by QR code (SWITCHBOARD.md 5.4, chosen 2026-10-10). Google binds a Google-account sign-in to the browser that made it and refuses a copy of it two hours
// on, so a server cannot keep such a pairing alive. A QR pairing is the phone scanning a code in Google Messages > Device pairing > QR code scanner: the pairing that
// results holds no Google-account cookie and no Gaia pairing id, so there is no sign-in to expire, nothing to renew and no browser. Connecting with it is the helper's
// ordinary connect.
//
// A helper of its own does the pairing, so everything the pairing already has - its connection, its saved session, its sign-in clock, its kept browser profile - is left
// exactly as it was until the phone has accepted. Only then is the new session written over the old one (the same commit a sign-in uses), the old helper let go, and
// everything that belonged to the Google-account sign-in cleared. The code is a credential: it is handed to the caller as a picture, and is in no log, no error, no file.
internal sealed partial class GoogleMessagesBackend
{
    // Whether the saved pairing is a QR pairing: no Google-account sign-in to expire, renew or time. Read from the session when it is connected with or written, and
    // never guessed: a missing or unreadable session leaves it as it was.
    private volatile bool qrSession;
    public bool QrSession => qrSession;

    public async Task PairByQrAsync(IGoogleQrOutput output, CancellationToken token)
    {
        GoogleMessagesLog.Write("sign_in starting mode=qr");
        JsonElement session;
        try
        {
            // A helper that is only for this pairing. The one that is connected, if any, is not touched: it keeps the pairing this computer has until the phone accepts the new one.
            await using var pairing = await HelperConnection.StartAsync(HelperExecutables.PathFor("gmessages-helper"), null, token);
            pairing.Received += (kind, data) =>
            {
                // The refreshed codes. A picture that cannot be read is skipped; the next one replaces it.
                if (kind == "qr" && TryPicture(data, out var png, out var number)) output.Code(png, number);
            };
            var started = await pairing.CallAsync("pair.qrStart", null, token);
            if (!TryPicture(started, out var first, out var firstNumber)) throw new InvalidOperationException("pair_qr_failed");
            output.Code(first, firstNumber);
            session = await pairing.CallAsync("pair.qrWait", null, token);
        }
        catch (OperationCanceledException)
        {
            GoogleMessagesLog.Write("sign_in failed reason=cancelled mode=qr");
            throw;
        }
        catch (InvalidOperationException ex)
        {
            // The helper's own fixed words only (HelperErrorCodes): never its text, and nothing of the code.
            GoogleMessagesLog.Write($"sign_in failed reason={GoogleMessagesLog.Reason(ex)} mode=qr");
            LastSignInFailure = ex.Message;
            throw new BackendException(BackendFailure.AuthenticationRequired, ex.Message switch
            {
                "pair_qr_timeout" => "The code expired before your phone scanned it. Try again and scan the new code.",
                "pair_qr_start_failed" => "Google would not start the pairing. Check that the server can reach the internet and try again.",
                "pair_qr_refresh_failed" => "The code could not be refreshed. Try again.",
                "request_cancelled" => "Pairing was cancelled.",
                var code when GooglePairingFailure.Message(code) is { } reason => reason,
                _ => "Your phone did not finish pairing. In Google Messages, open Device pairing, choose QR code scanner and scan the new code."
            });
        }
        catch (Exception failure) { GoogleMessagesLog.Write($"sign_in failed reason={GoogleMessagesLog.Reason(failure)} mode=qr"); throw; }

        // The phone has accepted. From here the pairing is written whatever happens to the caller: a cancel now would throw away a pairing the phone already holds.
        await CommitQrSessionAsync(session);
    }

    private static bool TryPicture(JsonElement data, out byte[] png, out int number)
    {
        png = []; number = 0;
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("png", out var picture) || picture.ValueKind != JsonValueKind.String
            || !data.TryGetProperty("n", out var n) || !n.TryGetInt32(out number) || number < 1 || number > 64) return false;
        try { png = Convert.FromBase64String(picture.GetString() ?? ""); }
        catch (FormatException) { return false; }
        return png.Length is > 0 and <= 256 * 1024;
    }

    // The one commit, in the order SignInAsync's is: the connected helper is let go, the old pairing's bookkeeping is made, the new one is written, and what belonged to the
    // old sign-in is cleared. A failure to write leaves the old pairing on disk as it was (the write is a move of a finished file over it).
    private async Task CommitQrSessionAsync(JsonElement session)
    {
        BeginSignIn();
        try
        {
            await gate.WaitAsync(CancellationToken.None);
            try
            {
                connected = false;
                LastSignInFailure = null;
                var saved = GoogleSessionProfile.HasSavedSession;
                byte[]? replaced = saved ? await File.ReadAllBytesAsync(GoogleSessionProfile.SessionPath) : null;
                // Detached before it is disposed, so the old helper's end cannot mark the new connection down or write its session over the new one.
                var previous = helper;
                helper = null;
                latestSession = null;
                if (previous is not null) await previous.DisposeAsync();
                while (realtime.Reader.TryRead(out _)) { }
                if (replaced is not null) await KeepLocalProfileAsync(session, replaced, protector);
                var plain = Encoding.UTF8.GetBytes(session.GetRawText());
                try { await CommitSessionAsync(await protector.ProtectAsync(plain)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
                qrSession = true;
                if (tokens is GoogleSessionProfile profile) profile.Invalidate();
                // What belonged to the Google-account sign-in: its age (nothing renews or times a QR pairing), the cookies it handed over last, and the browser profile that
                // renewed it - a full Google sign-in left on the disk of a server that has no use for it.
                GoogleSignInCookies.Forget();
                await GoogleSignInClock.ClearAsync();
                await GoogleMessagesBrowserProfile.ForgetAsync();
                NoteSignedIn();
                ForgetDefaultSms(); ForgetSims();
                GoogleMessagesLog.Write($"sign_in saved minted=none mode=qr replaced={(saved ? "yes" : "no")}");
            }
            finally { gate.Release(); }
        }
        finally { signingIn = false; }
    }

    // Read where the session is read (ConnectAsync) and where it is written; a session that is not an object leaves the answer as it was.
    private void NoteSessionKind(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return;
        var qr = GoogleSessionKind.IsQr(session);
        if (qr != qrSession) GoogleMessagesLog.Write($"session kind={(qr ? "qr" : "google_account")}");
        qrSession = qr;
    }
}

// What kind of pairing a saved session is. A Google-account pairing (Gaia) names the phone it was made with (dest_reg_id) and carries the account's cookies; a QR pairing has
// neither, which is how libgm itself tells them apart (AuthData.IsGoogleAccount, HasCookies).
internal static class GoogleSessionKind
{
    public static bool IsQr(JsonElement session)
    {
        if (session.ValueKind != JsonValueKind.Object) return false;
        if (!session.TryGetProperty("browser", out var browser) || browser.ValueKind != JsonValueKind.Object) return false;
        if (session.TryGetProperty("dest_reg_id", out var destination) && destination.ValueKind == JsonValueKind.String
            && destination.GetString() is { Length: > 0 } id && id.Any(character => character is not ('0' or '-'))) return false;
        if (session.TryGetProperty("cookies", out var cookies) && cookies.ValueKind == JsonValueKind.Object && cookies.EnumerateObject().Any()) return false;
        return true;
    }
}
