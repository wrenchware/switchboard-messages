namespace NativeBeeper.Core.Services;

public sealed record MessageActionOptions(bool CanReply, IReadOnlyList<string> AddReactions, IReadOnlyList<string> RemoveReactions,
    bool CanEdit = false, bool CanDeleteForMe = false, bool CanDeleteForEveryone = false, int MaxEditLength = 65536);
public interface IMessageActions
{
    bool HasLocalActionOptions => false;
    Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default);
    Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default);
    Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default);
}
