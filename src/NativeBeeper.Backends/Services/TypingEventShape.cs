using System.Text.Json;

namespace NativeBeeper.App.Services;

// Allowlisted names and JSON kinds only. Never retain values, arbitrary keys or identifiers.
internal sealed class TypingEventShape
{
    private static readonly HashSet<string> Names = new(StringComparer.Ordinal)
    {
        "type", "seq", "ts", "chatID", "ids", "entries", "id", "accountID", "network",
        "title", "participants", "items", "senderID", "text", "timestamp", "reactions",
        "attachments", "seen", "sendStatus", "unreadCount", "lastActivity", "lastMessage",
        "capabilities", "typingNotifications", "typing", "isTyping", "typingUsers",
        "typingUserIDs", "typingParticipants", "ephemeral", "m.typing", "user_ids",
        "content", "events", "state", "timeout", "expiresAt", "duration", "status"
    };
    private readonly Dictionary<string, int> shapes = new(StringComparer.Ordinal);
    public void Add(JsonElement root)
    {
        var type = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        var category = type is "ready" or "subscriptions.updated" or "error" or "chat.upserted" or "chat.deleted" or "message.upserted" or "message.deleted" ? type : "unrecognized";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, category, 0, seen);
        foreach (var shape in seen)
            if (shapes.Count < 1000 || shapes.ContainsKey(shape)) shapes[shape] = shapes.GetValueOrDefault(shape) + 1;
    }
    private static void Walk(JsonElement node, string path, int depth, HashSet<string> seen)
    {
        if (depth > 10 || seen.Count >= 1000) return;
        seen.Add(path + ": " + node.ValueKind);
        if (node.ValueKind == JsonValueKind.Object)
            foreach (var property in node.EnumerateObject().Take(100))
                Walk(property.Value, path + "." + (Names.Contains(property.Name) ? property.Name : "[other-key]"), depth + 1, seen);
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray().Take(20)) Walk(item, path + "[]", depth + 1, seen);
    }
    public string Report() => "Sanitized event structure; counts are events containing each shape. No scalar values or arbitrary property names retained.\nTraversal limited to depth 10, 100 properties/object, 20 items/array, 1000 shapes. Absence is not proof. typingNotifications is a capability, not live state.\n\n" +
        string.Join("\n", shapes.OrderBy(p => p.Key).Select(p => $"{p.Value} × {p.Key}"));
}
