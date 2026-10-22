using System.Text.Json;
using NativeBeeper.GoogleMessages.Spike;

namespace NativeBeeper.App.Services.Messenger;

/// <summary>
/// Messenger's retained history read out of, or written into, one account's <c>messages.db</c> by a helper that cannot sign in (SWITCHBOARD.md stage 6 slice 13, accounts move with
/// the PC). It is what a move uses on both machines: the PC exports its history and the server imports it, and the other way round for a move back. The helper is started in its offline
/// mode (<c>NATIVEBEEPER_MESSENGER_OFFLINE=1</c>), which refuses every method but the history ones, so nothing in this process can log in, connect, register a device or reach Facebook.
/// </summary>
internal interface IOfflineHistory : IAsyncDisposable
{
    /// <summary>Opens the store of the account whose Facebook id this is: one that is there (<paramref name="create"/> false) or a new one.</summary>
    Task OpenAsync(string facebookId, bool create, CancellationToken cancellationToken);

    /// <summary>The first page (null cursor), or the page after the one whose <c>next</c> is <paramref name="cursor"/>.</summary>
    Task<JsonElement> ExportPageAsync(string? cursor, CancellationToken cancellationToken);

    /// <summary>Writes one page; the answer is the helper's own count of what it did.</summary>
    Task<JsonElement> ImportPageAsync(JsonElement page, CancellationToken cancellationToken);

    /// <summary>What the opened store holds now, in an export's sections (<c>contacts</c>, <c>threads</c>, <c>messages</c> and the rest), read back from the database: the check that what an import reported is there.</summary>
    Task<JsonElement> CountAsync(CancellationToken cancellationToken);

    /// <summary>The newest encrypted-device database schema this helper build opens, from its hello (<see cref="MessengerDeviceSchema"/>); null when it does not say.</summary>
    int? DeviceSchema { get; }
}

/// <summary>Starts offline history helpers. <paramref name="privateRoot"/> / <paramref name="dataRoot"/> say where the store lives (the Linux server's private root, or a Windows data root); both null is the host's own.</summary>
internal interface IOfflineHistoryStarter
{
    Task<IOfflineHistory> StartAsync(string? privateRoot, string? dataRoot, CancellationToken cancellationToken);

    /// <summary>Whether a helper can be started at all (its executable is there), so a move can say so before it changes anything.</summary>
    bool Available { get; }
}

/// <summary>The real starter: the Messenger helper beside the host, in offline mode. A helper that does not say it is offline is closed, not used.</summary>
internal sealed class MessengerOfflineHistoryStarter : IOfflineHistoryStarter
{
    public bool Available => File.Exists(HelperExecutables.PathFor("messenger-helper"));

    public async Task<IOfflineHistory> StartAsync(string? privateRoot, string? dataRoot, CancellationToken cancellationToken)
    {
        var path = HelperExecutables.PathFor("messenger-helper");
        if (!File.Exists(path)) throw new InvalidOperationException("helper_missing");
        var environment = new Dictionary<string, string> { ["NATIVEBEEPER_MESSENGER_OFFLINE"] = "1" };
        if (privateRoot is { Length: > 0 }) environment[MessengerKeptAccount.PrivateRootVariable] = privateRoot;
        if (dataRoot is { Length: > 0 }) environment["NATIVEBEEPER_DATA_ROOT"] = dataRoot;
        var started = await HelperConnection.StartAsync(path, null, cancellationToken, environment).ConfigureAwait(false);
        try
        {
            var hello = await started.CallAsync("hello", null, cancellationToken).ConfigureAwait(false);
            if (hello.GetProperty("protocol").GetInt32() != 4) throw new InvalidOperationException("unsupported_protocol");
            if (!hello.TryGetProperty("offline", out var offline) || offline.ValueKind != JsonValueKind.True) throw new InvalidOperationException("helper_not_offline");
            return new Offline(started, MessengerDeviceSchema.SupportedBy(hello));
        }
        catch
        {
            try { await started.DisposeAsync().ConfigureAwait(false); } catch (Exception) { }
            throw;
        }
    }

    private sealed class Offline(HelperConnection helper, int? deviceSchema) : IOfflineHistory
    {
        public int? DeviceSchema => deviceSchema;

        public async Task OpenAsync(string facebookId, bool create, CancellationToken cancellationToken) =>
            await helper.CallAsync("backup.open", new { account = facebookId, create }, cancellationToken).ConfigureAwait(false);

        public Task<JsonElement> ExportPageAsync(string? cursor, CancellationToken cancellationToken) =>
            helper.CallAsync("backup.export", new { cursor = cursor ?? "" }, cancellationToken);

        public Task<JsonElement> CountAsync(CancellationToken cancellationToken) => helper.CallAsync("backup.count", null, cancellationToken);

        public Task<JsonElement> ImportPageAsync(JsonElement page, CancellationToken cancellationToken) =>
            helper.CallAsync("backup.import", page, cancellationToken);

        public async ValueTask DisposeAsync()
        {
            try { await helper.CallAsync("backup.exportEnd", null, CancellationToken.None).ConfigureAwait(false); } catch (Exception) { }
            await helper.DisposeAsync().ConfigureAwait(false);
        }
    }
}
