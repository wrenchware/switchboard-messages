using System.Text.RegularExpressions;

namespace NativeBeeper.Presentation.Services;

public static partial class ChatPreviewText
{
    // Google's reaction previews look like: 😂 to " Unfortunate looking ".
    [GeneratedRegex("^(?<emoji>\\S{1,16}) to [\"“]\\s*(?<text>.*?)\\s*[\"”]$")]
    private static partial Regex Reaction();

    public static string Format(bool outgoing, string text)
    {
        var match = Reaction().Match(text);
        if (match.Success && !match.Groups["emoji"].Value.Any(char.IsLetterOrDigit))
            return (outgoing ? "You reacted " : "Reacted ") + match.Groups["emoji"].Value + " to “" + match.Groups["text"].Value + "”";
        return (outgoing ? "You: " : "") + text;
    }
}
