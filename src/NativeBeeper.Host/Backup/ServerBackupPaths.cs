namespace NativeBeeper.Host.Backup;

/// <summary>
/// Where a server keeps what its backup reads and its restore writes (SWITCHBOARD.md stage 10): the data folder and the keys folder, which a Windows server without
/// a keys folder of its own gives as the same folder. The host names its folders; everything below them is fixed here. The names a network host owns
/// (<c>devices.json</c>, <c>push.json</c>, <c>tls/</c>, <c>push/</c>, <c>join-requests-paused</c>, <c>devices.generation</c>) are its own constants, which this
/// project cannot see: the harness holds the two together (ServerBackupChecks).
/// </summary>
internal sealed record ServerBackupPaths(string Data, string Keys)
{
    public string State => Path.Combine(Data, "state.db");
    public string Shared => Path.Combine(Data, "Server", SharedStateStore.FileName);
    public string Space => Path.Combine(Data, "Server", SharedSpace.FileName);
    public string History => Path.Combine(Data, "history.db");
    public string Archive => Path.Combine(Data, "archive.db");
    public string Photos => Path.Combine(Data, "saved-photos.db");
    public string Devices => Path.Combine(Data, "devices.json");
    public string Push => Path.Combine(Data, "push.json");
    /// <summary>The record of the last restore (<see cref="RestoredMark"/>).</summary>
    public string Restored => Path.Combine(Data, RestoredMark.FileName);
    /// <summary>The backup's own settings and its sealed password.</summary>
    public string Backup => Path.Combine(Data, "Backup");
    public string BackupSettings => Path.Combine(Backup, "settings.json");
    public string BackupPassword => Path.Combine(Backup, "password.protected");
    /// <summary>Where a backup copies a store with a rollback journal before reading it (StoreSnapshot); gone once read.</summary>
    public string Snapshots => Path.Combine(Backup, "snapshots");
    /// <summary>A restore's plan, its staged files and what it left to take in once an account is signed in again (<see cref="ServerRestore"/>).</summary>
    public string Restore => Path.Combine(Data, "Restore");
    public string Tls => Path.Combine(Keys, "tls");
    public string TlsCertificate => Path.Combine(Tls, "server.crt");
    public string TlsKey => Path.Combine(Tls, "server.key");
    public string PushProject => Path.Combine(Keys, "push");
    public const string FirebaseSettings = "google-services.json", FirebaseAccount = "service-account.json";
    public string JoinPause => Path.Combine(Keys, "join-requests-paused");
    public string DevicesGeneration => Path.Combine(Keys, "devices.generation");
}
