using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;

namespace NativeBeeper.App.Services.Messenger;

internal enum TypingProvider { None, Google, Messenger, Instagram }

/// <summary>
/// Which provider this account's typing in a conversation goes to, by who owns the conversation's id. Anything nobody
/// owns goes nowhere: an Instagram conversation used to fall through to Google Messages, whose id parsing threw on
/// every typing burst.
/// </summary>
internal static class TypingRoute
{
    public static TypingProvider Of(string chatId) =>
        GoogleVoiceModels.Owns(chatId) ? TypingProvider.None // Google Voice sends no typing.
        : InstagramIdentity.OwnsAny(chatId) ? TypingProvider.Instagram
        : MessengerModels.Owns(chatId) ? TypingProvider.Messenger
        : GoogleModels.Owns(chatId) ? TypingProvider.Google
        : TypingProvider.None;
}
