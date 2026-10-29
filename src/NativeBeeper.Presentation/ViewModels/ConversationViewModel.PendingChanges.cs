using NativeBeeper.Core.Messaging;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public sealed partial class ConversationViewModel
{
    // How long an edit or a delete shows before the service's copy confirms it; after that the conversation draws the service's copy again.
    public static TimeSpan ChangePredictionTimeout { get; set; } = TimeSpan.FromMinutes(1);
    private sealed record PendingChange(string? Text, bool? Everyone, string? BeforeText, bool BeforeDeleted, DateTimeOffset At);
    private readonly Dictionary<string, PendingChange> pendingChanges = [];

    /// <summary>
    /// Shows an edit (<paramref name="text"/>) or a delete (<paramref name="everyone"/>) at once, as the reactions are shown, while the service makes it. A delete
    /// for this account takes the message away; one for everyone draws it as deleted, which is how a service that keeps the row shows it too. The prediction
    /// ends when the service's copy of the message changes from what it was (or the message is gone), when the change is refused, or after a minute.
    /// </summary>
    public void ShowPendingChange(ChatMessage message, string? text, bool? everyone)
    {
        if (!ContainsChat(message.ChatId) || current is null) return;
        var at = DateTimeOffset.UtcNow;
        var actual = current.Messages.FirstOrDefault(m => m.Id == message.Id && m.ChatId == message.ChatId) ?? message;
        pendingChanges[MessageIdentity.Key(message)] = new(text, everyone, actual.Text, actual.IsDeleted, at);
        RenderLive();
        _ = ExpirePendingChangeAsync(MessageIdentity.Key(message), at);
    }

    public void ClearPendingChange(ChatMessage message)
    {
        if (pendingChanges.Remove(MessageIdentity.Key(message)) && current is not null) RenderLive();
    }

    private async Task ExpirePendingChangeAsync(string key, DateTimeOffset at)
    {
        await Task.Delay(ChangePredictionTimeout + TimeSpan.FromSeconds(1));
        if (pendingChanges.TryGetValue(key, out var pending) && pending.At == at && pendingChanges.Remove(key) && current is not null) RenderLive();
    }

    // Null for a message deleted here for this account alone, which is not drawn while the service removes it.
    private ChatMessage? WithPendingChange(ChatMessage message)
    {
        if (pendingChanges.Count == 0 || !pendingChanges.TryGetValue(MessageIdentity.Key(message), out var pending)) return message;
        if (message.Text != pending.BeforeText || message.IsDeleted != pending.BeforeDeleted || DateTimeOffset.UtcNow - pending.At > ChangePredictionTimeout)
        {
            pendingChanges.Remove(MessageIdentity.Key(message));
            return message;
        }
        return pending switch
        {
            { Text: { } text } => message with { Text = text, EditedAt = message.EditedAt ?? pending.At },
            { Everyone: true } => message with { IsDeleted = true },
            _ => null,
        };
    }
}
