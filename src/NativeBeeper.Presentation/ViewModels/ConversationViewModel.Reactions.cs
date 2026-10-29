using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    // How long a reaction shows before the service confirms it. The sample preview, with no service to confirm one, holds
    // its predictions for as long as it runs (ShellNavigationProbe).
    public static TimeSpan ReactionPredictionTimeout { get; set; } = TimeSpan.FromMinutes(1);
    private sealed record PendingReaction(string Key, bool Remove, IReadOnlyList<MessageReaction> Before, DateTimeOffset At);
    private readonly Dictionary<string, PendingReaction> pendingReactions = [];

    // Shows a reaction immediately while Google confirms it. The prediction ends when the message's reactions
    // change from their state at submission (Google's update), when submission fails, or after a minute without an update.
    public void ShowPendingReaction(ChatMessage message, string key, bool remove)
    {
        if (!ContainsChat(message.ChatId) || current is null) return;
        var at = DateTimeOffset.UtcNow;
        var actual = current.Messages.FirstOrDefault(m => m.Id == message.Id && m.ChatId == message.ChatId)?.Reactions ?? message.Reactions;
        pendingReactions[MessageIdentity.Key(message)] = new(key, remove, actual, at);
        RenderLive();
        _ = ExpirePendingReactionAsync(MessageIdentity.Key(message), at);
    }

    public void ClearPendingReaction(string messageId)
    {
        var keys = current?.Messages.Where(m => m.Id == messageId).Select(MessageIdentity.Key).Append(messageId).ToArray() ?? [messageId];
        var changed = false;
        foreach (var key in keys) changed |= pendingReactions.Remove(key);
        if (changed && current is not null) RenderLive();
    }
    public void ClearPendingReaction(ChatMessage message) => ClearPendingReaction(MessageIdentity.Key(message));

    private async Task ExpirePendingReactionAsync(string messageId, DateTimeOffset at)
    {
        await Task.Delay(ReactionPredictionTimeout + TimeSpan.FromSeconds(1));
        if (pendingReactions.TryGetValue(messageId, out var pending) && pending.At == at) ClearPendingReaction(messageId);
    }

    private ChatMessage WithPendingReaction(ChatMessage message)
    {
        if (!pendingReactions.TryGetValue(MessageIdentity.Key(message), out var pending)) return message;
        if (!message.Reactions.SequenceEqual(pending.Before) || DateTimeOffset.UtcNow - pending.At > ReactionPredictionTimeout)
        {
            pendingReactions.Remove(MessageIdentity.Key(message));
            return message;
        }
        var self = SourceChat(message.ChatId)?.Participants.Where(p => p.IsSelf == true).Select(p => p.Id).ToArray() ?? [];
        return message with { Reactions = ReactionPrediction.Apply(message.Reactions, pending.Key, pending.Remove, self) };
    }
}
