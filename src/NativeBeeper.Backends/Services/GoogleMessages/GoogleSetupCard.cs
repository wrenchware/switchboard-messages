namespace NativeBeeper.App.Services.GoogleMessages;

// What the Google Messages card in Settings offers beyond its two buttons' words (MainWindow.UpdateSettingsStatus). Never
// paired, the only thing to do is pair: Connect could only fail ("Pair your Google Messages account to connect."), so it
// is not offered, Pair is the accent button, the status line has no connection to report, and the card says what pairing
// needs, since somebody with an iPhone cannot pair at all. Paired, it is as it was.
internal sealed record GoogleSetupCard(bool OffersConnect, bool PairIsAccent, bool ShowsStatus, string? Needs)
{
    public const string NeedsPhone = "Needs an Android phone.";
    public static GoogleSetupCard For(bool paired) => paired
        ? new(OffersConnect: true, PairIsAccent: false, ShowsStatus: true, Needs: null)
        : new(OffersConnect: false, PairIsAccent: true, ShowsStatus: false, Needs: NeedsPhone);
}
