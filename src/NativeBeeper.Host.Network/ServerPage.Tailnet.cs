using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host.Network;

// Tailscale on the server's page (SWITCHBOARD.md 4.7 and 10.8, D9): the door set up from here rather than from the container's log. Signing the server in (a button
// and the same link as a QR code, for signing in on a phone), signing in again when Tailscale signed it out, approving it, and a checklist of what is left, each line
// in plain words with a link to the exact page of Tailscale's admin console and a tick once the server sees it done: key expiry off for the server and each Messages
// phone, only the owner's devices reaching it, and Tailnet Lock when the tailnet uses it. "Fix it for me" does what the API can with an OAuth client the owner gives
// once (TailscaleAdmin); "Use another Tailscale account" signs the door out and forgets it. Only the signed-in owner sees this, from home, so the sign-in link and the
// owner's login are shown here and nowhere else.
internal sealed partial class ServerPage
{
    private const string TailscaleMachines = "https://login.tailscale.com/admin/machines";
    private const string TailscaleAccessControls = "https://login.tailscale.com/admin/acls/file";
    private const string TailscaleLock = "https://login.tailscale.com/admin/settings/tailnet-lock";

    private static bool IsTailnetPath(string? path) => path is "/tailnet/signout" or "/tailnet/oauth" or "/tailnet/oauth/remove" or "/tailnet/fix";

    // Whether the page should look again soon: the door is starting, or waits to be signed in or approved.
    private bool TailnetWaiting => tailnet is { Enabled: true } board && board.Latest is not { Running: true };

    private static string? TailnetNotice(string done) => done switch
    {
        "tailnet_signed_out" => "The server signed out of Tailscale and forgot its machine there. Sign it in below with the account you want. The old machine stays in Tailscale's list until you remove it.",
        "tailnet_no_door" => "Tailscale isn't running on the server just now, so nothing was signed out. Try again in a minute.",
        "tailnet_oauth_saved" => "The OAuth client is kept on the server. Choose Fix it for me to use it.",
        "tailnet_oauth_removed" => "The OAuth client was removed from the server. Delete it in Tailscale too if you no longer need it.",
        _ => null,
    };

    // The machine's own page in Tailscale's console, by its address.
    private static string MachinePage(string? address) => TailscaleMachines + (address is { Length: > 0 } ip ? "/" + Uri.EscapeDataString(ip) : "");

    // The card: how to turn the door on, its sign-in while it needs one, then what is done and what is left.
    private void TailnetCard(StringBuilder body, TailnetBoard board, string token)
    {
        if (!board.Enabled)
        {
            body.Append("<section id=\"tailnet\"><h2>Away from home</h2>")
                .Append(Para("Messages on a phone reaches this server through Tailscale, so it works anywhere without opening the server to the internet."))
                .Append(Para("To use it, turn it on where this server was installed, then start the server again: on Unraid, set Reach from your tailnet to 1 in the container's settings; "
                    + "with Docker Compose, MESSAGES_TAILNET=1 in the .env file; on Linux or Proxmox, MESSAGES_TAILNET=1 in /etc/messages-server/environment. This page then walks you through the rest."))
                .Append("</section>");
            return;
        }
        var view = board.Latest;
        if (view is null)
        {
            body.Append("<section id=\"tailnet\"><h2>Tailscale</h2>").Append(Para("Starting Tailscale…")).Append("</section>");
            return;
        }
        body.Append(view.NeedsAttention ? "<section class=\"attention\" id=\"tailnet\">" : "<section id=\"tailnet\">").Append("<h2>Tailscale</h2>");
        if (view.State == "NeedsLogin")
        {
            SignInPart(body, view);
            body.Append("</section>");
            return;
        }
        if (view.NeedsApproval)
        {
            body.Append("<ul class=\"rows\">");
            Step(body, false, "Approve the server", "Your Tailscale network asks for each new machine to be approved. Open it in Tailscale and choose Approve.",
                Link(MachinePage(view.Address), "Open in Tailscale"));
            body.Append("</ul>");
            FixPart(body, token, view);
            body.Append("<p class=\"quiet\">This page moves on by itself once it's approved.</p></section>");
            return;
        }
        if (!view.Running)
        {
            body.Append(Para(view.State == "Starting" ? "Connecting to Tailscale…" : "Tailscale isn't connected just now. The server keeps trying.")).Append("</section>");
            return;
        }
        var name = view.Name is { Length: > 0 } n ? n : "messages-server";
        body.Append("<dl>");
        Row(body, "Name", board.Port == NetworkEndpoint.DefaultPort ? name : $"{name}:{board.Port}", copy: true);
        if (view.Address is { Length: > 0 } address) Row(body, "Address", address, copy: true);
        body.Append("</dl><ul class=\"rows\">");

        if (view.Tagged)
            Step(body, false, "Tagged", "Tagged in Tailscale, so the server can't tell your devices from anyone else's: your access rules decide alone who reaches it.");
        else
            Step(body, true, "Signed in", view.Owner is { Length: > 0 } owner ? $"As {owner}. The server lets in only this account's devices." : "The server lets in only your own devices.");

        if (view.ExpiryOff) Step(body, true, "Server stays signed in", "Its key expiry is off.");
        else if (view.Expiry is { } expiry)
            Step(body, false, "Keep the server signed in", $"Tailscale signs the server out{Until(expiry)} unless its key expiry is off. Open it in Tailscale, then choose Disable key expiry from its ⋯ menu.",
                Link(MachinePage(view.Address), "Open in Tailscale"));

        foreach (var phone in view.Phones ?? [])
        {
            if (phone.ExpiryOff) Step(body, true, $"{phone.Name} stays signed in", "Its key expiry is off.");
            else
                Step(body, false, $"Keep {phone.Name} signed in", $"Tailscale signs this phone out{Until(phone.Expiry)} unless its key expiry is off. Open it in Tailscale, then choose Disable key expiry from its ⋯ menu.",
                    Link(MachinePage(phone.Address), "Open in Tailscale"));
        }

        var rule = Html($"\"grants\": [\n  {{ \"src\": [\"{view.Owner ?? "you@example.com"}\"], \"dst\": [\"{view.Address ?? "100.x.y.z"}\"], \"ip\": [\"tcp:{board.Port}\"] }}\n]");
        var shown = "<details><summary>Show the rule</summary><pre class=\"copy\">" + rule + "</pre></details>" + Link(TailscaleAccessControls, "Open access controls");
        switch (view.Rules)
        {
            case "yours": Step(body, true, "Only your devices reach it", "Your access rules allow only your devices, and the server checks each one too."); break;
            case "open" when view.OnlyYours:
                Step(body, true, "Only your devices reach it", "Your Tailscale network holds only your own devices, so its rule that lets every device reach every other reaches only yours. "
                    + "If you share your network with someone later, add the rule below and take that one out.", shown);
                break;
            case "open":
                // The server's own check is the fence that matters; the rule is a second one.
                var who = view.Everyone ? "every device on your Tailscale network" : view.Others == 1 ? "1 device that isn't yours" : $"{view.Others} devices that aren't yours";
                Step(body, false, "Only your devices reach it", $"The server turns away anything that isn't one of your devices, but your access rules let {who} reach it as well. Add this rule to your access controls and take out the one that lets everyone in.",
                    shown);
                break;
            case "none":
                Step(body, false, "Let your devices reach it", "Your access rules let no device reach the server, your phone included. Add this rule to your access controls.", shown);
                break;
            default: Step(body, true, "Only your devices reach it", "The server checks each one."); break;
        }

        switch (view.Lock)
        {
            case "signed": Step(body, true, "Trusted by Tailnet Lock", "Your network's Tailnet Lock has signed it."); break;
            case "unsigned":
                Step(body, false, "Sign it for Tailnet Lock", "Your network uses Tailnet Lock, so your devices can't reach the server until one of your signing devices signs it.",
                    view.NodeKey is { Length: > 0 } key
                        ? "<details><summary>Show the command</summary><span class=\"copy block\">tailscale lock sign " + Html(key) + "</span></details>" + Link(TailscaleLock, "Open Tailnet Lock")
                        : Link(TailscaleLock, "Open Tailnet Lock"));
                break;
        }
        body.Append("</ul>");
        if (!view.AllDone) FixPart(body, token, view);
        body.Append("<p class=\"quiet\">A phone joins with the code from Add a phone, which names this server. ")
            .Append("<a href=\"/tailnet/signout\">Use another Tailscale account…</a></p></section>");
    }

    private static string Until(string? expiry) =>
        DateTimeOffset.TryParse(expiry, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? " on " + at.ToString("d MMM yyyy", CultureInfo.InvariantCulture) : "";

    // Signing in, or in again: the button, the same link as a QR code for a phone, and the word that it moves on by itself.
    private static void SignInPart(StringBuilder body, TailnetView view)
    {
        if (!view.NeedsLogin)
        {
            body.Append(Para(view.SignedOut ? "Tailscale signed the server out. Asking Tailscale for a new sign-in link…" : "Asking Tailscale for a sign-in link…"));
            return;
        }
        body.Append("<div class=\"qr-card\"><div class=\"qr small\">").Append(QrCode.Encode(view.Login!).ToSvg(label: "The sign-in link as a QR code")).Append("</div><div class=\"grow\">");
        body.Append(view.SignedOut
            ? Para("Tailscale signed the server out, so your phone can't reach it away from home. Sign it in again with your Tailscale account.")
            : Para("Sign the server in to Tailscale with your own account. It joins your Tailscale network as a machine of its own, and only your devices can reach it."));
        body.Append("<p><a class=\"button\" href=\"").Append(Html(view.Login)).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">")
            .Append(view.SignedOut ? "Sign in again" : "Sign in to Tailscale").Append("</a></p>")
            .Append("<p class=\"quiet\">Or scan the code to sign in on your phone. This page moves on by itself once you have.</p></div></div>");
    }

    // "Fix it for me": the OAuth client asked for once, then a button that does every step the API can.
    private void FixPart(StringBuilder body, string token, TailnetView view)
    {
        if (tailscale is null) return;
        body.Append("<div class=\"fix\"><h3>Fix it for me</h3>");
        if (!tailscale.Has)
        {
            body.Append(Para("The server can do these steps itself with a Tailscale OAuth client: turn off key expiry for itself and your Messages phones, approve them if your network asks for that, and add the access rule. It never signs for Tailnet Lock."))
                .Append("<form method=\"get\" action=\"/tailnet/oauth\"><button type=\"submit\" class=\"quiet\">Add an OAuth client…</button></form>");
        }
        else
        {
            body.Append(Form("/tailnet/fix", token, "Fix it for me"))
                .Append("<form method=\"post\" action=\"/tailnet/oauth/remove\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token))
                .Append("\"><button type=\"submit\" class=\"quiet\">Remove the OAuth client</button></form>");
        }
        body.Append("</div>");
    }

    private async Task TailnetAsync(HttpContext context, string path, IFormCollection? form, string token)
    {
        var post = form is not null;
        switch (path)
        {
            case "/tailnet/signout":
            {
                string Ask(string? problem) => AskBody("/tailnet/signout", token,
                    "Sign the server out of Tailscale and forget its machine? Your phones can't reach it away from home until it's signed in again, with this account or another. "
                    + "Nothing else on the server changes.", "Sign out of Tailscale", problem, danger: true);
                if (!post)
                {
                    await PageAsync(context, StatusCodes.Status200OK, "Use another Tailscale account", Ask(null)).ConfigureAwait(false);
                    return;
                }
                if (!await PasswordAgainAsync(context, form!, "tailnet_sign_out", "Use another Tailscale account", Ask).ConfigureAwait(false)) return;
                var asked = tailnet?.SignOut?.Invoke() == true;
                log($"server page tailnet sign_out outcome={(asked ? "asked" : "no_door")}");
                Redirect(context.Response, asked ? "/?done=tailnet_signed_out" : "/?done=tailnet_no_door");
                return;
            }
            case "/tailnet/oauth" when tailscale is not null:
            {
                string Ask(string? problem)
                {
                    var page = new StringBuilder("<section>");
                    if (problem is not null) page.Append(Notice(problem));
                    page.Append(Para("Make an OAuth client in Tailscale and paste its ID and secret here once. The server keeps them sealed with its own keys, never in a backup, and you can remove them here at any time."))
                        .Append("<ol class=\"steps\"><li><a href=\"").Append(TailscaleAdmin.OAuthPage).Append("\" target=\"_blank\" rel=\"noopener noreferrer\">Open OAuth clients in Tailscale</a> and choose Generate OAuth client.</li>")
                        .Append("<li>Give it two scopes, both with Write: <strong>Devices: Core</strong> (to turn off key expiry and approve machines) and <strong>Policy File</strong> (to add the access rule).</li>")
                        .Append("<li>Choose Generate client, then copy the client ID and the secret. Tailscale shows the secret only once.</li></ol>")
                        .Append("<form method=\"post\" action=\"/tailnet/oauth\"><input type=\"hidden\" name=\"form\" value=\"").Append(Html(token)).Append("\">")
                        .Append("<label for=\"client\">Client ID</label><input id=\"client\" name=\"client\" type=\"text\" autocomplete=\"off\" spellcheck=\"false\" required>")
                        .Append("<label for=\"secret\">Client secret</label><input id=\"secret\" name=\"secret\" type=\"password\" autocomplete=\"off\" required>")
                        .Append("<label for=\"password\">The server's password</label><input id=\"password\" name=\"password\" type=\"password\" autocomplete=\"current-password\" required>")
                        .Append("<button type=\"submit\">Keep it</button><a class=\"cancel\" href=\"/\">Cancel</a></form>");
                    return page.Append("</section>").ToString();
                }
                const string title = "Tailscale OAuth client";
                if (!post)
                {
                    await PageAsync(context, StatusCodes.Status200OK, title, Ask(null)).ConfigureAwait(false);
                    return;
                }
                if (!await PasswordAgainAsync(context, form!, "tailnet_oauth", title, Ask).ConfigureAwait(false)) return;
                var (client, secret) = (form!["client"].ToString(), form["secret"].ToString());
                if (!TailscaleAdmin.LooksRight(client, secret))
                {
                    await PageAsync(context, StatusCodes.Status400BadRequest, title, Ask("That doesn't look like an OAuth client. The secret begins with tskey-client-.")).ConfigureAwait(false);
                    return;
                }
                if (!await tailscale.SaveAsync(client, secret).ConfigureAwait(false))
                {
                    await PageAsync(context, StatusCodes.Status500InternalServerError, title, Ask("The server couldn't keep it. Try again.")).ConfigureAwait(false);
                    return;
                }
                Redirect(context.Response, "/?done=tailnet_oauth_saved#tailnet");
                return;
            }
            case "/tailnet/oauth/remove" when tailscale is not null && post:
                tailscale.Remove();
                Redirect(context.Response, "/?done=tailnet_oauth_removed#tailnet");
                return;
            case "/tailnet/fix" when tailscale is not null && post && tailnet?.Latest is { } view:
            {
                var report = await tailscale.FixAsync(view, tailnet.Port, context.RequestAborted).ConfigureAwait(false);
                var page = new StringBuilder("<section>");
                if (report.Problem is not null) page.Append(Notice(report.Problem));
                if (report.Changed.Count + report.Unchanged.Count > 0)
                {
                    page.Append("<ul class=\"rows\">");
                    foreach (var line in report.Changed) Step(page, true, "Changed", line);
                    foreach (var line in report.Unchanged) Step(page, true, "Already so", line, word: "Nothing to do");
                    page.Append("</ul>");
                }
                else if (report.Problem is null) page.Append(Para("There was nothing to change."));
                page.Append(Para("Tailscale's own settings take a minute to reach the server; the checklist then ticks by itself."))
                    .Append("<p><a href=\"/#tailnet\">Back to the server</a></p></section>");
                await PageAsync(context, StatusCodes.Status200OK, "Fix it for me", page.ToString()).ConfigureAwait(false);
                return;
            }
            default:
                Redirect(context.Response, "/");
                return;
        }
    }
}
