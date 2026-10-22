using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// Google Messages paired by QR code (SWITCHBOARD.md 5.4, chosen 2026-10-10; protocol version 13). A Google-account sign-in cannot be kept alive on a server (Google binds it
// to the browser that made it and refuses a copy at two hours), so a server pairs the way the upstream bridge does: the phone scans a code (Google Messages > Device pairing > QR
// code scanner) and the pairing holds no Google sign-in at all.
//
//   accounts.google.qrPairing         A journaled mutation like accounts.google.signIn, and it ends as that one does (Done when the pairing is saved, or the backend's failure). The
//                                     host starts the pairing and sends each picture of the code as a `qrCode` event to this connection only; the code lasts thirty seconds, so
//                                     a new one follows until the phone scans one or the attempt ends. One at a time on the server (with every Google sign-in). Ended by the
//                                     call's own cancel, by qrPairingCancel, by the connection closing, or by the host's own limit; whatever ends it before the phone has accepted
//                                     leaves the pairing the server already had exactly as it was.
//   accounts.google.qrPairingCancel   Not a mutation: ends this connection's pairing, if it has one. Answered Done either way.
//
// The code carries the pairing key and this device's request keys: it is a credential. So a picture is never logged, journaled, kept in the event log or written to a file, by either
// end; the events carry no identifier a log could show, an error names no part of them, and the types that hold them print only their own name.
public static partial class ProtocolMethods
{
    public const string QrPairingMethod = "accounts.google.qrPairing";
    public const string QrPairingCancelMethod = "accounts.google.qrPairingCancel";

    private static Entry[] QrPairingEntries() =>
    [
        new(QrPairingMethod, "IGoogleMessagesAccount.PairByQrAsync", typeof(QrPairingParams), typeof(Done), true),
        new(QrPairingCancelMethod, "IQrPairingHost.Cancel", null, typeof(Done)),
    ];
}

/// <summary>Starts a pairing by QR code.</summary>
public sealed record QrPairingParams(string ClientOpId) : MutationParams(ClientOpId);

/// <summary>
/// One picture of the code, pushed to the connection that is pairing and to no other (event kind <see cref="EventKinds.QrCode"/>, <c>seq</c> 0, never in the event log and never
/// replayed). <paramref name="Data"/> is a PNG, base64, at most <see cref="QrPairing.MaxPictureCharacters"/> characters; <paramref name="Number"/> counts the pictures of this pairing
/// from 1 and each replaces the one before. The picture is a credential: the record prints only its name.
/// </summary>
public sealed record QrCodeEvent(string ClientOpId, int Number, string Data)
{
    public override string ToString() => nameof(QrCodeEvent);
}

public static class QrPairing
{
    /// <summary>The longest picture the host sends, as base64 (about 192 KiB of PNG, several times what a code needs); a bigger one is not sent.</summary>
    public const int MaxPictureCharacters = 262_144;
}

/// <summary>Who is pairing: <paramref name="Owner"/> is the connection (the host ends this pairing for that connection only).</summary>
public sealed record QrPairingRequest(object Owner, string ClientOpId);

/// <summary>
/// The host's pairing by QR code, as the dispatcher serves it. A host without one (every host but the Linux server run with Google Messages) leaves it null, and both methods answer
/// <c>not_supported</c>.
/// </summary>
public interface IQrPairingHost
{
    /// <summary>
    /// Runs one pairing to its end: each picture of the code goes to <paramref name="output"/>, and the call returns when the pairing is saved. Failures are fixed code words as
    /// <see cref="InvalidOperationException"/> (<c>sign_in_busy</c>) or the backend's own.
    /// </summary>
    Task PairAsync(QrPairingRequest request, IGoogleQrOutput output, CancellationToken cancellationToken);

    /// <summary>Ends <paramref name="owner"/>'s pairing, if it has one.</summary>
    void Cancel(object owner);
}
