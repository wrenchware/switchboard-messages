using System.Text.Json;

namespace NativeBeeper.App.Services.Messenger;

/// <summary>
/// One page of Messenger's retained history, as the helper exported it. The format and its fields are documented at
/// the top of backup.go in the Messenger helper.
/// </summary>
/// <remarks>
/// <see cref="Raw"/> is the page itself, kept whole so it can be written to a backup and handed back to the helper
/// unchanged; the other members are read out of it for the caller's bookkeeping. Next names the page after this one
/// and is null on the last.
/// </remarks>
internal sealed record MessengerHistoryPage(JsonElement Raw, string Account, string Export, int Number, string? Next)
{
    public const string Format = "nativebeeper.messenger.history";
    public const int Version = 1;

    public bool IsLast => Next is null;

    // Null for anything that is not a page of the one format and version this build reads, a newer one included, so
    // nothing unreadable is written to a backup or offered back to the helper.
    public static MessengerHistoryPage? Read(JsonElement page)
    {
        if (page.ValueKind != JsonValueKind.Object) return null;
        if (Text(page, "format") != Format) return null;
        if (!page.TryGetProperty("version", out var version) || !version.TryGetInt32(out var read) || read != Version) return null;
        var account = Text(page, "account");
        var export = Text(page, "export");
        var next = Text(page, "next");
        if (account is not { Length: 32 } || !account.All(char.IsAsciiHexDigitLower) || string.IsNullOrEmpty(export) || next is null) return null;
        if (!page.TryGetProperty("page", out var number) || !number.TryGetInt32(out var sequence) || sequence < 0) return null;
        return new(page.Clone(), account, export, sequence, next.Length == 0 ? null : next);
    }

    private static string? Text(JsonElement page, string name) =>
        page.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

/// <summary>What one imported page did to one kind of row.</summary>
/// <remarks>
/// Added was written anew; Skipped was here already; Refused was kept out because this PC deleted it since, or it
/// had nothing to hang on; Removed was taken out here because the backup knew it had been deleted.
/// </remarks>
internal sealed record MessengerHistoryCounts(int Added, int Skipped, int Refused, int Removed);

/// <summary>
/// What one imported page did, in total and by kind: contacts, threads, mappings, deletions, messages, media, shares,
/// attachments and reactions. Cutoffs counts conversations whose later deletion the backup applied here.
/// </summary>
internal sealed record MessengerHistoryImport(int Page, int Added, int Skipped, int Refused, int Removed, int Cutoffs,
    IReadOnlyDictionary<string, MessengerHistoryCounts> Sections)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public static MessengerHistoryImport Read(JsonElement result) =>
        result.Deserialize<MessengerHistoryImport>(Json) is { Sections: not null } read ? read
            : throw new InvalidOperationException("backup_failed");
}
