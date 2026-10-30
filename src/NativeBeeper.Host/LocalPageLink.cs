using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NativeBeeper.Host;

/// <summary>
/// The Windows server's page on this PC (SWITCHBOARD.md 10.7): how Messages opens it signed in, with no password to type, and how the page knows the link is the app's.
/// <list type="bullet">
/// <item>The server writes where its page listens, <c>&lt;root&gt;\Server\page.json</c> (the port alone, nothing secret), each time it starts.</item>
/// <item>Messages makes a one-time code, writes its SHA-256 and an expiry to <c>&lt;root&gt;\Server\page-once</c>, and opens <c>https://127.0.0.1:&lt;port&gt;/login?once=&lt;code&gt;</c>
/// in the user's browser.</item>
/// <item>The page takes a code once (the file goes with it), within <see cref="Lifetime"/>, compared in fixed time, and signs that browser in as its password would.</item>
/// </list>
/// The data folder is the user's own (only that user and the system can open it), so whoever can write the file could already read everything the server keeps; the
/// code itself is never written anywhere but the browser's address, and never logged.
/// </summary>
internal static class LocalPageLink
{
    public const string PortFile = "page.json";
    public const string OnceFile = "page-once";
    /// <summary>How long a code stays good: long enough for a browser to open, short enough that one left in a history is spent.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Port(int Version, int Value);
    private sealed record Once(int Version, string Hash, long Expires);

    public static string PortPath(string dataRoot) => Path.Combine(dataRoot, "Server", PortFile);
    public static string OncePath(string dataRoot) => Path.Combine(dataRoot, "Server", OnceFile);

    /// <summary>The server records the port its page listens on.</summary>
    public static void WritePort(string dataRoot, int port) => WriteWhole(PortPath(dataRoot), JsonSerializer.SerializeToUtf8Bytes(new Port(1, port)));

    /// <summary>The port the server last said its page listens on, or null when it never did (or the file cannot be read).</summary>
    public static int? ReadPort(string dataRoot)
    {
        try
        {
            var path = PortPath(dataRoot);
            if (!File.Exists(path) || new FileInfo(path).Length > 256) return null;
            return JsonSerializer.Deserialize<Port>(File.ReadAllBytes(path)) is { Version: 1, Value: > 0 and < 65536 } port ? port.Value : null;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>
    /// A new one-time code for the page, replacing any earlier one, and the address that carries it; null when the server never said where its page is. The code is
    /// 32 random bytes in URL-safe base64.
    /// </summary>
    public static string? Issue(string dataRoot, DateTimeOffset now)
    {
        if (ReadPort(dataRoot) is not { } port) return null;
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        WriteWhole(OncePath(dataRoot), JsonSerializer.SerializeToUtf8Bytes(new Once(1, Hash(code), (now + Lifetime).ToUnixTimeMilliseconds())));
        return string.Create(CultureInfo.InvariantCulture, $"https://127.0.0.1:{port}/login?once={code}");
    }

    /// <summary>
    /// Whether <paramref name="code"/> is the one Messages made, still good. A code is taken once: the file is removed whatever the answer, so a wrong guess spends
    /// the real code too (Messages makes a new one each time the page is opened).
    /// </summary>
    public static bool TryRedeem(string dataRoot, string? code, DateTimeOffset now)
    {
        if (code is not { Length: > 0 and <= 64 }) return false;
        var path = OncePath(dataRoot);
        Once? stored;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 512) return false;
            stored = JsonSerializer.Deserialize<Once>(File.ReadAllBytes(path));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or JsonException) { stored = null; }
        try { File.Delete(path); } catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
        if (stored is not { Version: 1, Hash.Length: 64 } || now.ToUnixTimeMilliseconds() > stored.Expires) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(code)), Encoding.ASCII.GetBytes(stored.Hash));
    }

    private static string Hash(string code) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(code)));

    private static void WriteWhole(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temporary); } catch (Exception) { }
            throw;
        }
    }
}
