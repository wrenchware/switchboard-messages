namespace NativeBeeper.Core.Services;

public interface IAccessTokenStore
{
    ValueTask<string?> GetAsync(CancellationToken cancellationToken = default);
}
