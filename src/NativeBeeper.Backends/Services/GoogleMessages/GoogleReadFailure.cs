using NativeBeeper.Core.Backend;

namespace NativeBeeper.App.Services.GoogleMessages;

internal static class GoogleReadFailure
{
    // Google refused the sign-in (or it is old enough to be refused) and the window is renewing it invisibly. The failure
    // is still AuthenticationRequired, which lists read quietly as unavailable (PartialChatPages); only the words change,
    // since nothing needs doing and in a moment nothing will be wrong. Transient, so an open conversation waits for the
    // reconnect rather than showing this for the few seconds a renewal at launch takes.
    public static BackendException Renewing() => new(BackendFailure.AuthenticationRequired, "Google Messages is renewing its sign-in. It reconnects by itself in a moment.") { Transient = true };

    public static BackendException Map(string code) => code switch
    {
        // Google stopped accepting this computer's Google-account sign-in; the phone keeps the pairing. The Settings button
        // says Renew sign-in while that is so (MainWindow.Settings). There never was a "Sign in with Google" button.
        "authentication_required" => new(BackendFailure.AuthenticationRequired, "Google stopped accepting this computer's sign-in. Open Settings and choose Renew sign-in."),
        "pairing_required" => new(BackendFailure.AuthenticationRequired, "Google Messages needs pairing again. Open Settings and choose Pair again."),
        // The phone removed this computer from Device pairing, or Google signed it out: only pairing again brings it back.
        "pairing_lost" => new(BackendFailure.AuthenticationRequired, "Google Messages is no longer paired with this computer. Open Settings and choose Pair again."),
        "rate_limited" => new(BackendFailure.RateLimited, "Google Messages is limiting requests. Please wait a moment."),
        "access_denied" => new(BackendFailure.Forbidden, "Google denied this request. Your saved sign-in has been kept."),
        "unexpected_response" => new(BackendFailure.InvalidResponse, "Google Messages returned an unexpected response. Try again shortly."),
        _ => new(BackendFailure.Unavailable, "Google Messages could not reach this conversation. Keep your phone online; the connection will retry automatically.")
    };
}
