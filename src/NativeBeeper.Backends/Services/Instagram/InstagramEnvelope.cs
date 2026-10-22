using System.Text.Json;

namespace NativeBeeper.App.Services.Instagram;

/// <summary>
/// The format of Instagram's saved session as the helper hands it back (<c>{"version":2,"accountKey":...}</c>). The helper resumes only its own format, so a move back (SWITCHBOARD.md slice 13)
/// refuses an envelope newer than this build's helper reads: 2026-10-12, the server updates itself nightly from main and this PC only when released.
/// </summary>
internal static class InstagramEnvelope
{
    /// <summary>The format this build's helper writes and resumes (its <c>savedSessionVersion</c>; a harness check fails when the two differ).</summary>
    public const int Version = 2;

    /// <summary>The format an envelope says it is in; null when it says none (an older or a synthetic one, which no newer helper wrote).</summary>
    public static int? VersionOf(ReadOnlyMemory<byte> envelope)
    {
        try
        {
            using var document = JsonDocument.Parse(envelope);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("version", out var version)
                && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) ? number : null;
        }
        catch (JsonException) { return null; }
    }
}
