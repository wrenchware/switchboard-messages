namespace NativeBeeper.Presentation.Services;

// What a recording calls itself before anybody plays it.
//
// A player shows the running time once it is playing, so this is only ever read while it sits there. A voicemail says
// so. Anything else shows its file name, which is useful when somebody attached a recording they had — and useless
// when the provider's "name" is one of its own identifiers, which is what Google Messages sends for an audio message:
// a bare GUID, drawn where a phone would have shown the length of the recording. A name nobody wrote is not a name.
public static class RecordingLabel
{
    public const string Voicemail = "Voicemail", Recording = "Audio message";

    public static string Describe(string? name, bool isVoiceNote)
    {
        if (isVoiceNote) return Voicemail;
        var bare = string.IsNullOrWhiteSpace(name) ? "" : Path.GetFileNameWithoutExtension(name).Trim();
        return bare.Length == 0 || IsIdentifier(bare) ? Recording : bare;
    }

    // Hexadecimal and separators, and long enough that nobody typed it: a GUID with or without its dashes, or the
    // same idea in another shape. Short hex words a person might actually have named a file are left alone.
    private static bool IsIdentifier(string value)
    {
        if (value.Length < 16) return false;
        var digits = 0;
        foreach (var character in value)
        {
            if (character is '-' or '_') continue;
            if (!Uri.IsHexDigit(character)) return false;
            digits++;
        }
        return digits >= 16;
    }
}
