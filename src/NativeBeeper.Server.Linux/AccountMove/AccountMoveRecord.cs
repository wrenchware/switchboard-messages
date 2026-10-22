using System.Text.Json;
using System.Text.RegularExpressions;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Storage;

namespace NativeBeeper.Server.Linux;

/// <summary>Where an account move keeps things, on the data volume (sealed with the host's key) and on the keys volume (the helper's own files). Plain paths; nothing here reads a file.</summary>
internal sealed record AccountMovePaths(string DataRoot, string KeysRoot)
{
    public const string SentinelFile = "moved-away.json";

    /// <summary>A file in a Messenger account folder that says the account arrived by a move: the helper then never registers a replacement device on its own when Facebook refuses the one it came with.</summary>
    public const string MovedInFile = "moved-in.json";
    private static readonly Regex MoveId = new("^[A-Za-z0-9_-]{8,64}$", RegexOptions.CultureInvariant);
    private static readonly Regex MessengerKey = new("^[0-9a-f]{32}$", RegexOptions.CultureInvariant);
    private static readonly Regex InstagramKey = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    public static bool IsMoveId(string? value) => value is not null && MoveId.IsMatch(value);
    public static bool IsAccountKey(string provider, string? key) =>
        key is not null && (provider == MoveProviders.Messenger ? MessengerKey.IsMatch(key) : provider == MoveProviders.Instagram && InstagramKey.IsMatch(key));

    /// <summary>The sealed record of every provider's move state.</summary>
    public string RecordFile => Path.Combine(DataRoot, "Server", "account-move.protected");

    /// <summary>The plain record of what has moved away: no secret in it, so that an offline command can clear it without the keys (<c>--clear-held</c>).</summary>
    public string HeldFile => Path.Combine(DataRoot, "Server", "account-held.json");

    /// <summary>Everything a move stages on the data volume, one folder a move.</summary>
    public string StagingRoot => Path.Combine(DataRoot, "Server", "account-move");
    public string Staging(string moveId) => Path.Combine(StagingRoot, moveId);
    public string StagedMessengerSession(string moveId) => Path.Combine(Staging(moveId), "Messenger", "session.protected");
    public string StagedMessengerFlag(string moveId) => Path.Combine(Staging(moveId), "Messenger", "encrypted.on");
    public string StagedInstagramRoot(string moveId) => Path.Combine(Staging(moveId), "Instagram");
    public string StagedInstagramGuard(string moveId) => Path.Combine(StagedInstagramRoot(moveId), InstagramSessionStore.GuardFile);

    /// <summary>The helper's private root for a staged Messenger account: its accounts/&lt;key&gt; folder (e2ee.db, messages.db) is renamed into the live root at activation.</summary>
    public string IncomingMessengerRoot(string moveId) => Path.Combine(KeysRoot, "Messenger", ".incoming-" + moveId);
    public string IncomingMessengerAccount(string moveId, string key) => Path.Combine(IncomingMessengerRoot(moveId), "accounts", key);

    // ---- Where the server runs them from (the same places the backends read) ----
    public string LiveMessengerFolder => Path.Combine(DataRoot, "Messenger");
    public string LiveMessengerSession => Path.Combine(LiveMessengerFolder, "session.protected");
    public string LiveMessengerFlag => Path.Combine(LiveMessengerFolder, "encrypted.on");
    public string LiveMessengerChats => Path.Combine(LiveMessengerFolder, "chats.protected");
    public string LiveMessengerPrivateRoot => Path.Combine(KeysRoot, "Messenger");
    public string LiveMessengerAccount(string key) => Path.Combine(LiveMessengerPrivateRoot, "accounts", key);
    public string LiveInstagramRoot => Path.Combine(DataRoot, "Instagram");
    public string LiveInstagramPointer => Path.Combine(LiveInstagramRoot, "active.protected");
    public string LiveInstagramGuard => Path.Combine(LiveInstagramRoot, InstagramSessionStore.GuardFile);
    public string LiveInstagramAccount(string key) => Path.Combine(LiveInstagramRoot, "accounts", key);

    /// <summary>
    /// Where the files of a sign-in made on the server itself, and signed out since, go when a move brings the same account in: kept beside the live root, never deleted.
    /// Signing out keeps a Messenger account's device keys (a sign-in again reuses the device), so they sit exactly where the PC's would go.
    /// </summary>
    public string SetAsideMessenger(string key, string moveId) => Path.Combine(LiveMessengerPrivateRoot, "set-aside", key + "-" + moveId);
    public string SetAsideInstagram(string key, string moveId) => Path.Combine(LiveInstagramRoot, "set-aside", key + "-" + moveId);
}

/// <summary>One staged or installed piece, as the record keeps it (the SHA-256 is of the plaintext).</summary>
internal sealed record StoredPiece(string Name, long Bytes, string Sha256);

internal sealed record StoredHistory(int Next = 0, int Refused = 0, Dictionary<string, int>? Sections = null, bool Complete = false);

/// <summary>One provider's record. <paramref name="State"/> is one of <see cref="MoveStates"/>' words; <paramref name="At"/> is UTC milliseconds of the last change.</summary>
internal sealed record ProviderRecord(string State, string? MoveId = null, string? Key = null, List<StoredPiece>? Pieces = null, StoredHistory? History = null, long At = 0)
{
    public static ProviderRecord None { get; } = new(MoveStates.None);
    public IReadOnlyList<StoredPiece> PieceList => Pieces ?? [];
    public StoredHistory Held => History ?? new();
}

internal sealed record StoredRecord(int Version, Dictionary<string, ProviderRecord> Providers);

/// <summary>What has moved away, per provider: the plain file <see cref="AccountMovePaths.HeldFile"/>.</summary>
internal sealed record HeldEntry(string? MoveId, long At, bool Released);
internal sealed record HeldFileContent(int Version, Dictionary<string, HeldEntry> Held);

/// <summary>
/// Reads and writes the two records. The sealed one holds each provider's move state and what was staged; the plain one only that an account moved away. Each is written whole beside
/// itself and moved into place, flushed. A record that cannot be read is <see cref="Readable"/> false and is never guessed at: the host treats every provider as held.
/// </summary>
internal sealed class AccountMoveRecords(AccountMovePaths paths, IStateProtector protector, Action<string> log)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Dictionary<string, ProviderRecord> Providers { get; private set; } = new(StringComparer.Ordinal);
    public Dictionary<string, HeldEntry> Held { get; private set; } = new(StringComparer.Ordinal);

    /// <summary>False when either record is there and cannot be read.</summary>
    public bool Readable { get; private set; } = true;

    public async Task LoadAsync()
    {
        Readable = true;
        Providers = new(StringComparer.Ordinal);
        Held = new(StringComparer.Ordinal);
        try
        {
            if (File.Exists(paths.RecordFile))
            {
                var plain = await protector.UnprotectAsync(await File.ReadAllBytesAsync(paths.RecordFile).ConfigureAwait(false)).ConfigureAwait(false);
                var stored = JsonSerializer.Deserialize<StoredRecord>(plain, Json);
                if (stored is not { Version: 1, Providers: not null }) throw new InvalidDataException();
                Providers = new(stored.Providers.Where(pair => MoveProviders.IsKnown(pair.Key)), StringComparer.Ordinal);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            log($"account move record unreadable error={failure.GetType().Name}");
            Readable = false;
        }
        try
        {
            if (File.Exists(paths.HeldFile))
            {
                var stored = JsonSerializer.Deserialize<HeldFileContent>(await File.ReadAllBytesAsync(paths.HeldFile).ConfigureAwait(false), Json);
                if (stored is not { Version: 1, Held: not null }) throw new InvalidDataException();
                Held = new(stored.Held.Where(pair => MoveProviders.IsKnown(pair.Key)), StringComparer.Ordinal);
            }
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            log($"account move hold record unreadable error={failure.GetType().Name}");
            Readable = false;
        }
    }

    public async Task SaveProvidersAsync()
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(new StoredRecord(1, Providers), Json);
        try { await WriteAsync(paths.RecordFile, await protector.ProtectAsync(plain).ConfigureAwait(false)).ConfigureAwait(false); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain); }
    }

    public Task SaveHeldAsync() => WriteAsync(paths.HeldFile, JsonSerializer.SerializeToUtf8Bytes(new HeldFileContent(1, Held), Json));

    /// <summary>Written whole beside the file and moved over it, flushed, so a stop leaves the old record or the new one.</summary>
    public static async Task WriteAsync(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(content).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }
}
