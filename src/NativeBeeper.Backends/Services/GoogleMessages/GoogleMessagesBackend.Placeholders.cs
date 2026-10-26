using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // Conversations dropped as placeholders (GoogleDirectory.IsLeftPlaceholder): Google still answers for them, so a client
    // that asks after the drop is refused rather than bringing one back. A list that carries one again lets it in.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> placeholders = new(StringComparer.Ordinal);

    private bool IsPlaceholder(string chatId) => placeholders.ContainsKey(GoogleModels.Native(chatId));

    // A message an event moved from one conversation to another. The one it left goes when it was only a placeholder.
    private void MessageMoved(string fromChatId, GoogleMessage message)
    {
        bool everListed;
        lock (listedChatIds) everListed = listedChatIds.Contains(fromChatId);
        var dropped = GoogleDirectory.IsLeftPlaceholder(fromChatId, everListed, messages.Values);
        GoogleMessagesLog.Write($"event=message state=moved chat={GoogleMessagesLog.Tag(message.ChatId)} from={GoogleMessagesLog.Tag(fromChatId)} message={GoogleMessagesLog.Tag(message.Id)} placeholder={(dropped ? "dropped" : "kept")}");
        if (!dropped) return;
        placeholders[fromChatId] = 0;
        if (chats.TryRemove(fromChatId, out var gone)) ArchiveChats([gone with { Hidden = true }]);
        Publish(new(RealtimeKind.ChatDeleted, GoogleModels.Qualify(fromChatId)));
    }
}
