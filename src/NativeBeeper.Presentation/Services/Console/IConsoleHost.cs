using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Presentation.Services;

/// <summary>How much the console shows. Quiet: messages, calls and voicemail. Normal: also edits, unsends, reactions,
/// connection and catch-up lines. All: also read receipts and typing.</summary>
public enum ConsoleVerbosity { Quiet, Normal, All }

/// <summary>One service's connection and its helper process, as the host already knows them.</summary>
/// <param name="Network">The service's network name ("Messenger", "Google Voice"); empty when not known.</param>
/// <param name="ProcessId">The helper's process id while it runs.</param>
/// <param name="MemoryBytes">The helper process's working set while it runs.</param>
/// <param name="CpuTime">The helper process's total processor time while it runs; the console works out a rate from two.</param>
/// <param name="LastEvent">When the service last sent this PC anything (ConnectionHealth).</param>
/// <param name="LastSend">When a send on the service was last confirmed (ConnectionHealth).</param>
/// <param name="ExitHow">How the helper last ended ("exited", "killed", "gone"), with <paramref name="LastExit"/> its code.</param>
public sealed record ConsoleProcessStatus(string Name, string State, bool Running = false, TimeSpan? Uptime = null,
    int Restarts = 0, int? LastExit = null, string Network = "", int? ProcessId = null, long? MemoryBytes = null,
    TimeSpan? CpuTime = null, DateTimeOffset? LastEvent = null, DateTimeOffset? LastSend = null, string? ExitHow = null,
    DateTimeOffset? ExitAt = null);
public sealed record ConsoleSystemStatus(string Version, IReadOnlyList<ConsoleProcessStatus> Processes, int PendingSends = 0,
    int Scheduled = 0);

/// <summary>A count the app keeps, by name, with its size where the holder knows it.</summary>
public sealed record ConsoleCount(string Name, long Count, long? Bytes = null);

/// <summary>The Messages process itself, read when asked: sizes and counts only.</summary>
public sealed record ConsolePerf(DateTimeOffset At, TimeSpan Uptime, TimeSpan CpuTime, int Processors, long WorkingSet,
    long PrivateBytes, long Managed, long Heap, long Committed, long Fragmented, IReadOnlyList<long> Generations,
    IReadOnlyList<int> Collections, int Handles, int Gdi, int User, int Threads, IReadOnlyList<ConsoleCount> Caches);

/// <summary>One part of the data folder, as Settings → Storage measures it.</summary>
public sealed record ConsoleStorageItem(string Name, long Bytes, long Reclaimable);

/// <summary>A send the app is still following, or one scheduled for later. Text is the user's own words, shown only.</summary>
/// <param name="State">sending, pending, unknown, held, scheduled or not sent.</param>
public sealed record ConsoleQueueItem(string State, string ChatId, string Text, DateTimeOffset Created, DateTimeOffset? Due = null);

public enum ConsoleCheckLevel { Ok, Info, Warn, Fail }
/// <summary>One line of /diag: a check the host can answer from what it already holds.</summary>
public sealed record ConsoleCheck(ConsoleCheckLevel Level, string Name, string Detail);

/// <summary>What /whoami, /neofetch and /diag say about this PC and this copy of Messages.</summary>
/// <param name="Install">"installed" for the per-user installation, "dev" for a build run from the source tree.</param>
public sealed record ConsoleEnvironment(string Version, string Os, string Architecture, string Runtime, string AppSdk,
    string Machine, string User, string Install, string DataFolder, DateTimeOffset Launched, double Scale = 1);

/// <summary>New lines of a diagnostic log from <paramref name="Position"/> onwards. Reset: the file started again (a new
/// run or a rotation) and these lines replace what was shown.</summary>
public sealed record ConsoleLogRead(IReadOnlyList<string> Lines, long Position, bool Reset);

/// <summary>How a conversation or message action the console asked for ended. Unavailable: the app offers no such action
/// here, and nothing was asked. Refused: nothing changed. Unknown: it may or may not have happened, and is never retried.</summary>
public enum ConsoleActionOutcome { Done, Unavailable, Refused, Unknown }

/// <summary>A console action's outcome, with the app's own words for it when it has some (a menu's notice or status).</summary>
public sealed record ConsoleActionResult(ConsoleActionOutcome Outcome, string? Message = null)
{
    public static readonly ConsoleActionResult Done = new(ConsoleActionOutcome.Done);
    public static readonly ConsoleActionResult Unavailable = new(ConsoleActionOutcome.Unavailable);
    public static Task<ConsoleActionResult> NotHere => Task.FromResult(Unavailable);
}

/// <summary>
/// What the chat console asks of the application around it. Implemented by the console window; every member is an
/// explicit user action, a local preference, or a read of state this PC already holds — never a provider request of the
/// console's own. Members added after the first release have defaults, so a host that does not know them shows less.
/// </summary>
public interface IConsoleHost
{
    /// <summary>Snapshots of existing connection/helper state; never a request to a provider.</summary>
    ConsoleSystemStatus SystemStatus => new("dev", []);
    /// <summary>Opens a conversation in Messages, an explicit open: the ordinary read rule applies there.</summary>
    Task<bool> OpenAsync(string displayChatId);
    Task<bool> OpenSearchHitAsync(SearchHit hit);
    /// <summary>Mutes or unmutes a conversation on this PC only.</summary>
    Task SetMutedAsync(ChatSummary chat, bool muted);
    /// <summary>Whether notifications are on in Settings.</summary>
    bool NotificationsOn { get; }
    /// <summary>"Notifications paused until 3:15 PM." while paused, else null.</summary>
    string? PauseText { get; }
    void PauseFor(TimeSpan duration);
    void PauseUntilMorning();
    void Resume();
    /// <summary>Persisted by the host.</summary>
    ConsoleVerbosity Verbosity { get; set; }

    // ---- Read-only views of what this PC already holds (/top, /perf, /cache, /queues, /diag, /logs, /thread) ----

    /// <summary>The Messages process now; null where the host cannot say.</summary>
    ConsolePerf? Perf() => null;
    /// <summary>The data folder measured by part. Reads the disk, so it is asked for, not polled.</summary>
    Task<IReadOnlyList<ConsoleStorageItem>> StorageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ConsoleStorageItem>>([]);
    /// <summary>Unsettled sends and scheduled sends, oldest first.</summary>
    IReadOnlyList<ConsoleQueueItem> Queue => [];
    IReadOnlyList<ConsoleCheck> Checks() => [];
    ConsoleEnvironment? Environment => null;
    /// <summary>The sanitized diagnostic logs by name, in the Settings viewer's fixed order.</summary>
    IReadOnlyList<string> LogSources => [];
    /// <summary>Lines of a log after <paramref name="from"/> (a byte position from an earlier read; 0 for the tail).</summary>
    Task<ConsoleLogRead> ReadLogAsync(string source, long from, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ConsoleLogRead([], 0, false));
    /// <summary>A conversation's history as this PC keeps it (in memory first, then the saved copy); never a provider read.</summary>
    Task<HistorySnapshot?> SavedHistoryAsync(string chatId) => Task.FromResult<HistorySnapshot?>(null);
    /// <summary>The conversation open in the main window, by the id the list shows it under; null when none.</summary>
    string? OpenChatId => null;

    // ---- Conversation actions: the conversation menu's own, with its checks (MainWindow.ChatMenu) ----
    // Each is an explicit command the user typed on a line of its own; none is retried. A host that does not know one
    // leaves it unavailable.

    /// <summary>Archive or unarchive: on the service, except Instagram and linked conversations, kept on this PC.</summary>
    Task<ConsoleActionResult> SetArchivedAsync(ChatSummary chat, bool archived) => ConsoleActionResult.NotHere;
    /// <summary>Pin or unpin, on this PC only.</summary>
    Task<ConsoleActionResult> SetPinnedAsync(ChatSummary chat, bool pinned) => ConsoleActionResult.NotHere;
    /// <summary>The menu's Mark as unread, on this PC only.</summary>
    Task<ConsoleActionResult> MarkUnreadAsync(ChatSummary chat) => ConsoleActionResult.NotHere;
    /// <summary>The words the menu's Delete… asks with for this conversation; null where the menu offers no Delete.</summary>
    string? DeleteChatQuestion(ChatSummary chat) => null;
    /// <summary>The menu's Delete, once the user has said yes to <see cref="DeleteChatQuestion"/>.</summary>
    Task<ConsoleActionResult> DeleteChatAsync(ChatSummary chat) => ConsoleActionResult.NotHere;
    /// <summary>The menu's Remind me later (a snooze, on this PC); null calls it off.</summary>
    Task<ConsoleActionResult> SnoozeAsync(ChatSummary chat, DateTimeOffset? until) => ConsoleActionResult.NotHere;
    /// <summary>The labels this PC has, by name; empty where there are none or labels are not available.</summary>
    IReadOnlyList<string> LabelNames => [];
    /// <summary>Puts a label on a conversation or takes it off, on this PC, as the menu's Labels does.</summary>
    Task<ConsoleActionResult> SetLabelAsync(ChatSummary chat, string label, bool on) => ConsoleActionResult.NotHere;

    // ---- Message actions: the message menu's own (MessageBubble.Actions, MessageActionsViewModel) ----

    /// <summary>What the message menu offers for this message, as the menu asks for it; null when the host cannot say,
    /// which the console treats as nothing offered.</summary>
    Task<MessageActionOptions?> MessageOptionsAsync(ChatMessage message) => Task.FromResult<MessageActionOptions?>(null);
    /// <summary>Adds or removes one of your reactions. Seen by everyone in the conversation.</summary>
    Task<ConsoleActionResult> ReactAsync(ChatMessage message, string reaction, bool remove) => ConsoleActionResult.NotHere;
    /// <summary>Edits your own message. Seen by everyone in the conversation.</summary>
    Task<ConsoleActionResult> EditAsync(ChatMessage message, string text) => ConsoleActionResult.NotHere;
    /// <summary>Delete for me (<paramref name="everyone"/> false) or for everyone, as the menu's items do.</summary>
    Task<ConsoleActionResult> DeleteMessageAsync(ChatMessage message, bool everyone) => ConsoleActionResult.NotHere;
    /// <summary>The menu's Save: keeps the message in Saved Messages on this PC.</summary>
    Task<ConsoleActionResult> SaveMessageAsync(ChatSummary chat, ChatMessage message) => ConsoleActionResult.NotHere;
    /// <summary>The menu's Remind me: Saved Messages with a time, on this PC.</summary>
    Task<ConsoleActionResult> RemindAsync(ChatSummary chat, ChatMessage message, DateTimeOffset at) => ConsoleActionResult.NotHere;

    // ---- The console's own preferences ----

    /// <summary>The terminal colour scheme by name (ConsoleThemes); persisted.</summary>
    string Theme { get => ConsoleThemes.Default; set { } }
    /// <summary>When the console was last opened before this time, for the MOTD's "Last login"; the host persists it.</summary>
    DateTimeOffset? LastOpened { get => null; set { } }
    /// <summary>Saved aliases, name to command line. Stored protected for this Windows user: they can hold names.</summary>
    IReadOnlyDictionary<string, string> Aliases => new Dictionary<string, string>();
    Task SaveAliasesAsync(IReadOnlyDictionary<string, string> aliases) => Task.CompletedTask;
    /// <summary>Input history kept while Messages runs, so it survives closing the console; never written to disk. Null:
    /// the session keeps its own.</summary>
    List<string>? KeptInputs => null;
    /// <summary>Puts text on the clipboard (| copy).</summary>
    void Copy(string text) { }
    /// <summary>Opens the Messages guide at a topic (a guide id, a title or words); its start when null.</summary>
    void OpenGuide(string? topic) { }
    /// <summary>Starts the tour of the main window.</summary>
    void StartTour() { }
}
