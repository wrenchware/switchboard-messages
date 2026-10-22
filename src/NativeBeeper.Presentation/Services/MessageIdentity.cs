using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class MessageIdentity
{
    // Length framing remains unambiguous even when a provider uses punctuation in its IDs.
    public static string Key(ChatMessage message) => Key(message.ChatId, message.Id);
    public static string Key(string chatId, string messageId) => $"{chatId.Length}:{chatId}{messageId}";
}
