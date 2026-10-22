using System.Runtime.CompilerServices;
using System.Threading.Channels;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.Core.Services;

namespace NativeBeeper.App.Services.Messenger;

// Typing from every network into the one indicator, and this account's typing out to whichever network owns the
// conversation.
internal sealed class TypingRouter(GoogleMessagesBackend google, MessengerBackend messenger, InstagramBackend instagram,
    NativeBeeper.Presentation.Services.ComposerActivity typing, NativeBeeper.Presentation.Services.PrivateReading? reading = null) : ITypingService, IComposerActivitySink
{
    public bool CanReceive => google.CanReceive || messenger.CanReceive || instagram.CanReceive;
    public bool CanSend => google.CanSend || messenger.CanSend || instagram.CanSend;

    // Every actual composer edit yields Google history indexing, including edits inside the five-second window
    // where the remote typing notification is deliberately throttled. Both are noted: Google yields its background
    // indexing, and the shared one lets the synchronization apply stand out of the way for every provider, not just
    // that one. Messenger and Instagram put off telling the other end the typing stopped.
    public void NoteComposerActivity(string? chatId)
    {
        google.NoteComposerActivity(chatId);
        messenger.NoteComposerActivity(chatId);
        instagram.NoteComposerActivity(chatId);
        typing.Note(chatId);
    }

    // As MessagingRouter.MarkReadAsync: a typing start for a private-reading service that did not come through
    // PrivateReadingGate is refused and logged. A stop is never refused; it only ends a start that was sent.
    // A start that goes on to a provider is noted (TypingPings) for the send line's since_typing_ms.
    public Task SetTypingAsync(string chatId, bool isTyping, CancellationToken cancellationToken = default)
    {
        if (isTyping && NativeBeeper.Presentation.Services.PrivateReadingGate.Refuses(reading, chatId, "typing")) return Task.CompletedTask;
        var route = TypingRoute.Of(chatId);
        if (isTyping && route is TypingProvider.Instagram or TypingProvider.Messenger or TypingProvider.Google)
            NativeBeeper.Presentation.Services.TypingPings.Shared.Sent(chatId);
        return route switch
        {
            TypingProvider.Instagram => instagram.SetTypingAsync(chatId, isTyping, cancellationToken),
            TypingProvider.Messenger => messenger.SetTypingAsync(chatId, isTyping, cancellationToken),
            TypingProvider.Google => google.SetTypingAsync(chatId, isTyping, cancellationToken),
            _ => Task.CompletedTask,
        };
    }

    public async IAsyncEnumerable<TypingUpdate> WatchAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var merged = Channel.CreateUnbounded<TypingUpdate>();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        async Task Pump(ITypingService source)
        {
            try { await foreach (var update in source.WatchAsync(stop.Token)) await merged.Writer.WriteAsync(update, stop.Token); }
            catch (OperationCanceledException) { }
        }
        var pumps = Task.WhenAll(Pump(google), Pump(messenger), Pump(instagram));
        _ = pumps.ContinueWith(_ => merged.Writer.TryComplete(), TaskScheduler.Default);
        try
        {
            await foreach (var update in merged.Reader.ReadAllAsync(cancellationToken)) yield return update;
        }
        finally { stop.Cancel(); }
    }
}
