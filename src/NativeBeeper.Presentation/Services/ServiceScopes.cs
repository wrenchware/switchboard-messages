namespace NativeBeeper.Presentation.Services;

/// <summary>
/// What a connection scope or a conversation id says about its service. Pure: the prefixes are the ones every backend
/// already qualifies its ids with. A Google Voice scope is its account's own prefix ("gvoice:&lt;account&gt;:").
/// </summary>
public static class ServiceScopes
{
    /// <summary>The service's name for a scope or a conversation id. Anything unrecognised is Google Messages, as the
    /// router treats it.</summary>
    public static string Name(string scopeOrChatId) =>
        scopeOrChatId.StartsWith("gvoice:", StringComparison.Ordinal) ? "Google Voice"
        : scopeOrChatId.StartsWith("messenger:", StringComparison.Ordinal) ? "Messenger"
        : scopeOrChatId.StartsWith("instagram:", StringComparison.Ordinal) ? "Instagram" : "Google Messages";

    /// <summary>The connection scope a conversation id (or a scope) belongs to; null when it names no known service.</summary>
    public static string? Scope(string? id)
    {
        if (id is null) return null;
        foreach (var prefix in new[] { "google-direct:", "messenger:", "instagram:" })
            if (id.StartsWith(prefix, StringComparison.Ordinal)) return prefix;
        if (!id.StartsWith("gvoice:", StringComparison.Ordinal)) return null;
        var end = id.IndexOf(':', 7);
        return end >= 0 ? id[..(end + 1)] : null;
    }
}
