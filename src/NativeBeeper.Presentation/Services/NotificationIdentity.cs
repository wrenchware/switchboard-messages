using System.Security.Cryptography;
using System.Text;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

public static class NotificationIdentity
{
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static async Task<string?> ProfileAsync(IAccessTokenStore tokens, CancellationToken token = default) => ProfileOf(await tokens.GetAsync(token));
    public static string? ProfileOf(string? credential) => string.IsNullOrWhiteSpace(credential) ? null : Hash("desktop:127.0.0.1:23373\n" + credential);
    public static bool IsValidId(string? id) => id is { Length: 64 } && id.All(char.IsAsciiHexDigit);
}
