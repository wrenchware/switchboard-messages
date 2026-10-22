namespace NativeBeeper.Presentation.Help;

/// <summary>
/// One stop of the first-run tour: a short title and text (the guide's own inline marks: **bold**, &lt;kbd&gt;keys&lt;/kbd&gt;),
/// the main-window element it points at when that is on screen, and the guide section that says more.
/// </summary>
/// <param name="Target">The main window element's name; null, or not shown, and the tip sits in the window's corner.</param>
/// <param name="Topic">The guide id "Read more" opens; it must exist in the guide (a check holds it to that).</param>
/// <param name="OnlyWithoutAccounts">Shown only on a copy with no account connected yet.</param>
public sealed record TourStep(string Id, string Title, string Body, string? Target, string Topic, bool OnlyWithoutAccounts = false);

/// <summary>
/// The tour Messages offers on its first run, and from Help whenever asked. It shows what is easy to miss rather than what
/// anyone would find, points only at the window's own furniture, and never opens, reads or sends anything: every stop
/// is words and an arrow.
/// </summary>
public static class TourSteps
{
    public static readonly IReadOnlyList<TourStep> All =
    [
        new("welcome", "Welcome to Messages",
            "Google Messages, Messenger, Google Voice and Instagram in one Windows inbox. This two-minute tour shows the things " +
            "that are easy to miss. Leave whenever you like: <kbd>F1</kbd> opens the full guide any time.", null, "basics"),
        new("accounts", "Connect your services",
            "**Settings → Accounts** connects each one. Google Messages pairs with your phone; Messenger and Instagram sign in " +
            "on their own pages; add as many Google Voice numbers as you like.", "EmptyAddAccount", "connecting", OnlyWithoutAccounts: true),
        new("search", "Search everything",
            "<kbd>Ctrl+E</kbd> searches every service's messages from here. The full search window narrows by person, " +
            "service, date and kind: photos, links, files, calls.", "TitleSearch", "search"),
        new("commands", "Everything by name",
            "<kbd>Ctrl+K</kbd> opens commands: type what you want — pause notifications, settings, a chat's name — " +
            "and press Enter. The **More options** menu here has Help and the shortcut list.", "HeaderMoreButton", "command-palette"),
        new("filters", "Narrow the list",
            "Unread only (<kbd>Ctrl+U</kbd>), one service or Voice number, a label of your own, and the Archived and " +
            "Spam views are all up here.", "FilterSegments", "filters-and-views"),
        new("menu", "Right-click a conversation",
            "Pin it (drag pins to reorder; <kbd>Ctrl+1</kbd>…<kbd>Ctrl+9</kbd> open them), mute it for a while, " +
            "**Remind me later**, give it a label, link the same person across services, or open it in its own window.",
            "ChatList", "conversation-menu"),
        new("keys", "Keep your hands on the keys",
            "<kbd>Alt+Up</kbd>/<kbd>Alt+Down</kbd> move through chats, <kbd>Alt+Shift+Down</kbd> jumps to the next unread, " +
            "<kbd>Ctrl+Tab</kbd> flips between recent ones, and <kbd>Alt+Left</kbd> goes back. <kbd>Ctrl+/</kbd> lists them all.",
            null, "keyboard-shortcuts"),
        new("writing", "Writing tricks",
            "<kbd>Up</kbd> edits your last message. Right-click **Send** to send it later. **Undo send** gives you seconds to " +
            "change your mind. <kbd>Ctrl+V</kbd> pastes a photo and <kbd>Ctrl+Shift+R</kbd> records a voice message.",
            null, "writing-and-sending"),
        new("linked", "One person, every service",
            "Link the conversations you have with the same person on different services into one. Messages answers where " +
            "they last wrote; <kbd>Alt+Down</kbd> in the message box switches service.", null, "linked-conversations"),
        new("new", "Start something",
            "<kbd>Ctrl+N</kbd> starts a chat or a group on any service, searching your conversations and each service's " +
            "directory as you type.", "NewChatButton", "new-chats-and-groups"),
        new("desktop", "It lives on your desktop",
            "Closing the window leaves Messages in the tray, still notifying. **Start with Windows**, a shortcut to show it " +
            "from anywhere (<kbd>Ctrl+Alt+Shift+M</kbd>) and pop-out windows (<kbd>Ctrl+Shift+N</kbd>) are a click away in Settings.",
            null, "living-on-your-desktop"),
        new("console", "For keyboard people",
            "<kbd>Ctrl+`</kbd> opens the chat console: every service's messages as one live terminal, with a prompt. " +
            "Try `/help` and `/chats`. Only what you type as a message is ever sent.", null, "the-chat-console"),
        new("done", "That's the tour",
            "<kbd>F1</kbd> opens the full guide, searchable, from anywhere in Messages. You can take this tour again from " +
            "**More options → Take the tour**.", null, "getting-help"),
    ];

    /// <summary>The stops for this copy: the account stop only before any account is connected.</summary>
    public static IReadOnlyList<TourStep> For(bool hasAccount) => [.. All.Where(step => !step.OnlyWithoutAccounts || !hasAccount)];
}
