using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Presentation.Services;

public static class ConversationHeaderText
{
    // Direct chats show the other person's number, groups their size; then the expected route.
    public static string Subtitle(ChatSummary chat, string? transport)
    {
        var parts = new List<string>();
        if (chat.MemberChatIds.Count > 0) parts.Add($"{chat.MemberChatIds.Count} conversations");
        else if (chat.Type == "group") parts.Add($"{Math.Max(chat.ParticipantCount, chat.Participants.Count)} people");
        else if (DirectNumber(chat) is { } phone && phone != chat.Title) parts.Add(phone);
        if (chat.Network.Equals("Messenger", StringComparison.OrdinalIgnoreCase)) parts.Add("Messenger");
        if (transport is { Length: > 0 } && !parts.Contains(transport, StringComparer.OrdinalIgnoreCase)) parts.Add(transport);
        if (chat.IsReadOnly) parts.Add("Read-only");
        return string.Join(" · ", parts);
    }

    // A merged conversation's header: how many conversations it links, and their services by name for the tooltip, as
    // Details lists them. It used to count distinct networks, so a second conversation on a service already linked (another
    // account, or the person's other number) was listed in Details but left the header a number short. A Voice person's
    // texts, calls and voicemail on one account are one conversation there and one here.
    public static (string Subtitle, string Detail) Merged(IReadOnlyList<ChatSummary> members, Func<ChatSummary, string> serviceLabel, string? onlyLabel)
    {
        var count = members.Select(LinkedKey).Distinct(StringComparer.Ordinal).Count();
        var linked = $"{count} linked {(count == 1 ? "conversation" : "conversations")}";
        return (onlyLabel is null ? linked : $"{onlyLabel} only · {linked}", string.Join(" · ", members.Select(serviceLabel).Distinct()));
    }
    private static string LinkedKey(ChatSummary chat)
    {
        if (!chat.Network.Equals("Google Voice", StringComparison.OrdinalIgnoreCase)) return "chat|" + chat.Id;
        var people = chat.Participants.Where(p => p.IsSelf != true).Select(p => p.Id).Order(StringComparer.Ordinal).ToArray();
        return people.Length == 0 ? "chat|" + chat.Id : "voice|" + chat.AccountId + "|" + string.Join(",", people);
    }

    // The counterpart's number on its own, for copying it. The subtitle joins it with the transport and the read-only
    // mark, so reading the number back out of that string would be parsing our own presentation.
    public static string? Number(ChatSummary chat) =>
        chat.MemberChatIds.Count > 0 || chat.Type == "group" ? null : DirectNumber(chat);

    // A direct conversation is not always one participant row: Google Messages lists hidden rows and can
    // repeat a person under a second identity, so requiring exactly one row left an RCS header with no
    // number at all. A provider-supplied number decides when there is one; the identifier is only a
    // fallback for providers that supply none. Rows that disagree stay unprinted rather than guessed.
    private static string? DirectNumber(ChatSummary chat)
    {
        var others = chat.Participants.Where(p => p.IsSelf != true).ToArray();
        return Only(others.Select(p => Phone(p.Phone))) ?? Only(others.Select(p => Phone(p.Id)));
        static string? Only(IEnumerable<string?> values)
            => values.OfType<string>().Distinct(StringComparer.Ordinal).ToArray() is [var single] ? single : null;
    }

    // North American numbers read as (202) 555-0147; other numbers are shown as provided.
    public static string? Phone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsLetter)) return null;
        var trimmed = value.Trim();
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        // An account id is all digits too, and far longer than any number written without a country code. Printing one
        // under a conversation title reads as a phone number, which it is not.
        if (!trimmed.StartsWith('+') && digits.Length > 11) return null;
        if (digits.Length == 11 && digits[0] == '1' && (trimmed.StartsWith("+1", StringComparison.Ordinal) || !trimmed.StartsWith('+'))) digits = digits[1..];
        else if (trimmed.StartsWith('+')) return trimmed;
        return digits.Length == 10 ? $"({digits[..3]}) {digits[3..6]}-{digits[6..]}" : trimmed;
    }
}
