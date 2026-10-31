using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

/// <summary>What became of one push: Firebase took it, Firebase says the token is gone for good (its registration is forgotten), or it failed this time.</summary>
public enum PushOutcome { Sent, Gone, Failed }

/// <summary>How a sealed push reaches a phone: Firebase on a server set up with the owner's project; the sample server writes them down instead.</summary>
public interface IPushTransport
{
    /// <summary>Sends <paramref name="sealedPush"/> (base64) to <paramref name="token"/>; <paramref name="urgent"/> for one that shows a notification (Firebase demotes an app whose urgent pushes show nothing).</summary>
    Task<PushOutcome> SendAsync(string token, string sealedPush, bool urgent, CancellationToken cancellationToken = default);
    /// <summary>
    /// A replaceable catch-up hint: only the newest pending one is needed. Transports without a queue can send it normally. <paramref name="urgent"/> only for
    /// the one wake a burst of the owner's own sends gets (PushNotifier.BurstSpacing), since an urgent push that shows nothing counts against the app.
    /// </summary>
    Task<PushOutcome> SendSyncAsync(string token, string sealedPush, bool urgent = false, CancellationToken cancellationToken = default) =>
        SendAsync(token, sealedPush, urgent, cancellationToken);
}

/// <summary>A server's pushes: what it offers phones, and how it sends to them.</summary>
public sealed record PushSetup(PushOffer Offer, IPushTransport Transport);

/// <summary>Why two files are not a Firebase setup: not the Android app's settings, not a service account key, from two projects, or with no app for Messages in it.</summary>
public enum PushProblem { None, NotAppSettings, NotServiceAccount, ProjectsDiffer, NoApp }

/// <summary>What Google said of a service account key when the server asked it for an access token: took it, refused it, or could not be asked just now.</summary>
public enum PushKeyCheck { Accepted, Refused, Unreachable }

/// <summary>
/// Firebase Cloud Messaging's HTTP v1 API, as the owner's own project (SWITCHBOARD.md Decisions, 2026-10-15: no relay). Set up by two files the owner puts in
/// the server's push folder: <c>google-services.json</c> (the Android app's settings, which the server offers phones: none of it secret) and
/// <c>service-account.json</c> (the project's private key, which only signs the server's requests for an access token and never leaves it). Nothing of a
/// push but its sealed bytes reaches Firebase, and nothing of a token or key is logged.
/// </summary>
public sealed class FirebaseTransport : IPushTransport, IDisposable
{
    public const string AppSettingsFile = "google-services.json";
    public const string ServiceAccountFile = "service-account.json";

    /// <summary>The Android packages Messages is (the phone build and the development copy are <c>.dev</c>): a project needs an app for at least one of them.</summary>
    public static readonly IReadOnlyList<string> Packages = ["com.wrenchware.messages", "com.wrenchware.messages.dev"];
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly string projectId;
    private readonly string clientEmail;
    private readonly RSA key;
    private readonly string tokenUri;
    private readonly HttpClient http;
    private readonly Action<string> log;
    private readonly SemaphoreSlim tokenGate = new(1, 1);
    private string? accessToken;
    private DateTimeOffset accessUntil;

    private FirebaseTransport(string projectId, string clientEmail, RSA key, string tokenUri, HttpClient http, Action<string> log)
    {
        this.projectId = projectId; this.clientEmail = clientEmail; this.key = key; this.tokenUri = tokenUri; this.http = http; this.log = log;
    }

    /// <summary>
    /// The setup in <paramref name="folder"/>, or null when either file is missing (no project set up: the server offers no pushes). A file that is there but
    /// cannot be read is logged by its kind and also gives null.
    /// </summary>
    public static PushSetup? TryLoad(string folder, Action<string> log, HttpClient? http = null)
    {
        var settingsPath = Path.Combine(folder, AppSettingsFile);
        var accountPath = Path.Combine(folder, ServiceAccountFile);
        if (!File.Exists(settingsPath) || !File.Exists(accountPath)) { log("push off reason=no_project"); return null; }
        string settings, account;
        try { (settings, account) = (File.ReadAllText(settingsPath), File.ReadAllText(accountPath)); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            log($"push off reason=unreadable type={failure.GetType().Name}");
            return null;
        }
        var read = Read(settings, account, log, http);
        if (read.Setup is { } setup) log($"push on apps={setup.Offer.Apps?.Count ?? 0}");
        else log(read.Problem == PushProblem.ProjectsDiffer ? "push off reason=projects_differ" : $"push off reason=unreadable type={read.Failure}");
        return read.Setup;
    }

    /// <summary>
    /// The setup two files' text make (<see cref="AppSettingsFile"/>'s and <see cref="ServiceAccountFile"/>'s), or why they make none; <c>Failure</c> names the
    /// kind of what failed in the file that could not be read. An app for Messages is not required here (a phone of another package is refused at registration);
    /// the server's page asks for one before it keeps the files.
    /// </summary>
    public static (PushSetup? Setup, PushProblem Problem, string? Failure) Read(string settings, string account, Action<string> log, HttpClient? http = null)
    {
        PushOffer offer;
        try { offer = OfferFrom(JsonNode.Parse(settings)!); }
        catch (Exception failure) when (Unreadable(failure)) { return (null, PushProblem.NotAppSettings, failure.GetType().Name); }
        RSA? rsa = null;
        try
        {
            var key = JsonNode.Parse(account)!;
            rsa = RSA.Create();
            rsa.ImportFromPem((string)key["private_key"]!);
            // A public key imports too, and would fail only at the first signature: refused here, as not a private key.
            rsa.ExportParameters(includePrivateParameters: true);
            var project = (string)key["project_id"]!;
            var email = (string)key["client_email"]!;
            // Google's own address, or one like it: anything that is not an https address is no key file of Firebase's.
            var tokenUri = (string?)key["token_uri"] ?? "https://oauth2.googleapis.com/token";
            if (!Uri.TryCreate(tokenUri, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps) throw new FormatException("token_uri");
            if (project != offer.ProjectId) { rsa.Dispose(); return (null, PushProblem.ProjectsDiffer, null); }
            var transport = new FirebaseTransport(project, email, rsa, tokenUri, http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) }, log);
            return (new PushSetup(offer, transport), PushProblem.None, null);
        }
        catch (Exception failure) when (Unreadable(failure))
        {
            rsa?.Dispose();
            return (null, PushProblem.NotServiceAccount, failure.GetType().Name);
        }
    }

    private static bool Unreadable(Exception failure) => failure is JsonException or InvalidOperationException or NullReferenceException or CryptographicException
        or ArgumentException or FormatException or IndexOutOfRangeException;

    /// <summary>Asks Google for an access token with the key, as every push does first: whether it takes the key. Nothing is sent to a phone.</summary>
    public async Task<PushKeyCheck> CheckAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await AccessTokenAsync(cancellationToken).ConfigureAwait(false);
            return PushKeyCheck.Accepted;
        }
        // Google answers a key it does not know, or one deleted or disabled, with a 400 or 401 (invalid_grant); anything else is no answer about the key.
        catch (TokenRefusedException refused) when (refused.Status is 400 or 401 or 403) { return PushKeyCheck.Refused; }
        // The asker gave up (the page closed, the server stopping): no answer, and nothing to keep on the strength of it.
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException) { return PushKeyCheck.Unreachable; }
    }

    private sealed class TokenRefusedException(int status) : InvalidOperationException($"token status {status}")
    {
        public int Status { get; } = status;
    }

    /// <summary>What phones are offered, from the Android app's google-services.json: the project, its sender, its key, and each Android package's app.</summary>
    public static PushOffer OfferFrom(JsonNode settings)
    {
        var project = settings["project_info"]!;
        var clients = settings["client"]!.AsArray();
        var apps = clients.Select(client => new PushApp(
            (string)client!["client_info"]!["android_client_info"]!["package_name"]!,
            (string)client["client_info"]!["mobilesdk_app_id"]!)).ToList();
        var apiKey = (string?)clients.FirstOrDefault()?["api_key"]?.AsArray().FirstOrDefault()?["current_key"];
        return new PushOffer(true, (string)project["project_id"]!, (string)project["project_number"]!, apiKey, apps);
    }

    public Task<PushOutcome> SendAsync(string token, string sealedPush, bool urgent, CancellationToken cancellationToken = default) =>
        SendCoreAsync(token, sealedPush, urgent, sync: false, cancellationToken);

    public Task<PushOutcome> SendSyncAsync(string token, string sealedPush, bool urgent = false, CancellationToken cancellationToken = default) =>
        SendCoreAsync(token, sealedPush, urgent, sync: true, cancellationToken);

    // One collapse key for sync only: the latest hint causes a replay of every missed conversation. Collapsing normal read withdrawals or incoming
    // notifications too would lose the other chats' notification removals or words; a long PC send batch must not fill FCM's 100-message offline queue.
    internal static JsonObject MessageBody(string token, string sealedPush, bool urgent, bool sync = false)
    {
        var android = new JsonObject { ["priority"] = urgent ? "HIGH" : "NORMAL", ["ttl"] = "86400s" };
        if (sync) android["collapse_key"] = "messages-sync";
        return new JsonObject { ["message"] = new JsonObject {
            ["token"] = token, ["data"] = new JsonObject { ["p"] = sealedPush }, ["android"] = android,
        } };
    }

    private async Task<PushOutcome> SendCoreAsync(string token, string sealedPush, bool urgent, bool sync, CancellationToken cancellationToken)
    {
        string bearer;
        try { bearer = await AccessTokenAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException)
        {
            log($"push token failed type={failure.GetType().Name}");
            return PushOutcome.Failed;
        }
        var body = MessageBody(token, sealedPush, urgent, sync);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{projectId}/messages:send")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode) return PushOutcome.Sent;
            var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            // The token itself is no longer good (the app was uninstalled, or its data cleared): FCM says UNREGISTERED, or the token is not one.
            // SENDER_ID_MISMATCH: a token from another Firebase project (the files were replaced), which this project can never reach.
            var gone = response.StatusCode == HttpStatusCode.NotFound || text.Contains("UNREGISTERED", StringComparison.Ordinal) || text.Contains("SENDER_ID_MISMATCH", StringComparison.Ordinal) ||
                (response.StatusCode == HttpStatusCode.BadRequest && text.Contains("registration token", StringComparison.OrdinalIgnoreCase));
            if (response.StatusCode == HttpStatusCode.Unauthorized) accessToken = null;
            log($"push refused status={(int)response.StatusCode} gone={gone}");
            return gone ? PushOutcome.Gone : PushOutcome.Failed;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException)
        {
            log($"push failed type={failure.GetType().Name}");
            return PushOutcome.Failed;
        }
    }

    // An OAuth access token for the project, from a JWT the service account signs (RFC 7523), kept until five minutes before it runs out.
    private async Task<string> AccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (accessToken is { } kept && DateTimeOffset.UtcNow < accessUntil) return kept;
            var assertion = Assertion(DateTimeOffset.UtcNow);
            using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
            };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new TokenRefusedException((int)response.StatusCode);
            var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false))!;
            accessToken = (string)answer["access_token"]!;
            accessUntil = DateTimeOffset.UtcNow.AddSeconds(((int?)answer["expires_in"] ?? 3600) - 300);
            return accessToken;
        }
        finally { tokenGate.Release(); }
    }

    /// <summary>The signed JWT the token request carries: the service account, Firebase Messaging's scope, an hour from <paramref name="now"/>.</summary>
    internal string Assertion(DateTimeOffset now)
    {
        static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var issued = now.ToUnixTimeSeconds();
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { iss = clientEmail, scope = Scope, aud = tokenUri, iat = issued, exp = issued + 3600 }));
        var signature = Base64Url(key.SignData(Encoding.ASCII.GetBytes(header + "." + claims), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        return header + "." + claims + "." + signature;
    }

    public void Dispose()
    {
        key.Dispose();
        http.Dispose();
        tokenGate.Dispose();
    }
}
