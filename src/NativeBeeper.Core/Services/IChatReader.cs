using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Core.Services;

public interface IChatReader
{
    Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default);
    Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default);
    Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default);
}
