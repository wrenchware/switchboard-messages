namespace NativeBeeper.Presentation.Services;

public static class ComposeTransport
{
    // These are expected routes, never a claim about an earlier message's transport.
    public static string Label(string? expected, bool hasAttachment, bool connected) => !connected ? "" : expected switch
    {
        "RCS" => "RCS",
        "SMS" or "SMS/MMS" when hasAttachment => "MMS",
        "SMS" => "SMS",
        "SMS/MMS" => "SMS/MMS",
        _ => ""
    };
    // With a photo attached the text is its caption; the route still shows when there is one.
    public static string Placeholder(string label, bool attachment) => attachment
        ? label.Length == 0 ? "Add a caption" : "Add a caption · " + label
        : Placeholder(label);
    public static string Placeholder(string label) => label switch
    {
        "RCS" => "RCS message",
        "SMS" => "Text message · SMS",
        "MMS" => "Add a message · MMS",
        "SMS/MMS" => "Text message · SMS/MMS",
        _ => "Write a message"
    };
    // Outgoing bubble color family. Unrecognized routes stay null and keep the palette color.
    public static string? BubbleFamily(string? transport) => transport switch
    {
        "RCS" => "RCS",
        "SMS" or "MMS" or "SMS/MMS" => "SMS",
        _ => null
    };
}
