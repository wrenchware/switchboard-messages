using NativeBeeper.Core.Messaging;

namespace NativeBeeper.Protocol;

// The server's whole copy of every conversation (SWITCHBOARD.md stage 8, protocol 24). The server keeps each conversation whole from its newest message
// down to as far back as anyone has read, numbers every change it makes to it, and hands a client only what changed after the number the client holds, so
// a client's copy is whole by construction and is drawn at once. A revision is "<epoch>.<number>": the epoch is new whenever the server's store starts
// over, and a revision of another epoch, or older than the conversation's last reset, is answered with a reset (replace your copy, don't patch it).
// Nothing here is journaled: each is a read, and asking again gives the same answer or a newer one.
public static partial class ProtocolMethods
{
    /// <summary>What changed in one conversation after a revision (<see cref="HistorySyncParams"/>), or its newest messages when the client holds nothing of it.</summary>
    public const string HistorySync = "history.sync";
    /// <summary>Messages older than one the client holds (<see cref="HistoryOlderParams"/>), read from the service and kept if the server does not hold them yet.</summary>
    public const string HistoryOlder = "history.older";
    /// <summary>Which conversations changed after a revision (<see cref="HistoryChangesParams"/>): what a phone woken by a push reads first.</summary>
    public const string HistoryChanges = "history.changes";

    private static Entry[] WholeHistoryEntries() =>
    [
        new(HistorySync, "IWholeHistory.SyncAsync", typeof(HistorySyncParams), typeof(HistorySyncResult)),
        new(HistoryOlder, "IWholeHistory.OlderAsync", typeof(HistoryOlderParams), typeof(HistoryOlderResult)),
        new(HistoryChanges, "IWholeHistory.ChangesAsync", typeof(HistoryChangesParams), typeof(HistoryChangesResult)),
    ];
}

/// <summary>
/// <paramref name="Since"/> is the revision of the client's copy of this conversation; none asks for its newest <paramref name="Limit"/> messages (50 when
/// left out, at most <see cref="WholeHistoryLimits.MaxPage"/>).
/// </summary>
public sealed record HistorySyncParams(string ChatId, string? Since = null, int? Limit = null);

/// <summary>
/// <paramref name="Reset"/>: <paramref name="Messages"/> are the conversation's newest, oldest first, and replace the client's copy; otherwise they are the
/// messages changed after the revision asked for (new, edited, reacted to, receipts), oldest change first, and <paramref name="Removed"/> names those that went.
/// A changed message older than the oldest the client holds is one the client has not read yet: it leaves it. <paramref name="MoreOlder"/> says something lies
/// before the oldest message of a reset (held, or still to read from the service); <paramref name="Complete"/> that the service has nothing older at all.
/// Commit <paramref name="Revision"/> with what was applied.
/// </summary>
public sealed record HistorySyncResult(string Revision, bool Reset, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Removed, bool MoreOlder, bool Complete);

/// <summary>
/// The messages before the client's oldest, named by its time, sort key and id (the order every copy sorts by), at most <paramref name="Limit"/> (50 when left
/// out, at most <see cref="WholeHistoryLimits.MaxPage"/>).
/// </summary>
public sealed record HistoryOlderParams(string ChatId, DateTimeOffset BeforeTimestamp, string BeforeSortKey, string BeforeId, int? Limit = null);

/// <summary><paramref name="Messages"/> oldest first, directly before the one asked from; <paramref name="More"/> says there is still something before them.</summary>
public sealed record HistoryOlderResult(IReadOnlyList<ChatMessage> Messages, bool More, bool Complete);

public sealed record HistoryChangesParams(string? Since = null);

/// <summary>
/// The conversations whose history changed after <c>Since</c>, each with its revision now. <paramref name="Reset"/> says the server cannot tell (another epoch,
/// or too many changed): every copy the client holds is to be read again with <see cref="ProtocolMethods.HistorySync"/>, which says by itself which need a reset.
/// </summary>
public sealed record HistoryChangesResult(string Revision, bool Reset, IReadOnlyList<HistoryChatRevision> Chats);

public sealed record HistoryChatRevision(string ChatId, string Revision);

/// <summary>
/// One change to the server's copy of one conversation, in the event sequence as a <see cref="EventKinds.History"/> frame. A client whose copy is at
/// <paramref name="From"/> applies <paramref name="Messages"/> and <paramref name="Removed"/> and is then at <paramref name="To"/>, with no call; any other client
/// (or this one when <paramref name="Truncated"/> says the messages were too many to carry) asks <see cref="ProtocolMethods.HistorySync"/> from what it holds.
/// <paramref name="Reset"/> says the server started this conversation again, so a copy of it is replaced rather than patched.
/// </summary>
public sealed record HistoryChange(string ChatId, string From, string To, IReadOnlyList<ChatMessage> Messages, IReadOnlyList<string> Removed,
    bool Reset = false, bool Truncated = false);

public static class WholeHistoryLimits
{
    /// <summary>The most messages one sync or older read hands back.</summary>
    public const int MaxPage = 200;
    /// <summary>The newest messages a sync with no revision hands back when it does not say.</summary>
    public const int DefaultPage = 50;
    /// <summary>The most changed messages a sync carries before it answers with a reset instead.</summary>
    public const int MaxChanges = 500;
    /// <summary>The most messages a history event carries; more are announced as truncated and read with a sync.</summary>
    public const int MaxEventMessages = 100;
    /// <summary>The most conversations one <see cref="ProtocolMethods.HistoryChanges"/> names before it answers with a reset.</summary>
    public const int MaxChangedChats = 5000;
}

/// <summary>The server's whole copy of every conversation, as <see cref="ProtocolMethods.HistorySync"/>, <see cref="ProtocolMethods.HistoryOlder"/> and
/// <see cref="ProtocolMethods.HistoryChanges"/> serve it.</summary>
public interface IWholeHistory
{
    Task<HistorySyncResult> SyncAsync(HistorySyncParams request, CancellationToken cancellationToken = default);
    Task<HistoryOlderResult> OlderAsync(HistoryOlderParams request, CancellationToken cancellationToken = default);
    Task<HistoryChangesResult> ChangesAsync(HistoryChangesParams request, CancellationToken cancellationToken = default);
}
