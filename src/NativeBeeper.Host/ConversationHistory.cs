using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NativeBeeper.Core.Messaging;
using NativeBeeper.Core.Services;
using NativeBeeper.Protocol;
using NativeBeeper.Storage;

namespace NativeBeeper.Host;

/// <summary>
/// The server's whole copy of every conversation (SWITCHBOARD.md stage 8). Each conversation is held from its newest message down to a floor with no gap
/// between, filled from the service's newest page and kept up from the providers' events, which this sees first (<see cref="Over"/>). Every change takes the
/// next number of one sequence (<see cref="HistoryDatabase"/>), and a client asks only for what changed after the number it holds (<see cref="SyncAsync"/>), so
/// its copy is whole by construction. The same copy answers the reads every client version already makes (<c>messages.page</c>, <c>history.held</c>,
/// <c>messages.resolve</c>, the peeks): this stands in front of the router as each of those ports, and passes on whatever it does not hold.
/// </summary>
/// <remarks>
/// <para>A conversation's state changes only once what it says is written: a read cancelled or failed half way leaves it as it was. What it holds is known
/// whole up to <see cref="State.Verified"/>, which only a page read, or an event while nothing was missed, moves. After a provider reconnects (or an event's
/// messages could not all be read) its conversations are suspect: events still write, but only a read of the newest page that reaches back to the verified
/// position (within <see cref="MaxCatchUpPages"/> pages) makes it whole again; one that cannot starts the conversation over, which tells every copy of it to
/// replace itself. A held message missing from inside a page that spans it went on the service (an Instagram unsend made elsewhere shows only so).</para>
/// <para>Events never wait here: each conversation's reads and writes run in order on a chain of its own beside the feed, and a resolve asked meanwhile waits
/// for the same read. Reads nobody asked for are paced per service, and Instagram's come one page at a time out of its hourly budget, only while half of it is
/// left for arrivals. Logs counts, states and one-way tags only.</para>
/// </remarks>
internal sealed class ConversationHistory : IWholeHistory, IChatReader, ILinkPreviewLoader, IMessageSender, IMessagePeek, IHistoryWindowReader, IMessageActions,
    IMessageMutations, IDisposable
{
    /// <summary>The most pages a refresh reads back looking for what it holds before it starts the conversation over.</summary>
    public const int MaxCatchUpPages = 10;
    /// <summary>The most pages a read further back than the floor takes from the service for one answer.</summary>
    public const int MaxOlderPages = 8;
    /// <summary>The conversations a reconnect reads again at once (the most recently active); the rest are read again when opened.</summary>
    public const int CatchUpChats = 100;
    /// <summary>The most messages one event is read for; an event naming more makes the conversation read again whole.</summary>
    public const int MaxEventIds = 64;
    private const string CursorPrefix = "h1|";
    private const string InstagramPrefix = "instagram:";

    private readonly HistoryDatabase db;
    private readonly IChatReader chats;
    private readonly IMessageSender sender;
    private readonly IMessagePeek? peek;
    private readonly IHistoryWindowReader? windows;
    private readonly IMessageActions? actions;
    private readonly IMessageMutations? mutations;
    private readonly Action<string> log;
    private readonly Func<string, bool> mayReadUnasked;
    private readonly ConcurrentDictionary<string, Held> held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<ChatMessage?>> resolving = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> scopeResyncs = new(StringComparer.Ordinal);
    private readonly WorkQueue queue = new();
    private readonly CancellationTokenSource stop = new();
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private EventLog? events;
    private Task? worker;
    private Task? walker;

    /// <param name="mayReadUnasked">Asked before each service read nobody asked for (filling or catching up a conversation in the background): false defers
    /// it. Instagram's answers from its background budget.</param>
    public ConversationHistory(HistoryDatabase db, IChatReader chats, IMessageSender sender, IMessagePeek? peek, IHistoryWindowReader? windows,
        IMessageActions? actions, IMessageMutations? mutations, Action<string> log, Func<string, bool>? mayReadUnasked = null)
    {
        this.db = db; this.chats = chats; this.sender = sender; this.peek = peek; this.windows = windows; this.actions = actions; this.mutations = mutations;
        this.log = log; this.mayReadUnasked = mayReadUnasked ?? (_ => true);
    }

    /// <summary>Between two reads of one service that nobody asked for.</summary>
    // A second: Messenger's reads of conversations not on the device go to Facebook, and nobody is waiting on these.
    public TimeSpan Pace { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>How long after starting the first walk of the list begins, and how often it repeats.</summary>
    public TimeSpan WalkDelay { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan WalkEvery { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>A conversation opened after this long without a read of its newest page has it read again behind the answer.</summary>
    public TimeSpan OpenRefreshAfter { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>When a change made through this host is read back, in case its service sends no event for it.</summary>
    public IReadOnlyList<TimeSpan> ConfirmAfter { get; init; } = [TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(6)];
    /// <summary>A held message this young is not taken as gone for missing from a page: the service's page may not have it yet.</summary>
    public TimeSpan AbsenceGrace { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>How long a background read put off (its page partial, the budget spent) waits before it is tried again.</summary>
    public TimeSpan RetryAfter { get; init; } = TimeSpan.FromMinutes(2);
    /// <summary>The longest a resolve waits for an event's own read of the same message before it asks the service itself.</summary>
    public TimeSpan ResolveWait { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>The longest a page, a sync or a held read waits for the conversation's events already under way to be written.</summary>
    public TimeSpan SettleWait { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Opens the store and starts the background work.</summary>
    public void Start()
    {
        _ = Task.Run(async () =>
        {
            var clock = Stopwatch.GetTimestamp();
            try
            {
                var rows = await db.OpenAsync(stop.Token).ConfigureAwait(false);
                foreach (var row in rows) held[row.ChatId] = Held.From(row);
                log($"history store open chats={rows.Count} filled={held.Values.Count(h => h.State.Filled)} seq={db.Seq} bytes={db.Bytes} ms={Ms(clock)}");
                ready.TrySetResult();
                worker = Task.Run(WorkAsync, CancellationToken.None);
                walker = Task.Run(WalkAsync, CancellationToken.None);
            }
            catch (Exception failure)
            {
                // Served without a history: every read goes on to the services, as before stage 8.
                log($"history store open failed error={failure.GetType().Name}");
                ready.TrySetException(failure);
            }
        });
    }

    /// <summary>The store is open (for the checks).</summary>
    internal Task Opened => ready.Task;

    /// <summary>Conversations waiting for a background read (for the checks).</summary>
    internal int Queued => queue.Count;

    /// <summary>Every conversation's event work done so far has finished (for the checks).</summary>
    internal Task SettledAsync() => Task.WhenAll(held.Values.Select(chat => chat.Tail));

    /// <summary>The log the history changes go into.</summary>
    public void Attach(EventLog log) => events = log;

    /// <summary>The providers' feed, each event's messages read into the copy beside it.</summary>
    public IRealtimeFeed Over(IRealtimeFeed source) => new ObservedFeed(this, source);

    /// <summary>
    /// An account was signed out, removed from the server, or Google's local data was reset (<c>ProtocolServices.AccountEnded</c>): its conversations' copies
    /// go. Never waits.
    /// </summary>
    public void Ended(string account, string? key)
    {
        var prefix = account switch
        {
            "messenger" => "messenger:",
            "instagram" => InstagramPrefix,
            "voice" when !string.IsNullOrEmpty(key) => NativeBeeper.App.Services.GoogleVoice.GoogleVoiceModels.Account(key) + ":",
            "google-reset" => "google-direct:",
            _ => null,
        };
        if (prefix is null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await ready.Task.ConfigureAwait(false);
                var forgotten = await ForgetAsync(held.Keys.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)).ToArray()).ConfigureAwait(false);
                log($"history store forgotten account={account} chats={forgotten}");
            }
            catch (Exception failure) when (!stop.IsCancellationRequested) { log($"history store forget failed error={failure.GetType().Name}"); }
        });
    }

    public void Dispose()
    {
        stop.Cancel();
        queue.Complete();
        try { Task.WaitAll([.. new[] { worker, walker }.OfType<Task>()], TimeSpan.FromSeconds(2)); } catch (Exception) { }
        db.Dispose();
    }

    // ---- IWholeHistory (protocol 24) ----

    public async Task<HistorySyncResult> SyncAsync(HistorySyncParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var chat = await FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        await chat.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var state = chat.State;
            var since = Parse(request.Since);
            var revision = Revision(chat.Seq);
            if (since is { } at && at <= db.Seq && at >= chat.ResetSeq)
            {
                var changed = await db.ReadChangedAsync(chat.ChatId, at, WholeHistoryLimits.MaxChanges + 1, cancellationToken).ConfigureAwait(false);
                if (changed.Count <= WholeHistoryLimits.MaxChanges)
                {
                    var messages = changed.Where(row => !row.Removed).Select(Message).OfType<ChatMessage>().ToArray();
                    var removed = changed.Where(row => row.Removed).Select(row => row.MessageId).ToArray();
                    return new(revision, false, messages, removed, MoreOlder: true, state.Complete);
                }
            }
            var newest = await NewestAsync(chat.ChatId, state, limit, cancellationToken).ConfigureAwait(false);
            return new(revision, true, newest.Messages, [], newest.More, state.Complete);
        }
        finally { chat.Gate.Release(); }
    }

    public async Task<HistoryOlderResult> OlderAsync(HistoryOlderParams request, CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(request.Limit ?? WholeHistoryLimits.DefaultPage, 1, WholeHistoryLimits.MaxPage);
        var chat = await FilledAsync(request.ChatId, cancellationToken).ConfigureAwait(false);
        var page = await OlderPageAsync(chat, new Position(request.BeforeTimestamp.UtcTicks, request.BeforeSortKey ?? "", request.BeforeId ?? ""), limit, cancellationToken)
            .ConfigureAwait(false);
        return new(page.Messages, page.More, chat.State.Complete);
    }

    public async Task<HistoryChangesResult> ChangesAsync(HistoryChangesParams request, CancellationToken cancellationToken = default)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (Parse(request.Since) is not { } since || since > db.Seq) return new(Revision(db.Seq), true, []);
        // The store's number and the conversations' numbers read together, so the revision handed out never passes a change it does not name.
        var (seq, changed) = await db.ReadChangedChatsAsync(since, WholeHistoryLimits.MaxChangedChats + 1, cancellationToken).ConfigureAwait(false);
        if (changed.Count > WholeHistoryLimits.MaxChangedChats) return new(Revision(seq), true, []);
        return new(Revision(seq), false, changed.Select(row => new HistoryChatRevision(row.ChatId, Revision(row.Seq))).ToArray());
    }

    // ---- IChatReader: the list as the services give it; a conversation's pages from the copy ----

    public Task<Page<ChatSummary>> GetChatsAsync(string? olderThan = null, CancellationToken cancellationToken = default) => chats.GetChatsAsync(olderThan, cancellationToken);
    public Task<ChatSummary> GetChatAsync(string chatId, CancellationToken cancellationToken = default) => chats.GetChatAsync(chatId, cancellationToken);

    public async Task<Page<ChatMessage>> GetMessagesAsync(string chatId, string? olderThan = null, CancellationToken cancellationToken = default)
    {
        // A cursor of the service's (handed out before this copy held the conversation) goes on to it, as before.
        if (olderThan is not null && !olderThan.StartsWith(CursorPrefix, StringComparison.Ordinal))
            return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        if (!await IsReadyAsync(cancellationToken).ConfigureAwait(false)) return await chats.GetMessagesAsync(chatId, olderThan, cancellationToken).ConfigureAwait(false);
        var chat = Chat(chatId);
        var clock = Stopwatch.GetTimestamp();
        if (!chat.State.Filled)
        {
            var (page, _) = await RefreshAsync(chat, Reason.Open, cancellationToken).ConfigureAwait(false);
            // A page the service is still loading (or one that came back partial) is handed on as it is, and the copy waits for a whole one.
            if (!chat.State.Filled) return page ?? await chats.GetMessagesAsync(chatId, null, cancellationToken).ConfigureAwait(false);
        }
        else if (olderThan is null && Stale(chat)) Enqueue(chatId, Work.Open);
        if (olderThan is null) await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
        var state = chat.State;
        (IReadOnlyList<ChatMessage> Messages, bool More) result = olderThan is null
            ? await NewestAsync(chatId, state, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : ReadCursor(olderThan) is { } before ? await OlderPageAsync(chat, before, WholeHistoryLimits.DefaultPage, cancellationToken).ConfigureAwait(false)
            : throw new ArgumentException("Invalid page position.");
        log($"history serve method=page chat={Tag(chatId)} older={(olderThan is not null ? 1 : 0)} rows={result.Messages.Count} more={(result.More ? 1 : 0)} ms={Ms(clock)}");
        return new(result.Messages, result.More, result.More && result.Messages.Count > 0 ? Cursor(result.Messages[0]) : null, null);
    }

    public Task LoadLinkPreviewsAsync(string chatId, IReadOnlyList<string> messageIds, CancellationToken cancellationToken = default) =>
        chats is ILinkPreviewLoader loader ? loader.LoadLinkPreviewsAsync(chatId, messageIds, cancellationToken) : throw new NotSupportedException();

    // ---- IMessageSender: sends go on; a resolve is the copy's when it holds the message ----

    public Task<SendPermission> GetSendPermissionAsync(string chatId, CancellationToken cancellationToken = default) => sender.GetSendPermissionAsync(chatId, cancellationToken);
    public Task<SendReceipt> SendTextAsync(string chatId, string text, CancellationToken cancellationToken = default) => sender.SendTextAsync(chatId, text, cancellationToken);

    // What is read here is never written from here: an answer can be a partial copy (Instagram's list words), and the copy takes messages only from pages
    // and the events' own reads.
    public async Task<ChatMessage> ResolveMessageAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        // The event that named it is being read for the copy: wait for that read rather than read the service twice or answer the copy from before it, but
        // never long (a push is waiting behind this): past ResolveWait the service is asked itself.
        if (resolving.TryGetValue(Key(chatId, messageId), out var pending))
        {
            try
            {
                if (await pending.WaitAsync(ResolveWait, cancellationToken).ConfigureAwait(false) is { } fresh) return fresh;
                return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is TimeoutException) { return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false); }
        }
        if (await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false) is { } kept) return kept;
        return await sender.ResolveMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
    }

    // ---- IMessagePeek ----

    public async ValueTask<ChatMessage?> PeekAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    // The copy keeps only whole messages (pages, and event reads a whole copy stands behind), so what it holds is whole, and so is what an event's read brings.
    public async ValueTask<ChatMessage?> PeekWholeAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        await PendingAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? await HeldMessageAsync(chatId, messageId, cancellationToken).ConfigureAwait(false)
            ?? (peek is null ? null : await peek.PeekWholeAsync(chatId, messageId, cancellationToken).ConfigureAwait(false));

    public bool HoldsPartialCopies(string chatId) => peek?.HoldsPartialCopies(chatId) ?? false;

    // ---- IHistoryWindowReader ----

    public bool CanReadWindow(string chatId) => windows?.CanReadWindow(chatId) ?? false;

    public async Task<HistoryWindow> ReadWindowAsync(string chatId, DateTimeOffset at, int older, int newer, CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false) && held.TryGetValue(chatId, out var chat) && chat.State is { Filled: true } state
            && (state.Floor is null || at.UtcTicks > state.Floor.Value.Ticks))
        {
            var before = await HeldBeforeAsync(chatId, state, new Position(at.UtcTicks, "", ""), older + 1, cancellationToken).ConfigureAwait(false);
            var after = await NewerAsync(chatId, state, at, newer, cancellationToken).ConfigureAwait(false);
            var moreOlder = before.Count > older || !state.Complete;
            return new HistoryWindow([.. before.TakeLast(older), .. after.Messages], moreOlder, after.More);
        }
        return windows is null ? throw new NotSupportedException() : await windows.ReadWindowAsync(chatId, at, older, newer, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HeldHistory?> ReadHeldAsync(string chatId, DateTimeOffset after, int limit, CancellationToken cancellationToken = default)
    {
        if (await IsReadyAsync(cancellationToken).ConfigureAwait(false) && held.TryGetValue(chatId, out var chat) && chat.State.Filled && limit > 0)
        {
            await SettledAsync(chat, cancellationToken).ConfigureAwait(false);
            var state = chat.State;
            var rows = await NewerAsync(chatId, state, after.AddTicks(1), limit, cancellationToken, newestFirst: true).ConfigureAwait(false);
            // Everything after the moment is here when the copy is known whole, reaches back past it (or to the conversation's start), and the newest asked
            // for cover it all.
            var reaches = state.Floor is null || state.Complete || state.Floor.Value.Ticks <= after.UtcTicks;
            return new HeldHistory(rows.Messages, reaches && !rows.More && !chat.Suspect);
        }
        return windows is null ? null : await windows.ReadHeldAsync(chatId, after, limit, cancellationToken).ConfigureAwait(false);
    }

    // ---- IMessageActions, IMessageMutations: done by the router; what they change is read back into the copy ----

    public bool HasLocalActionOptions => actions?.HasLocalActionOptions ?? false;
    public Task<MessageActionOptions> GetOptionsAsync(string chatId, string messageId, CancellationToken cancellationToken = default) =>
        (actions ?? throw new NotSupportedException()).GetOptionsAsync(chatId, messageId, cancellationToken);
    public Task<SendReceipt> ReplyAsync(string chatId, string messageId, string text, CancellationToken cancellationToken = default) =>
        (actions ?? throw new NotSupportedException()).ReplyAsync(chatId, messageId, text, cancellationToken);

    public async Task ReactAsync(string chatId, string messageId, string reaction, bool remove, CancellationToken cancellationToken = default)
    {
        await (actions ?? throw new NotSupportedException()).ReactAsync(chatId, messageId, reaction, remove, cancellationToken).ConfigureAwait(false);
        Confirm(chatId, messageId);
    }

    public async Task EditAsync(string chatId, string messageId, string? originalText, string text, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).EditAsync(chatId, messageId, originalText, text, cancellationToken).ConfigureAwait(false);
        // Done on the service: the copy says so at once, and the service's own copy replaces this when it is read back.
        if (held.TryGetValue(chatId, out var chat) && chat.State.Filled)
            Chain(chat, async () =>
            {
                if (await HeldMessageAsync(chatId, messageId, CancellationToken.None).ConfigureAwait(false) is { } was)
                    await KeepAsync(chat, [was with { Text = text, EditedAt = was.EditedAt ?? DateTimeOffset.UtcNow }], Reason.Mutation, whole: false).ConfigureAwait(false);
            });
        Confirm(chatId, messageId);
    }

    public async Task DeleteAsync(string chatId, string messageId, bool forEveryone, CancellationToken cancellationToken = default)
    {
        await (mutations ?? throw new NotSupportedException()).DeleteAsync(chatId, messageId, forEveryone, cancellationToken).ConfigureAwait(false);
        // Removed for this account alone: gone from every copy at once. Unsent for everyone: the service says how it shows, so it is read back.
        if (!forEveryone && held.TryGetValue(chatId, out var chat) && chat.State.Filled) Chain(chat, () => RemoveAsync(chat, [messageId], Reason.Mutation));
        else Confirm(chatId, messageId);
    }

    // ---- Events: queued beside the feed, never waited for ----

    private void Observe(RealtimeEvent change)
    {
        // Before the store is open, events wait in arrival order and are taken in that order once it is (a delete never ahead of the change it followed).
        lock (early)
        {
            if (!earlyTaken)
            {
                early.Add(change);
                if (early.Count == 1) _ = TakeEarlyAsync();
                return;
            }
        }
        Take(change);
    }

    private readonly List<RealtimeEvent> early = [];
    private bool earlyTaken;

    private async Task TakeEarlyAsync()
    {
        try { await ready.Task.ConfigureAwait(false); }
        catch (Exception) { }
        // Under the lock, so nothing that arrives meanwhile is taken ahead of what waited (taking one only queues its work).
        lock (early)
        {
            foreach (var change in early) Take(change);
            early.Clear();
            earlyTaken = true;
        }
    }

    private void Take(RealtimeEvent change)
    {
        if (!ready.Task.IsCompletedSuccessfully) return;
        try { Handle(change); }
        catch (Exception failure) { log($"history event failed kind={change.Kind} error={failure.GetType().Name}"); }
    }

    private void Handle(RealtimeEvent change)
    {
        switch (change.Kind)
        {
            case RealtimeKind.MessageChanged when change.ChatId is { } chatId:
            {
                var chat = Chat(chatId);
                if (!chat.State.Filled) { Enqueue(chatId, Work.Arrival); return; }
                if (change.Ids is not { Count: > 0 } ids) { Enqueue(chatId, Work.Refresh); return; }
                if (ids.Count > MaxEventIds) Suspect(chat);
                ReadInto(chat, [.. ids.Distinct(StringComparer.Ordinal).Take(MaxEventIds)], Reason.Event);
                return;
            }
            case RealtimeKind.MessageDeleted when change.ChatId is { } chatId && change.Ids is { Count: > 0 } ids:
                if (held.TryGetValue(chatId, out var deleting) && deleting.State.Filled) Chain(deleting, () => RemoveAsync(deleting, [.. ids], Reason.Event));
                return;
            case RealtimeKind.ChatChanged:
                foreach (var id in Named(change).Take(MaxEventIds))
                {
                    // Instagram's arrivals come as their own events from its own reads; a read here would cost its budget for a row that moved.
                    if (id.StartsWith(InstagramPrefix, StringComparison.Ordinal)) continue;
                    Enqueue(id, held.TryGetValue(id, out var known) && known.State.Filled ? Work.Check : Work.Arrival);
                }
                return;
            case RealtimeKind.ChatDeleted:
                _ = ForgetAsync([.. Named(change).Where(held.ContainsKey)]);
                return;
            case RealtimeKind.Disconnected:
                // What arrives until the service is back may have a gap below it: only a read of the newest page makes these whole again.
                foreach (var chat in InScope(change.Scope)) chat.MarkSuspect();
                return;
            case RealtimeKind.Connected or RealtimeKind.ResyncRequired:
            {
                scopeResyncs[change.Scope ?? ""] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var scoped = InScope(change.Scope).ToArray();
                foreach (var chat in scoped) chat.MarkSuspect();
                // Whatever these did while the service was away is read again: the most recently active now, the rest when opened.
                foreach (var chat in scoped.OrderByDescending(chat => chat.State.Newest?.Ticks ?? 0).Take(CatchUpChats)) Enqueue(chat.ChatId, Work.CatchUp);
                return;
            }
        }
    }

    /// <summary>
    /// Waits, at most <see cref="SettleWait"/>, for what the conversation's events have already set going to be written, so a read served just after an event
    /// holds its messages: the event has gone on to the client before them, and a copy read without them a moment earlier was kept as current.
    /// </summary>
    private async Task SettledAsync(Held chat, CancellationToken cancellationToken)
    {
        Task tail;
        lock (chat.ChainLock) tail = chat.Tail;
        if (tail.IsCompleted) return;
        try { await tail.WaitAsync(SettleWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { }
    }

    /// <summary>A message an event's read is bringing right now, waited for (at most <see cref="ResolveWait"/>); null when none is on its way.</summary>
    private async Task<ChatMessage?> PendingAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!resolving.TryGetValue(Key(chatId, messageId), out var pending)) return null;
        try { return await pending.WaitAsync(ResolveWait, cancellationToken).ConfigureAwait(false); }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return null; }
    }

    private IEnumerable<Held> InScope(string? scope) =>
        held.Values.Where(chat => chat.State.Filled && chat.ChatId.StartsWith(scope ?? "", StringComparison.Ordinal));

    private static IEnumerable<string> Named(RealtimeEvent change) =>
        (change.ChatId is { } one ? [one] : Array.Empty<string>()).Concat(change.Ids ?? []).Distinct(StringComparer.Ordinal);

    // A conversation's own work in the order it was asked for, beside the feed. Each step catches its own failures, so one never stops the chain.
    private static void Chain(Held chat, Func<Task> step)
    {
        lock (chat.ChainLock) chat.Tail = chat.Tail.ContinueWith(_ => step(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }

    // Reads the named messages from the service at once, together, and writes them in the conversation's own order. The reads are not held behind anything
    // (a push waits on them through ResolveMessageAsync, whose wait is in place before the event goes on); only the writes wait their turn.
    private void ReadInto(Held chat, IReadOnlyList<string> ids, Reason reason)
    {
        var reads = ids.Select(id => (Key: Key(chat.ChatId, id), Read: ReadWholeAsync(chat.ChatId, id))).ToArray();
        foreach (var (key, read) in reads) resolving[key] = read;
        var all = Task.WhenAll(reads.Select(entry => entry.Read));
        _ = all.ContinueWith(_ =>
        {
            foreach (var (key, read) in reads) resolving.TryRemove(new KeyValuePair<string, Task<ChatMessage?>>(key, read));
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        Chain(chat, async () =>
        {
            try
            {
                var found = (await all.ConfigureAwait(false)).OfType<ChatMessage>().ToArray();
                var missed = found.Length < reads.Length;
                if (found.Length > 0) await KeepAsync(chat, found, reason, whole: !missed).ConfigureAwait(false);
                // Something the event named is not in the copy: until a page is read, nothing above it is known whole.
                if (missed) Suspect(chat);
            }
            catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
            {
                log($"history read failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
                Suspect(chat);
            }
        });
    }

    private void Suspect(Held chat)
    {
        chat.MarkSuspect();
        Enqueue(chat.ChatId, Work.Refresh);
    }

    // A message as whole as a page would give it, or none. Where a service holds some messages only in part (Instagram's list words for a photo), only a copy its
    // whole memory stands behind is taken.
    private async Task<ChatMessage?> ReadWholeAsync(string chatId, string messageId)
    {
        try
        {
            if (peek?.HoldsPartialCopies(chatId) == true) return await peek.PeekWholeAsync(chatId, messageId, stop.Token).ConfigureAwait(false);
            return await sender.ResolveMessageAsync(chatId, messageId, stop.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"history read failed chat={Tag(chatId)} error={failure.GetType().Name}");
            return null;
        }
    }

    private void Confirm(string chatId, string messageId)
    {
        if (!held.TryGetValue(chatId, out var chat) || !chat.State.Filled) return;
        _ = Task.Run(async () =>
        {
            foreach (var wait in ConfirmAfter)
            {
                try { await Task.Delay(wait, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                ReadInto(chat, [messageId], Reason.Mutation);
            }
        });
    }

    private async Task<int> ForgetAsync(IReadOnlyList<string> chatIds)
    {
        var forgotten = 0;
        foreach (var id in chatIds)
        {
            if (!held.TryGetValue(id, out var chat)) continue;
            // Under its gate, so a write in flight finishes first and none after it reaches the store; and its rows go before it leaves the map, so a
            // conversation made again under the same id (an event after this) fills afresh and is never deleted by this.
            await chat.Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                chat.Forgotten = true;
                forgotten += await db.ForgetAsync([id], CancellationToken.None).ConfigureAwait(false);
                held.TryRemove(new KeyValuePair<string, Held>(id, chat));
            }
            finally { chat.Gate.Release(); }
        }
        return forgotten;
    }

    // ---- Filling and catching up ----

    private async Task<Held> FilledAsync(string chatId, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var chat = Chat(chatId);
        if (chat.State.Filled) return chat;
        await RefreshAsync(chat, Reason.Open, cancellationToken).ConfigureAwait(false);
        return chat.State.Filled ? chat
            : throw new NativeBeeper.Core.Backend.BackendException(NativeBeeper.Core.Backend.BackendFailure.NotReady, "The conversation's history is still loading.");
    }

    /// <summary>
    /// Reads the service's newest page and joins it to what is held (or fills the conversation with it), reading further back until the two meet. Returns the
    /// service's own page, and whether the read was taken: false when it was put off (a page still loading or partial, a further page the budget did not
    /// allow), which changes nothing held. <paramref name="mayReadMore"/>, for a read nobody asked for, is asked before each further page.
    /// </summary>
    private async Task<(Page<ChatMessage>? Page, bool Taken)> RefreshAsync(Held chat, Reason reason, CancellationToken cancellationToken, Func<bool>? mayReadMore = null)
    {
        await chat.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.GetTimestamp();
        try
        {
            if (chat.Forgotten) return (null, true);
            var state = chat.State;
            // A suspicion that comes while this reads (a provider dropping again) is not cleared by it.
            var suspicions = chat.SuspicionsNow;
            var page = await chats.GetMessagesAsync(chat.ChatId, null, cancellationToken).ConfigureAwait(false);
            if (!Usable(page))
            {
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=partial refreshing={(page.IsRefreshing ? 1 : 0)} ms={Ms(clock)}");
                return (page, false);
            }
            var gathered = Sorted(page.Items);
            var cursor = page.OldestCursor;
            var more = page.HasMore;
            if (!state.Filled)
            {
                var newest = gathered.Count > 0 ? Position.Of(gathered[^1]) : (Position?)null;
                var filled = new State(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
                // Not announced: no copy of it can be held yet, and a first fill of every conversation would push real events out of the log's ring. It clears
                // whatever was kept of the conversation before it was whole, so nothing stray sits under the floor.
                await WriteAsync(chat, gathered, [], filled, reset: true, announce: false).ConfigureAwait(false);
                Settle(chat, suspicions);
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=filled rows={gathered.Count} complete={(filled.Complete ? 1 : 0)} ms={Ms(clock)}");
                return (page, true);
            }
            // A held conversation the service now calls empty is not taken at its word yet: read again later, nothing changed.
            if (gathered.Count == 0 && state.Newest is not null)
            {
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=empty ms={Ms(clock)}");
                return (page, false);
            }
            var pages = 1;
            while (!Meets(state, gathered) && more && cursor is not null && pages < MaxCatchUpPages)
            {
                if (mayReadMore?.Invoke() == false)
                {
                    log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=deferred pages={pages} ms={Ms(clock)}");
                    return (page, false);
                }
                var older = await chats.GetMessagesAsync(chat.ChatId, cursor, cancellationToken).ConfigureAwait(false);
                pages++;
                // A page that says nothing yet is no reason to start over: the read is tried again later, and nothing changes now.
                if (!Usable(older) || older.Items.Count == 0 && older.HasMore)
                {
                    log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=partial pages={pages} ms={Ms(clock)}");
                    return (page, false);
                }
                gathered = Sorted([.. older.Items, .. gathered]);
                cursor = older.OldestCursor;
                more = older.HasMore;
            }
            if (!Meets(state, gathered))
            {
                // What the service has now does not reach what is known whole: start the conversation over from this, and every copy of it does the same.
                var newest = gathered.Count > 0 ? Position.Of(gathered[^1]) : (Position?)null;
                var restarted = new State(true, !more, more ? cursor : null, gathered.Count > 0 ? Position.Of(gathered[0]) : null, newest, newest);
                await WriteAsync(chat, gathered, [], restarted, reset: true, found: true).ConfigureAwait(false);
                Settle(chat, suspicions);
                log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=reset rows={gathered.Count} pages={pages} ms={Ms(clock)}");
                return (page, true);
            }
            var absent = await AbsentAsync(chat.ChatId, gathered, cancellationToken).ConfigureAwait(false);
            var joined = state;
            var reached = true;
            if (gathered.Count > 0)
            {
                // Known whole up to what was read, and no further: messages events wrote above the page's newest (a service answering from a copy a moment
                // old) were not checked by it, and the conversation stays suspect until a read reaches them.
                var seen = Position.Of(gathered[^1]);
                var top = Max(state.Newest, seen);
                var verified = Max(state.Verified, seen);
                reached = verified.Equals(top);
                joined = joined with { Newest = top, Verified = verified };
                // Read further back than the floor while finding where the two meet: the floor moves down with it. One filled while it held nothing (a
                // Messenger encrypted conversation this device has no messages of) takes its floor, and whether there is more, from this read.
                if (state.Floor is not { } floor || Position.Of(gathered[0]).CompareTo(floor) < 0)
                    joined = joined with { Floor = Position.Of(gathered[0]), Cursor = more ? cursor : null, Complete = !more };
            }
            var written = await WriteAsync(chat, gathered, absent, joined, found: true).ConfigureAwait(false);
            if (reached) Settle(chat, suspicions);
            log($"history fill chat={Tag(chat.ChatId)} reason={Name(reason)} outcome=joined rows={gathered.Count} put={written.Put.Count} absent={written.Removed.Count} pages={pages} reached={(reached ? 1 : 0)} ms={Ms(clock)}");
            return (page, reached);
        }
        finally { chat.Gate.Release(); }
    }

    // A whole read of the newest page: the conversation is whole again, and was read just now, unless something was suspected of it since the read began.
    private static void Settle(Held chat, long suspicions)
    {
        chat.Settle(suspicions);
        chat.RefreshedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    private static bool Usable(Page<ChatMessage> page) => !page.IsRefreshing && page.Warning is null && page.UnavailableScopes is not { Count: > 0 };

    // Whether a run read from the newest back reaches what is known whole: nothing is, or it reaches back to it.
    private static bool Meets(State state, IReadOnlyList<ChatMessage> gathered) =>
        state.Verified is not { } verified || gathered.Count == 0 || Position.Of(gathered[0]).CompareTo(verified) <= 0;

    // The held messages a run of pages spans (strictly inside its oldest and newest, older than the grace) that the run does not have: gone on the service.
    private async Task<IReadOnlyList<string>> AbsentAsync(string chatId, IReadOnlyList<ChatMessage> gathered, CancellationToken cancellationToken)
    {
        if (gathered.Count < 2) return [];
        var oldest = Position.Of(gathered[0]);
        var newest = Position.Of(gathered[^1]);
        var graceTicks = (DateTimeOffset.UtcNow - AbsenceGrace).UtcTicks;
        var present = gathered.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
        var rows = await db.ReadBetweenAsync(chatId, gathered[0].Timestamp.ToUnixTimeMilliseconds(), gathered[^1].Timestamp.ToUnixTimeMilliseconds(), 5000, cancellationToken)
            .ConfigureAwait(false);
        return rows.Where(row => !row.Removed && !present.Contains(row.MessageId)).Select(Message).OfType<ChatMessage>()
            .Where(message => Position.Of(message) is var at && at.CompareTo(oldest) > 0 && at.CompareTo(newest) < 0 && at.Ticks < graceTicks)
            .Select(message => message.Id).ToArray();
    }

    // Messages strictly before a position, from the copy, reading further back from the service while the copy runs out before `limit`.
    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> OlderPageAsync(Held chat, Position before, int limit, CancellationToken cancellationToken)
    {
        var pages = 0;
        while (true)
        {
            var state = chat.State;
            var rows = await HeldBeforeAsync(chat.ChatId, state, before, limit + 1, cancellationToken).ConfigureAwait(false);
            if (rows.Count > limit) return (rows.TakeLast(limit).ToArray(), true);
            if (state.Complete || pages >= MaxOlderPages) return (rows, !state.Complete);
            if (!await ExtendAsync(chat, cancellationToken).ConfigureAwait(false)) return (rows, !chat.State.Complete);
            pages++;
        }
    }

    // One page older than the floor from the service, kept, and the floor moved down to it once written. False when nothing more could be read now (it
    // changes nothing then: a page the service has not loaded is read again on the next ask).
    private async Task<bool> ExtendAsync(Held chat, CancellationToken cancellationToken)
    {
        await chat.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var clock = Stopwatch.GetTimestamp();
        try
        {
            var state = chat.State;
            if (state.Complete || chat.Forgotten) return false;
            Page<ChatMessage>? page;
            try
            {
                page = state.Cursor is { } cursor ? await chats.GetMessagesAsync(chat.ChatId, cursor, cancellationToken).ConfigureAwait(false)
                    : await RecoverOlderAsync(chat.ChatId, state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is not OperationCanceledException && state.Cursor is not null)
            {
                // The service's position from an earlier run may mean nothing to it now: find the floor again from its newest page.
                log($"history older cursor failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
                page = await RecoverOlderAsync(chat.ChatId, state, cancellationToken).ConfigureAwait(false);
            }
            if (page is null || !Usable(page)) return false;
            var items = Sorted(page.Items);
            var below = state.Floor is { } floor ? items.Where(message => Position.Of(message).CompareTo(floor) < 0).ToArray() : items.ToArray();
            // Complete only on the service's word; a page that says more but brings nothing is read again on the next ask.
            if (items.Count == 0 && page.HasMore) return false;
            var next = state with
            {
                Floor = below.Length > 0 ? Position.Of(below[0]) : state.Floor,
                Cursor = page.HasMore ? page.OldestCursor : null,
                Complete = !page.HasMore,
            };
            await WriteAsync(chat, items, [], next).ConfigureAwait(false);
            log($"history older chat={Tag(chat.ChatId)} rows={items.Count} below={below.Length} complete={(next.Complete ? 1 : 0)} ms={Ms(clock)}");
            return below.Length > 0;
        }
        finally { chat.Gate.Release(); }
    }

    // Pages back from the service's newest until a page reaches below the floor, and returns that page (it holds the floor's neighbours below it). Null
    // when it was not found within the pages allowed: fewer for Instagram, every page of which is a request on the real account.
    private async Task<Page<ChatMessage>?> RecoverOlderAsync(string chatId, State state, CancellationToken cancellationToken)
    {
        var limit = chatId.StartsWith(InstagramPrefix, StringComparison.Ordinal) ? 4 : MaxCatchUpPages * 2;
        var page = await chats.GetMessagesAsync(chatId, null, cancellationToken).ConfigureAwait(false);
        for (var pages = 1; ; pages++)
        {
            if (!Usable(page)) return null;
            var items = Sorted(page.Items);
            if (state.Floor is not { } floor || items.Count > 0 && Position.Of(items[0]).CompareTo(floor) < 0) return page;
            if (!page.HasMore || page.OldestCursor is null) return page;
            if (pages >= limit) return null;
            page = await chats.GetMessagesAsync(chatId, page.OldestCursor, cancellationToken).ConfigureAwait(false);
        }
    }

    // The held messages strictly before a position (and not below the floor), oldest first, at most `limit`.
    private async Task<IReadOnlyList<ChatMessage>> HeldBeforeAsync(string chatId, State state, Position before, int limit, CancellationToken cancellationToken)
    {
        // A moment can hold several messages, ordered by sort key and id within it: read a little more than asked and order them here.
        var rows = await db.ReadAtOrBeforeAsync(chatId, new DateTimeOffset(before.Ticks, TimeSpan.Zero).ToUnixTimeMilliseconds(), limit + 64, cancellationToken)
            .ConfigureAwait(false);
        return Sorted(rows.Select(Message).OfType<ChatMessage>()
                .Where(message => Position.Of(message).CompareTo(before) < 0 && AboveFloor(state, message)))
            .TakeLast(limit).ToArray();
    }

    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewestAsync(string chatId, State state, int limit, CancellationToken cancellationToken)
    {
        var rows = await db.ReadNewestAsync(chatId, limit + 65, cancellationToken).ConfigureAwait(false);
        var messages = Sorted(rows.Select(Message).OfType<ChatMessage>().Where(message => AboveFloor(state, message)));
        return (messages.TakeLast(limit).ToArray(), messages.Count > limit || !state.Complete);
    }

    // The held messages at or after a moment, oldest first: the first `limit`, or with `newestFirst` the newest that many.
    private async Task<(IReadOnlyList<ChatMessage> Messages, bool More)> NewerAsync(string chatId, State state, DateTimeOffset at, int limit, CancellationToken cancellationToken,
        bool newestFirst = false)
    {
        var ticks = at.UtcTicks;
        var rows = newestFirst
            ? await db.ReadNewestAsync(chatId, limit + 65, cancellationToken).ConfigureAwait(false)
            : await db.ReadAtOrAfterAsync(chatId, at.ToUnixTimeMilliseconds(), limit + 65, cancellationToken).ConfigureAwait(false);
        var messages = Sorted(rows.Select(Message).OfType<ChatMessage>().Where(message => message.Timestamp.UtcTicks >= ticks && AboveFloor(state, message)));
        if (newestFirst)
        {
            // Read newest down: there is more after the moment when the newest read did not reach back to it.
            var reachedBack = rows.Count < limit + 65 || rows.Any(row => row.Order < at.ToUnixTimeMilliseconds());
            return (messages.TakeLast(limit).ToArray(), messages.Count > limit || !reachedBack);
        }
        return (messages.Take(limit).ToArray(), messages.Count > limit);
    }

    private static bool AboveFloor(State state, ChatMessage message) => state.Floor is not { } floor || Position.Of(message).CompareTo(floor) >= 0;

    private async Task<ChatMessage?> HeldMessageAsync(string chatId, string messageId, CancellationToken cancellationToken)
    {
        if (!ready.Task.IsCompletedSuccessfully || !held.TryGetValue(chatId, out var chat) || !chat.State.Filled) return null;
        var row = await db.ReadOneAsync(chatId, messageId, cancellationToken).ConfigureAwait(false);
        return row is { Removed: false } ? Message(row) : null;
    }

    // Messages an event (or a change made here) brought, under the conversation's gate. They move the newest held; they move what is known whole only when
    // nothing was missed before them and none of them is missing (`whole`).
    private async Task KeepAsync(Held chat, IReadOnlyList<ChatMessage> messages, Reason reason, bool whole)
    {
        await Gated(chat, async () =>
        {
            var state = chat.State;
            if (!state.Filled) return;
            var kept = messages.Where(message => AboveFloor(state, message)).ToArray();
            if (kept.Length == 0) return;
            var top = Max(state.Newest, kept.Select(Position.Of).Max());
            var verified = whole && !chat.Suspect ? Max(state.Verified, top) : state.Verified;
            // Written while something may be missing below it: read the newest page soon, rather than leave it suspect until it is next opened.
            if (chat.Suspect) Enqueue(chat.ChatId, Work.Refresh);
            await WriteAsync(chat, kept, [], state with { Newest = top, Verified = verified }).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    private Task RemoveAsync(Held chat, IReadOnlyList<string> ids, Reason reason) =>
        Gated(chat, async () =>
        {
            if (chat.State.Filled) await WriteAsync(chat, [], ids, chat.State).ConfigureAwait(false);
        });

    private async Task Gated(Held chat, Func<Task> write)
    {
        try
        {
            await chat.Gate.WaitAsync(stop.Token).ConfigureAwait(false);
            try
            {
                if (!chat.Forgotten) await write().ConfigureAwait(false);
            }
            finally { chat.Gate.Release(); }
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !stop.IsCancellationRequested)
        {
            log($"history write failed chat={Tag(chat.ChatId)} error={failure.GetType().Name}");
        }
    }

    /// <summary>
    /// Writes a change to one conversation (the caller holds its gate) with the state it leaves the conversation in, and only once that is written takes the
    /// state and puts what changed in the event sequence. The write itself is never cancelled: a service's page in hand is kept, or nothing changes.
    /// </summary>
    /// <remarks>
    /// <paramref name="found"/>: the write is a read of the service's pages (a join or a fresh start), not an event's own messages. What it put and removed is then
    /// said as realtime events too, history-marked so no one notifies for it: a client that keeps its own copy and ignores history changes (the PC, every client
    /// below 24) learns of a message no event announced, as a provider's event would have told it. A Google Voice text the PC had read a moment before its event
    /// reached the copy stayed missing there for good (2026-10-29).
    /// </remarks>
    private async Task<HistoryWritten> WriteAsync(Held chat, IReadOnlyList<ChatMessage> puts, IReadOnlyList<string> removes, State next, bool reset = false, bool announce = true,
        bool found = false)
    {
        if (chat.Forgotten) return new(chat.Seq, [], [], false);
        var from = chat.Seq;
        var written = await db.WriteAsync(new HistoryEdit(chat.ChatId,
            puts.Select(message => new HistoryPut(message.Id, message.Timestamp.ToUnixTimeMilliseconds(), Bytes(message))).ToArray(),
            removes, Save(next), reset), CancellationToken.None).ConfigureAwait(false);
        chat.State = next;
        if (written.Skipped > 0)
        {
            // A message too large to keep is a hole: the conversation is read again rather than called whole.
            log($"history write skipped chat={Tag(chat.ChatId)} messages={written.Skipped}");
            chat.MarkSuspect();
        }
        if (!written.Changed) return written;
        chat.Seq = written.Seq;
        if (reset) chat.ResetSeq = written.Seq;
        if (announce && next.Filled && events is { } sequence)
        {
            var changed = written.Put.ToHashSet(StringComparer.Ordinal);
            var messages = puts.Where(message => changed.Contains(message.Id)).DistinctBy(message => message.Id).ToArray();
            var truncated = reset || messages.Length > WholeHistoryLimits.MaxEventMessages;
            sequence.AppendHistory(new HistoryChange(chat.ChatId, Revision(from), Revision(written.Seq), truncated ? [] : messages, truncated ? [] : written.Removed,
                reset, truncated));
            if (found)
            {
                var newest = messages.OrderByDescending(Position.Of).Take(MaxEventIds).Select(message => message.Id).ToArray();
                if (newest.Length > 0) sequence.Append(new RealtimeEvent(RealtimeKind.MessageChanged, chat.ChatId, newest, false, DateTimeOffset.UtcNow, IsHistory: true));
                if (written.Removed.Count > 0) sequence.Append(new RealtimeEvent(RealtimeKind.MessageDeleted, chat.ChatId, [.. written.Removed], ReceivedAt: DateTimeOffset.UtcNow));
                // More than one event names: a copy kept closed is read again when it opens rather than patched.
                if (messages.Length > MaxEventIds || reset) sequence.Append(new RealtimeEvent(RealtimeKind.ChatChanged, chat.ChatId, [chat.ChatId], ReceivedAt: DateTimeOffset.UtcNow));
            }
        }
        return written;
    }

    private bool Stale(Held chat)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var scope = scopeResyncs.Where(pair => chat.ChatId.StartsWith(pair.Key, StringComparison.Ordinal)).Select(pair => pair.Value).DefaultIfEmpty(0).Max();
        return chat.Suspect || chat.RefreshedAt < scope || now - chat.RefreshedAt > (long)OpenRefreshAfter.TotalMilliseconds;
    }

    // ---- The background: filling every conversation, catching up, checking ----

    private enum Work { Open = 0, Arrival = 1, Check = 2, Refresh = 3, CatchUp = 4, Initial = 5 }
    private enum Reason { Open, Event, Arrival, Check, Refresh, CatchUp, Initial, Older, Mutation }

    private void Enqueue(string chatId, Work work) => queue.Add(chatId, work);

    private async Task WorkAsync()
    {
        var next = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await queue.TakeAsync(stop.Token).ConfigureAwait(false) is { } item)
        {
            try
            {
                var service = Service(item.ChatId);
                if (next.TryGetValue(service, out var at) && at > Environment.TickCount64) await Task.Delay(TimeSpan.FromMilliseconds(at - Environment.TickCount64), stop.Token).ConfigureAwait(false);
                var chat = Chat(item.ChatId);
                if (chat.Forgotten) continue;
                if (item.Work == Work.Check)
                {
                    var summary = await chats.GetChatAsync(item.ChatId, stop.Token).ConfigureAwait(false);
                    // Only a conversation that moved past what is held is read again (a read or a mute moves nothing held), and only once for each move: a
                    // reaction moves the list past the newest message for good, and would otherwise be read again at every walk.
                    if (summary.LastActivity is not { } last || last.UtcTicks <= chat.SeenActivity
                        || chat.State.Newest is { } newest && last.UtcTicks <= newest.Ticks + TimeSpan.TicksPerSecond * 2) continue;
                    chat.SeenActivity = last.UtcTicks;
                }
                // Opening is a read someone asked for; everything else is the server's own, and waits while the service's budget says so, page by page.
                var asked = item.Work == Work.Open;
                if (!asked && !mayReadUnasked(item.ChatId))
                {
                    queue.Later(item.ChatId, item.Work, TimeSpan.FromMinutes(10), item.Tries);
                    continue;
                }
                var (_, taken) = await RefreshAsync(chat, item.Work switch
                {
                    Work.Open => Reason.Open, Work.Arrival => Reason.Arrival, Work.Check => Reason.Check, Work.CatchUp => Reason.CatchUp,
                    Work.Initial => Reason.Initial, _ => Reason.Refresh,
                }, stop.Token, asked ? null : () => mayReadUnasked(item.ChatId)).ConfigureAwait(false);
                next[service] = Environment.TickCount64 + (long)Pace.TotalMilliseconds;
                if (!taken && item.Tries < 5) queue.Later(item.ChatId, item.Work, RetryAfter * (1 << item.Tries), item.Tries + 1);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure)
            {
                log($"history work failed chat={Tag(item.ChatId)} work={item.Work} error={failure.GetType().Name}");
                if (item.Tries < 5) queue.Later(item.ChatId, item.Work, RetryAfter * (1 << item.Tries), item.Tries + 1);
            }
        }
    }

    // Walks the list now and then: a conversation not held yet is filled (the most recently active first), and one whose list moved past what is held is
    // checked. Instagram's rows are compared here, from the list it already holds, rather than read one by one.
    private async Task WalkAsync()
    {
        try { await Task.Delay(WalkDelay, stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        while (!stop.IsCancellationRequested)
        {
            var clock = Stopwatch.GetTimestamp();
            int listed = 0, filling = 0, checking = 0;
            try
            {
                string? cursor = null;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (var pages = 0; pages < 200; pages++)
                {
                    var page = await chats.GetChatsAsync(cursor, stop.Token).ConfigureAwait(false);
                    foreach (var row in page.Items)
                    {
                        // Spam is read only when someone opens it.
                        if (!seen.Add(row.Id) || row.IsSpam) continue;
                        listed++;
                        var chat = Chat(row.Id);
                        if (!chat.State.Filled) { Enqueue(row.Id, Work.Initial); filling++; continue; }
                        if (row.LastActivity is not { } last || last.UtcTicks <= chat.SeenActivity
                            || chat.State.Newest is { } newest && last.UtcTicks <= newest.Ticks + TimeSpan.TicksPerSecond * 2) continue;
                        checking++;
                        if (row.Id.StartsWith(InstagramPrefix, StringComparison.Ordinal))
                        {
                            chat.SeenActivity = last.UtcTicks;
                            Enqueue(row.Id, Work.Refresh);
                        }
                        else Enqueue(row.Id, Work.Check);
                    }
                    if (!page.HasMore || page.OldestCursor is null || page.OldestCursor == cursor) break;
                    cursor = page.OldestCursor;
                }
                log($"history walk listed={listed} filling={filling} checking={checking} held={held.Values.Count(h => h.State.Filled)} queued={queue.Count} seq={db.Seq} bytes={db.Bytes} ms={Ms(clock)}");
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { return; }
            catch (Exception failure) { log($"history walk failed listed={listed} error={failure.GetType().Name}"); }
            try { await Task.Delay(WalkEvery, stop.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---- Small parts ----

    private Held Chat(string chatId) => held.GetOrAdd(chatId, id => new Held(id));

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        try { await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); return true; }
        catch (Exception failure) when (failure is not OperationCanceledException) { return false; }
    }

    private string Revision(long seq) => db.Epoch + "." + seq.ToString(CultureInfo.InvariantCulture);

    private long? Parse(string? revision)
    {
        if (revision is null) return null;
        var dot = revision.LastIndexOf('.');
        return dot > 0 && revision.AsSpan(0, dot).SequenceEqual(db.Epoch) && long.TryParse(revision.AsSpan(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var seq)
            ? seq : null;
    }

    private static string Key(string chatId, string messageId) => chatId + "\n" + messageId;

    private static Position? Max(Position? a, Position b) => a is { } known && known.CompareTo(b) > 0 ? known : b;
    private static Position? Max(Position? a, Position? b) => b is { } other ? Max(a, other) : a;

    private static byte[] Bytes(ChatMessage message)
    {
        // Who has seen it in one order, so the same message read twice is the same bytes and takes no change number.
        var canonical = message.Delivery.SeenBy.Count > 1
            ? message with { Delivery = message.Delivery with { SeenBy = new SortedDictionary<string, bool?>(message.Delivery.SeenBy.ToDictionary(), StringComparer.Ordinal) } }
            : message;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, ProtocolJson.Options);
        // Past what a row holds (megabytes of text): kept with its words cut short rather than left out, which would be a hole in the middle of the copy.
        return bytes.Length <= MaxRowBytes ? bytes
            : JsonSerializer.SerializeToUtf8Bytes(canonical with { Text = canonical.Text?[..Math.Min(canonical.Text.Length, 65_536)] }, ProtocolJson.Options);
    }

    // Under the store's limit for one row, with room for its seal.
    private const int MaxRowBytes = 4 * 1024 * 1024 - 1024;

    private static ChatMessage? Message(HistoryRow row)
    {
        if (row.Removed) return null;
        try { return JsonSerializer.Deserialize<ChatMessage>(row.Payload, ProtocolJson.Options); }
        catch (JsonException) { return null; }
    }

    private static List<ChatMessage> Sorted(IEnumerable<ChatMessage> messages) =>
        messages.DistinctBy(message => message.Id).OrderBy(message => Position.Of(message)).ToList();

    private static string Cursor(ChatMessage oldest) => CursorPrefix + oldest.Timestamp.UtcTicks.ToString(CultureInfo.InvariantCulture) + "|"
        + Convert.ToBase64String(Encoding.UTF8.GetBytes(oldest.SortKey ?? "")) + "|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(oldest.Id));

    private static Position? ReadCursor(string cursor)
    {
        var parts = cursor[CursorPrefix.Length..].Split('|');
        if (parts.Length != 3 || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return null;
        try { return new Position(ticks, Encoding.UTF8.GetString(Convert.FromBase64String(parts[1])), Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))); }
        catch (FormatException) { return null; }
    }

    private static string Service(string chatId) => chatId.IndexOf(':') is > 0 and var colon ? chatId[..colon] : chatId;

    private static string Tag(string chatId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("history:" + chatId)).AsSpan(0, 3)).ToLowerInvariant();

    private static string Name(Reason reason) => reason.ToString().ToLowerInvariant();

    private static long Ms(long clock) => (long)Stopwatch.GetElapsedTime(clock).TotalMilliseconds;

    private static byte[] Save(State state) => JsonSerializer.SerializeToUtf8Bytes(state);

    /// <summary>Where a message sorts: its time, then its sort key, then its id (the order every copy uses).</summary>
    internal readonly record struct Position(long Ticks, string SortKey, string Id) : IComparable<Position>
    {
        public static Position Of(ChatMessage message) => new(message.Timestamp.UtcTicks, message.SortKey ?? "", message.Id);

        public int CompareTo(Position other) =>
            Ticks != other.Ticks ? Ticks.CompareTo(other.Ticks)
            : string.CompareOrdinal(SortKey, other.SortKey) is var sort and not 0 ? sort
            : string.CompareOrdinal(Id, other.Id);
    }

    /// <summary>
    /// A conversation as the copy holds it: whether it is filled, whether the service said it has nothing older, the service's cursor below the floor, the
    /// oldest held (the floor), the newest held, and how far up from the floor it is known whole. Replaced whole, and only once what it says is written.
    /// </summary>
    internal sealed record State(bool Filled = false, bool Complete = false, string? Cursor = null, Position? Floor = null, Position? Newest = null,
        Position? Verified = null)
    {
        public static State Empty { get; } = new();
    }

    // One conversation's place in the copy. Its state is replaced only under its gate (or before anyone can see it); reads take it as it stands.
    private sealed class Held(string chatId)
    {
        public string ChatId { get; } = chatId;
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public object ChainLock { get; } = new();
        public Task Tail = Task.CompletedTask;
        public volatile State State = State.Empty;
        public long Seq;
        public long ResetSeq;
        public long RefreshedAt;
        // The list's newest activity when it was last checked against what is held (in memory: a restart checks each once more).
        public long SeenActivity;
        // Something may be missing above what is known whole (a provider reconnected, an event's message could not be read, the server restarted): until its
        // newest page is read again, events still write but nothing is called whole past the verified position.
        public volatile bool Suspect = true;
        // How many times it was made suspect: a read clears the suspicion only if none came while it read.
        public long Suspicions;
        private readonly object suspectLock = new();

        public void MarkSuspect()
        {
            lock (suspectLock) { Suspicions++; Suspect = true; }
        }

        public void Settle(long seen)
        {
            lock (suspectLock) if (Suspicions == seen) Suspect = false;
        }

        public long SuspicionsNow { get { lock (suspectLock) return Suspicions; } }
        public volatile bool Forgotten;

        public static Held From(HistoryChatRow row)
        {
            var chat = new Held(row.ChatId) { Seq = row.Seq, ResetSeq = row.ResetSeq };
            if (row.State is { Length: > 0 } saved)
            {
                try
                {
                    if (JsonSerializer.Deserialize<State>(saved) is { } state)
                        chat.State = state.Filled && state.Verified is null && state.Newest is not null ? state with { Verified = state.Floor } : state;
                }
                catch (JsonException) { }
            }
            return chat;
        }
    }

    private sealed class ObservedFeed(ConversationHistory owner, IRealtimeFeed source) : IRealtimeFeed
    {
        public Task RunAsync(Func<RealtimeEvent, CancellationToken, Task> receive, CancellationToken cancellationToken) =>
            source.RunAsync((change, token) =>
            {
                owner.Observe(change);
                return receive(change, token);
            }, cancellationToken);
    }

    /// <summary>
    /// The background's work, each conversation once at the most urgent of what it was asked for, and work put off until a time. Bounded by the number of
    /// conversations, since a conversation is in it at most once.
    /// </summary>
    private sealed class WorkQueue
    {
        // Order: when it was asked for, so work of one urgency goes first come first served (the walk asks for the most recently active first).
        public sealed record Item(string ChatId, Work Work, int Tries, long NotBefore, long Order = 0);
        private readonly object gate = new();
        private readonly Dictionary<string, Item> items = new(StringComparer.Ordinal);
        private TaskCompletionSource signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool completed;
        private long order;

        public int Count { get { lock (gate) return items.Count; } }

        public void Add(string chatId, Work work) => Put(new Item(chatId, work, 0, 0));
        public void Later(string chatId, Work work, TimeSpan after, int tries = 0) => Put(new Item(chatId, work, tries, Environment.TickCount64 + (long)after.TotalMilliseconds));

        private void Put(Item item)
        {
            TaskCompletionSource wake;
            lock (gate)
            {
                if (completed) return;
                if (items.TryGetValue(item.ChatId, out var was) && was.Work <= item.Work && was.NotBefore <= item.NotBefore) return;
                items[item.ChatId] = was is null ? item with { Order = ++order }
                    : item with { Work = (Work)Math.Min((int)was.Work, (int)item.Work), Tries = Math.Max(was.Tries, item.Tries), Order = was.Order };
                wake = signal;
            }
            wake.TrySetResult();
        }

        public void Complete()
        {
            lock (gate) completed = true;
            signal.TrySetResult();
        }

        public async Task<Item?> TakeAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (gate)
                {
                    if (completed) return null;
                    var now = Environment.TickCount64;
                    var due = items.Values.Where(item => item.NotBefore <= now).OrderBy(item => item.Work).ThenBy(item => item.Order).FirstOrDefault();
                    if (due is not null)
                    {
                        items.Remove(due.ChatId);
                        return due;
                    }
                    if (signal.Task.IsCompleted) signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
                    var soonest = items.Count == 0 ? (long?)null : items.Values.Min(item => item.NotBefore) - now;
                    wait = soonest is { } delay ? Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1, delay)), cancellationToken)) : signal.Task;
                }
                try { await wait.WaitAsync(cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { return null; }
            }
        }
    }
}
