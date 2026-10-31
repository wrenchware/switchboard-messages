using System.Text.Json;
using System.Text.Json.Serialization;

namespace NativeBeeper.Host.Network;

/// <summary>
/// What the server's tailnet door last reported for the server's own page (SWITCHBOARD.md 4.7; experiments/Tailnet/door/page.go): its state, the sign-in link while
/// it needs one, its name and address, the owner it lets in, key expiry, Tailnet Lock, and what the tailnet's access rules let reach it. Held in memory for the page,
/// which only its signed-in owner sees from home, and never logged: the link decides whose tailnet the server joins, and the owner's login is a name.
/// </summary>
/// <param name="State">Tailscale's own word: <c>NeedsLogin</c>, <c>Starting</c>, <c>Running</c> and so on.</param>
/// <param name="Expiry"><c>off</c>, or when the node's key expires (RFC 3339).</param>
/// <param name="Lock"><c>off</c>, <c>signed</c>, <c>unsigned</c> (then <paramref name="NodeKey"/> is the key to sign) or <c>unknown</c>.</param>
/// <param name="Rules"><c>yours</c> (only the owner's own devices may reach the server's port), <c>open</c> (every device may, <paramref name="Everyone"/>, or
/// <paramref name="Others"/> devices that are not the owner's), <c>none</c>, or <c>unknown</c>.</param>
internal sealed record TailnetView(string State, string? Login = null, string? Name = null, string? DnsName = null, string? Address = null, string? Owner = null,
    bool Tagged = false, string? Expiry = null, string Lock = "unknown", string? NodeKey = null, string Rules = "unknown", int Others = 0, bool Everyone = false)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip };

    /// <summary>The door's report (the JSON after <c>tailnet page </c>), or null when it is not one.</summary>
    public static TailnetView? Parse(string json)
    {
        try { return JsonSerializer.Deserialize<TailnetView>(json, Json) is { State.Length: > 0 } view ? view : null; }
        catch (JsonException) { return null; }
    }

    [JsonIgnore] public bool Running => State == "Running";
    [JsonIgnore] public bool NeedsLogin => State == "NeedsLogin" && Login is { Length: > 0 };
    [JsonIgnore] public bool ExpiryOff => Expiry == "off";

    /// <summary>Whether the owner has something to do: sign it in, sign it for Tailnet Lock, turn key expiry off, or let their devices reach it at all.</summary>
    [JsonIgnore] public bool NeedsAttention => NeedsLogin || Running && (Lock == "unsigned" || !ExpiryOff && Expiry is not null || Rules == "none");
}

/// <summary>
/// Where the door's reports wait for the page: made before either (the network host starts first), given to both. <see cref="Enabled"/> says whether the server
/// was started with its tailnet door (<c>MESSAGES_TAILNET=1</c>); a server without it shows the page how to turn it on.
/// </summary>
internal sealed class TailnetBoard(bool enabled, int port = NativeBeeper.Protocol.NetworkEndpoint.DefaultPort)
{
    private volatile TailnetView? latest;

    public bool Enabled { get; } = enabled;

    /// <summary>The port devices reach the server on through the door.</summary>
    public int Port { get; } = port;

    /// <summary>The last report, or null before the door's first.</summary>
    public TailnetView? Latest => latest;

    public void Set(TailnetView? view) => latest = view;
}
