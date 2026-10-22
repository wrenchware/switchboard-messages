using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Presentation.Services;

namespace NativeBeeper.Presentation.ViewModels;

public enum MessageMutationOutcome { Accepted, Rejected, Unknown }
public sealed partial class MessageActionsViewModel
{
    private readonly HashSet<(string? Profile, string Chat, string Message, string Action)> uncertainChanges = [];
    private readonly Dictionary<(string? Profile, string Chat, string Message), string> editDrafts = [];
    public bool IsDialogOpen { get; private set; }
    public bool HasEditDrafts => editDrafts.Count > 0;
    public void ClearState() { editDrafts.Clear(); uncertainChanges.Clear(); uncertainReactions.Clear(); pending.Clear(); Status = ""; }
    public bool BeginDialog() { if (IsDialogOpen || IsBusy) return false; IsDialogOpen = true; return true; }
    public void EndDialog() => IsDialogOpen = false;
    public string? SavedEdit(ChatMessage message, string? profile) => editDrafts.GetValueOrDefault((profile, message.ChatId, message.Id));
    public Task<MessageMutationOutcome> EditAsync(ChatMessage message, string text, string? profile) => ChangeAsync(message, text, null, profile);
    public Task<MessageMutationOutcome> DeleteAsync(ChatMessage message, bool everyone, string? profile) => ChangeAsync(message, null, everyone, profile);

    private async Task<MessageMutationOutcome> ChangeAsync(ChatMessage message, string? text, bool? everyone, string? profile)
    {
        var operation = (profile, message.ChatId, message.Id, everyone is null ? "edit" : everyone.Value ? "deleteEveryone" : "deleteMe");
        if (uncertainChanges.Contains(operation)) { Status = UnknownChangeStatus; return MessageMutationOutcome.Unknown; }
        if (!Available(message.ChatId) || IsBusy || mutations is null) { Status = "Reconnect before changing this message."; return MessageMutationOutcome.Rejected; }
        IsBusy = true;
        try
        {
            if (profile != await NotificationIdentity.ProfileAsync(tokens)) { Status = "Connection changed. Reopen the message menu."; return MessageMutationOutcome.Rejected; }
            if (uncertainChanges.Count >= 128 || editDrafts.Count >= 128) { Status = "Review saved changes before editing more messages."; return MessageMutationOutcome.Rejected; }
            if (text is not null)
            {
                editDrafts[(profile, message.ChatId, message.Id)] = text;
                await mutations.EditAsync(message.ChatId, message.Id, message.Text, text);
                editDrafts.Remove((profile, message.ChatId, message.Id));
            }
            else await mutations.DeleteAsync(message.ChatId, message.Id, everyone!.Value);
            // Nothing is said when a change works. The message vanishing or changing in the conversation is the report,
            // and it arrives before a bar announcing it would — leaving the bar behind, asking to be dismissed.
            Status = "";
            return MessageMutationOutcome.Accepted;
        }
        catch (SendException ex)
        {
            Status = ex.Message;
            if (ex.OutcomeUnknown) uncertainChanges.Add(operation);
            return ex.OutcomeUnknown ? MessageMutationOutcome.Unknown : MessageMutationOutcome.Rejected;
        }
        catch (Exception) { Status = "Couldn't make this change. Your edited text is kept until you close Messages."; return MessageMutationOutcome.Rejected; }
        finally { IsBusy = false; }
    }
}

