using NativeBeeper.Core.Services;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.DataProtection;
namespace NativeBeeper.WindowsPlatform;
public sealed class WindowsStateProtector : IStateProtector
{
    public async Task<byte[]> ProtectAsync(byte[] value)
    {
        var buffer = await new DataProtectionProvider("LOCAL=user").ProtectAsync(CryptographicBuffer.CreateFromByteArray(value));
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes); return bytes;
    }
    public async Task<byte[]> UnprotectAsync(byte[] value)
    {
        var buffer = await new DataProtectionProvider().UnprotectAsync(CryptographicBuffer.CreateFromByteArray(value));
        CryptographicBuffer.CopyToByteArray(buffer, out var bytes); return bytes;
    }
}
