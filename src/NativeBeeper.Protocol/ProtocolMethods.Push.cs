namespace NativeBeeper.Protocol;

// Notifications to a phone (SWITCHBOARD.md 7.5, Decisions: encrypted pushes). A server set up with the owner's own Firebase project offers its app settings
// (`push.offer`); the phone makes a P-256 key and a secret, asks Firebase for a token, and gives the three to the server (`push.register`), which keeps them
// beside the paired device and forgets them when the device is removed. Each new message then goes to a phone that is not connected as a push sealed to
// its key (Web Push encryption, RFC 8291), so Firebase carries only ciphertext of one padded size. A server without the project offers nothing, and a
// phone then asks for nothing. Neither call sends anything to a service; `push.register` is per device, so it is answered only on a network connection.
public static partial class ProtocolMethods
{
    public const string PushOffer = "push.offer";
    public const string PushRegister = "push.register";

    private static Entry[] PushEntries() =>
    [
        new(PushOffer, "IPushService.OfferAsync", null, typeof(PushOffer)),
        new(PushRegister, "IPushService.RegisterAsync", typeof(PushRegisterParams), typeof(Done)),
    ];
}

/// <summary>
/// The owner's Firebase project as a phone needs it to ask Firebase for a token: none of it is a secret (they are the values of the app's own
/// google-services.json). <paramref name="Apps"/> names the Firebase app of each Android package the project knows; a phone whose package is not among them
/// asks for nothing. <paramref name="Available"/> false: this server sends no pushes (no project set up), and the rest is empty.
/// </summary>
public sealed record PushOffer(bool Available, string? ProjectId = null, string? SenderId = null, string? ApiKey = null, IReadOnlyList<PushApp>? Apps = null);

public sealed record PushApp(string Package, string AppId);

/// <summary>
/// The calling device's push address: Firebase's <paramref name="Token"/> for its <paramref name="Package"/>, and the P-256 public key (65-byte uncompressed
/// point, base64) and 16-byte secret (base64) its pushes are sealed to. Registering again replaces it; a device registers whenever any of them changes.
/// </summary>
// Opt-in: only a phone that understands a notification-free "sync" push may receive one. Older clients and saved registrations default to false.
public sealed record PushRegisterParams(string Token, string Package, string PublicKey, string Auth, bool BackgroundSync = false);

/// <summary>The server's pushes, for the connection that asks: a network connection's carries its device, so a registration is that device's.</summary>
public interface IPushService
{
    Task<PushOffer> OfferAsync(CancellationToken cancellationToken = default);
    Task RegisterAsync(PushRegisterParams registration, CancellationToken cancellationToken = default);
}
