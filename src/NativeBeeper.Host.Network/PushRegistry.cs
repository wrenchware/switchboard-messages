using System.Text.Json;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Host.Network;

/// <summary>
/// Each paired device's push address (SWITCHBOARD.md 7.5): Firebase's token for it and the key and secret its pushes are sealed to. Kept in
/// <c>push.json</c> beside <c>devices.json</c>, sealed the same way, and forgotten when the device is removed or Firebase says the token is gone. A token is
/// never logged.
/// </summary>
/// <remarks>
/// Only a device paired now has a row that counts (<c>paired</c>, the device list's own answer): a row can outlive its device (a <c>push.json</c> put back
/// from before a revoke, a <c>devices.json</c> the owner removed, a registration that raced its device's removal, a removal whose write failed), so rows of
/// devices no longer paired are let go at load, a registration from one is refused, and the notifier asks again before each push.
/// </remarks>
internal sealed class PushRegistry
{
    public const string FileName = "push.json";

    public sealed record Registration(string DeviceId, string Token, string Package, byte[] PublicKey, byte[] Auth, DateTimeOffset At, bool BackgroundSync = false);
    private sealed record Stored(string DeviceId, string Token, string Package, string PublicKey, string Auth, DateTimeOffset At, bool BackgroundSync = false);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string path;
    private readonly IStateProtector protector;
    private readonly Action<string> log;
    private readonly Func<string, bool> paired;
    private readonly object gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private Dictionary<string, Registration> registrations;

    private PushRegistry(string path, IStateProtector protector, Action<string> log, Func<string, bool> paired, Dictionary<string, Registration> registrations)
    {
        this.path = path; this.protector = protector; this.log = log; this.paired = paired; this.registrations = registrations;
    }

    /// <summary>
    /// The registrations kept in <paramref name="folder"/> of devices <paramref name="paired"/> says are paired; none when there is no file, or one that cannot
    /// be read (each phone registers again when it connects). Rows of other devices, or with a key no push can be sealed to, are let go, and the file written
    /// without them.
    /// </summary>
    public static async Task<PushRegistry> LoadAsync(string folder, IStateProtector protector, Action<string> log, Func<string, bool> paired)
    {
        var path = Path.Combine(folder, FileName);
        var kept = new Dictionary<string, Registration>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                var opened = await protector.UnprotectAsync(await File.ReadAllBytesAsync(path).ConfigureAwait(false)).ConfigureAwait(false);
                foreach (var stored in JsonSerializer.Deserialize<List<Stored>>(opened, Json) ?? [])
                    kept[stored.DeviceId] = new(stored.DeviceId, stored.Token, stored.Package, Convert.FromBase64String(stored.PublicKey), Convert.FromBase64String(stored.Auth), stored.At, stored.BackgroundSync);
            }
            catch (Exception failure) when (failure is IOException or JsonException or FormatException or System.Security.Cryptography.CryptographicException or InvalidDataException)
            {
                log($"push registrations unreadable type={failure.GetType().Name}");
            }
        }
        // And a key no push can be sealed to, kept before registrations were held to one: every push to it would fail.
        var letGo = kept.Values.Where(r => !paired(r.DeviceId) || !WebPushEncryption.Accepts(r.PublicKey)).Select(r => r.DeviceId).ToArray();
        foreach (var id in letGo) kept.Remove(id);
        log($"push registrations count={kept.Count}");
        var registry = new PushRegistry(path, protector, log, paired, kept);
        if (letGo.Length > 0)
        {
            log($"push registrations pruned count={letGo.Length}");
            await registry.SaveAsync().ConfigureAwait(false);
        }
        return registry;
    }

    /// <summary>Whether <paramref name="deviceId"/> is paired now: no push goes to any other.</summary>
    public bool Paired(string deviceId) => paired(deviceId);

    /// <summary>Every registration kept; whether its device is still paired is asked as each push goes (<see cref="Paired"/>).</summary>
    public IReadOnlyList<Registration> All()
    {
        lock (gate) return [.. registrations.Values];
    }

    /// <summary>The registration of <paramref name="deviceId"/> as it is now, or null for none.</summary>
    public Registration? Get(string deviceId)
    {
        lock (gate) return registrations.GetValueOrDefault(deviceId);
    }

    /// <summary>Keeps <paramref name="registration"/>, replacing the device's last; false, keeping nothing, for a device that is not paired.</summary>
    public async Task<bool> SetAsync(Registration registration)
    {
        if (!paired(registration.DeviceId))
        {
            log("push registration refused why=device_unpaired");
            return false;
        }
        int moved;
        lock (gate)
        {
            // A phone that joined again without being removed first brings the same token under its new id: the old row would send each message twice.
            var others = registrations.Values.Where(r => r.DeviceId != registration.DeviceId && r.Token == registration.Token).Select(r => r.DeviceId).ToArray();
            foreach (var other in others) registrations.Remove(other);
            moved = others.Length;
            if (moved == 0 && registrations.TryGetValue(registration.DeviceId, out var was) && was.Token == registration.Token && was.Package == registration.Package &&
                was.PublicKey.AsSpan().SequenceEqual(registration.PublicKey) && was.Auth.AsSpan().SequenceEqual(registration.Auth) && was.BackgroundSync == registration.BackgroundSync) return true;
            registrations[registration.DeviceId] = registration;
        }
        if (moved > 0) log($"push registration removed why=token_moved count={moved}");
        log("push registered");
        await SaveAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Forgets the device's registration; given <paramref name="token"/>, only while the row still holds that token (one registered since stays).</summary>
    public Task RemoveAsync(string deviceId, string why, string? token = null)
    {
        lock (gate)
        {
            if (!registrations.TryGetValue(deviceId, out var was) || token is not null && was.Token != token) return Task.CompletedTask;
            registrations.Remove(deviceId);
        }
        log($"push registration removed why={why}");
        return SaveAsync();
    }

    private async Task SaveAsync()
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            List<Stored> snapshot;
            lock (gate) snapshot = [.. registrations.Values.Select(r => new Stored(r.DeviceId, r.Token, r.Package, Convert.ToBase64String(r.PublicKey), Convert.ToBase64String(r.Auth), r.At, r.BackgroundSync))];
            var sealedBytes = await protector.ProtectAsync(JsonSerializer.SerializeToUtf8Bytes(snapshot, Json)).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var stream = new FileStream(temporary, options))
            {
                await stream.WriteAsync(sealedBytes).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, true);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log($"push registrations not saved type={failure.GetType().Name}");
        }
        finally { writing.Release(); }
    }
}
