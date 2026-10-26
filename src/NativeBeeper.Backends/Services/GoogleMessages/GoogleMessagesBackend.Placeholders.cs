using NativeBeeper.Core.Backend;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.GoogleMessages;

internal sealed partial class GoogleMessagesBackend
{
    // Conversations dropped as placeholders (GoogleDirectory.IsLeftPlaceholder): Google still answers for them, so a client
    // that asks after the drop is refused rather than bringing one back. A list that carries one again lets it in.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> placeholders = new(StringComparer.Ordinal);
    private GoogleArrivalHold? arrivalHold;
    private GoogleArrivalHold ArrivalHold => LazyInitializer.EnsureInitialized(ref arrivalHold, () => new(Publish));

    // How long a new conversation's unsettled first message waits for its download to finish or move (GoogleArrivalHold).
    internal TimeSpan ArrivalHoldLimit { get; set; } = TimeSpan.FromSeconds(3);

    private bool IsPlaceholder(string chatId) => placeholders.ContainsKey(GoogleModels.Native(chatId));

    private void HoldIfUnsettled(GoogleMessage message)
    {
        if (!GoogleArrivalHold.Unsettled(message) || chats.ContainsKey(message.ChatId) || placeholders.ContainsKey(message.ChatId)) return;
        lock (listedChatIds) if (listedChatIds.Contains(message.ChatId)) return;
        if (ArrivalHold.Begin(message.ChatId) is not { } token) return;
        GoogleMessagesLog.Write($"arrival held chat={GoogleMessagesLog.Tag(message.ChatId)}");
        _ = ExpireHoldAsync(message.ChatId, token);
    }

    private async Task ExpireHoldAsync(string chatId, int token)
    {
        await Task.Delay(ArrivalHoldLimit);
        var events = ArrivalHold.Expire(chatId, token);
        if (events >= 0) GoogleMessagesLog.Write($"arrival released chat={GoogleMessagesLog.Tag(chatId)} reason=limit events={events}");
    }

    private void ReleaseIfSettled(string chatId)
    {
        if (!ArrivalHold.IsHeld(chatId) || !messages.Values.Any(message => message.ChatId == chatId && !GoogleArrivalHold.Unsettled(message))) return;
        var events = ArrivalHold.Release(chatId);
        if (events >= 0) GoogleMessagesLog.Write($"arrival released chat={GoogleMessagesLog.Tag(chatId)} reason=whole events={events}");
    }

    // A message an event moved from one conversation to another. The one it left goes when it was only a placeholder: held,
    // nothing of it was ever published; shown, clients are told it is gone.
    private void MessageMoved(string fromChatId, GoogleMessage message)
    {
        bool everListed;
        lock (listedChatIds) everListed = listedChatIds.Contains(fromChatId);
        var dropped = GoogleDirectory.IsLeftPlaceholder(fromChatId, everListed, messages.Values);
        var discarded = dropped ? ArrivalHold.Discard(fromChatId) : -1;
        GoogleMessagesLog.Write($"event=message state=moved chat={GoogleMessagesLog.Tag(message.ChatId)} from={GoogleMessagesLog.Tag(fromChatId)} message={GoogleMessagesLog.Tag(message.Id)} placeholder={(dropped ? "dropped" : "kept")}{(discarded >= 0 ? $" held_discarded={discarded}" : "")}");
        if (!dropped) { ReleaseIfSettled(fromChatId); return; }
        placeholders[fromChatId] = 0;
        if (RemoveChat(fromChatId, out var gone)) ArchiveChats([gone with { Hidden = true }]);
        if (discarded < 0) Publish(new(RealtimeKind.ChatDeleted, GoogleModels.Qualify(fromChatId)));
    }
}
