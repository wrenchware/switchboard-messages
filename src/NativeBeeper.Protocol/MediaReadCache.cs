namespace NativeBeeper.Protocol;

/// <summary>
/// The bytes of a few media reads, kept for a short while so that the chunks of one read (<c>media.read</c>) are cut from one
/// fetch instead of asking the provider again per chunk. Not a media cache: the disk cache under the routers keeps pictures for
/// days, and this lives for the seconds a client takes to read them through. Bounded in bytes and in number; a read that fails is
/// not kept; two callers asking for the same key at once share one fetch.
/// </summary>
public sealed class MediaReadCache(TimeProvider? time = null)
{
    private sealed class Entry(string key)
    {
        public string Key { get; } = key;
        public readonly TaskCompletionSource<byte[]> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Size;
        public DateTimeOffset Expires = DateTimeOffset.MaxValue;
        public bool Ready;
    }

    private readonly TimeProvider clock = time ?? TimeProvider.System;
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private long held;

    /// <summary>The most items held at once.</summary>
    public int MaxEntries { get; set; } = 8;

    /// <summary>The most bytes held at once; an item larger than this is handed to the caller and not kept (so each chunk of it fetches again).</summary>
    public long MaxBytes { get; set; } = 512L * 1024 * 1024;

    /// <summary>How long an item stays after it was last asked for.</summary>
    public TimeSpan Lifetime { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an item stays once its last chunk has gone out: a read that is over need not hold memory, but a second look right after is free.</summary>
    public TimeSpan FinishedLifetime { get; set; } = TimeSpan.FromSeconds(2);

    public int Count { get { lock (gate) return entries.Count; } }
    public long HeldBytes { get { lock (gate) return held; } }

    /// <summary>The bytes for <paramref name="key"/>: those kept, or what <paramref name="fetch"/> returns (once, however many callers ask).</summary>
    public async Task<byte[]> GetAsync(string key, Func<CancellationToken, Task<byte[]>> fetch, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            Entry entry;
            bool owner;
            lock (gate)
            {
                Purge();
                if (entries.TryGetValue(key, out entry!))
                {
                    owner = false;
                    if (entry.Ready) entry.Expires = clock.GetUtcNow() + Lifetime;
                }
                else
                {
                    entry = new Entry(key);
                    entries[key] = entry;
                    owner = true;
                }
            }

            if (owner)
            {
                // A fetch nobody else is waiting on that fails leaves a faulted task nobody reads.
                _ = entry.Done.Task.ContinueWith(static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                byte[] bytes;
                try { bytes = await fetch(cancellationToken).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    lock (gate) { if (entries.TryGetValue(key, out var current) && current == entry) entries.Remove(key); }
                    entry.Done.TrySetException(ex);
                    throw;
                }
                lock (gate)
                {
                    if (bytes.LongLength > MaxBytes) entries.Remove(key);
                    else
                    {
                        entry.Size = bytes.LongLength;
                        entry.Ready = true;
                        entry.Expires = clock.GetUtcNow() + Lifetime;
                        held += entry.Size;
                        Evict(entry);
                    }
                }
                entry.Done.TrySetResult(bytes);
                return bytes;
            }

            try { return await entry.Done.Task.WaitAsync(cancellationToken).ConfigureAwait(false); }
            // The caller whose fetch this was gave up; this one did not, so it asks again.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && attempt < 2) { }
        }
    }

    /// <summary>Marks the item's last chunk as sent: it is dropped after <see cref="FinishedLifetime"/> rather than <see cref="Lifetime"/>.</summary>
    public void Finish(string key)
    {
        lock (gate)
        {
            if (entries.TryGetValue(key, out var entry) && entry.Ready)
            {
                var soon = clock.GetUtcNow() + FinishedLifetime;
                if (soon < entry.Expires) entry.Expires = soon;
            }
        }
    }

    // Caller holds the gate. Entries still being fetched are never dropped.
    private void Purge()
    {
        var now = clock.GetUtcNow();
        foreach (var entry in entries.Values.Where(entry => entry.Ready && entry.Expires <= now).ToList()) Drop(entry);
    }

    // Caller holds the gate. Drops the least recently used finished items until the bounds hold, never the one just stored.
    private void Evict(Entry keep)
    {
        while (entries.Count > MaxEntries || held > MaxBytes)
        {
            var oldest = entries.Values.Where(entry => entry.Ready && entry != keep).OrderBy(entry => entry.Expires).FirstOrDefault();
            if (oldest is null) break;
            Drop(oldest);
        }
    }

    private void Drop(Entry entry)
    {
        if (entries.Remove(entry.Key)) held -= entry.Size;
    }
}
