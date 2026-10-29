using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Storage;

/// <summary>One message as the history database holds it: its id, where it sorts (the message's time in Unix milliseconds), the change number of its last
/// change, and its sealed-then-opened bytes (the message, or for a removed one only its id).</summary>
public sealed record HistoryRow(string MessageId, long Order, long Seq, bool Removed, byte[] Payload);

/// <summary>A conversation's own row: the change number of its newest change, the number it was last reset at (a copy older than that is replaced, not
/// patched), and the opaque state its owner keeps beside it.</summary>
public sealed record HistoryChatRow(string ChatId, long Seq, long ResetSeq, byte[]? State);

/// <summary>A message to keep: its id, its order and its bytes. Bytes equal to what is kept are not a change and take no change number.</summary>
public sealed record HistoryPut(string MessageId, long Order, byte[] Payload);

/// <summary>
/// One conversation's change, written in one transaction. <paramref name="Reset"/> first forgets every message held for it, so what follows stands alone;
/// <paramref name="Removes"/> leave a removed mark that carries a change number, so a copy that held the message learns it went. A <paramref name="State"/>
/// replaces the conversation's own row, and takes a change number only with a change to its messages or a reset.
/// </summary>
public sealed record HistoryEdit(string ChatId, IReadOnlyList<HistoryPut> Puts, IReadOnlyList<string> Removes, byte[]? State = null, bool Reset = false);

/// <summary>What a write changed: the change number it ended at (the store's newest), and the ids it put and removed. Nothing changed is both lists empty.</summary>
public sealed record HistoryWritten(long Seq, IReadOnlyList<string> Put, IReadOnlyList<string> Removed, bool Reset, int Skipped = 0)
{
    public bool Changed => Put.Count > 0 || Removed.Count > 0 || Reset;
}

/// <summary>
/// The server's whole copy of every conversation (SWITCHBOARD.md stage 8), in <c>history.db</c> beside its other stores. Each message and each conversation's
/// state is sealed with AES-GCM under a key of this file's own, which the platform's protector keeps; conversation and message ids are stored only as keyed
/// hashes, and each row's id rides inside its sealed bytes. What stays readable is a message's time, which is what orders a conversation, and the change
/// numbers. One number counts every change the file has made (<see cref="Seq"/>), under an <see cref="Epoch"/> that is new whenever the file starts over.
/// </summary>
/// <remarks>
/// A file whose key no longer opens it starts over with a new epoch, so every client that held a copy under the old one is told to replace it. One
/// connection is held open and every call takes the one gate; reads are short (a page) and a write is one conversation's change.
/// </remarks>
public sealed class HistoryDatabase(string path, IStateProtector protector) : IDisposable
{
    private const int Version = 1;
    private const int RecordLimit = 4 * 1024 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);
    private SqliteConnection? connection;
    private byte[] key = [];
    private bool disposed;

    /// <summary>Names this file's run of change numbers: a number means nothing under another epoch.</summary>
    public string Epoch { get; private set; } = "";

    /// <summary>The newest change number this file has given; 0 before the first change.</summary>
    public long Seq { get; private set; }

    /// <summary>Opens the file (making it, or starting it over when its key cannot be read) and returns every conversation's own row.</summary>
    public async Task<IReadOnlyList<HistoryChatRow>> OpenAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (connection is null) await Task.Run(OpenFile, cancellationToken).ConfigureAwait(false);
            var stored = await Task.Run(() => Meta("key") as byte[], cancellationToken).ConfigureAwait(false);
            byte[]? opened = null;
            if (stored is not null)
            {
                // Only a key that no longer opens it starts it over. A protector that cannot read its own key now (a keys volume missing, a file
                // unreadable) fails the open instead, and the host serves without a history until it can.
                try { opened = await protector.UnprotectAsync(stored).ConfigureAwait(false); } catch (CryptographicException) { opened = null; }
                if (opened is { Length: not 64 }) opened = null;
            }
            if (opened is null)
            {
                // Missing or unreadable: nothing held can be read again, so it all goes and a new epoch tells every copy to start again.
                opened = RandomNumberGenerator.GetBytes(64);
                var sealedKey = await protector.ProtectAsync(opened).ConfigureAwait(false);
                var epoch = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
                await Task.Run(() =>
                {
                    using var transaction = connection!.BeginTransaction();
                    Execute(transaction, "DELETE FROM messages; DELETE FROM chats; DELETE FROM meta;");
                    SetMeta(transaction, "key", sealedKey);
                    SetMeta(transaction, "epoch", epoch);
                    SetMeta(transaction, "seq", 0L);
                    transaction.Commit();
                }, cancellationToken).ConfigureAwait(false);
            }
            key = opened;
            Epoch = Convert.ToString(await Task.Run(() => Meta("epoch"), cancellationToken).ConfigureAwait(false)) ?? "";
            Seq = Convert.ToInt64(await Task.Run(() => Meta("seq"), cancellationToken).ConfigureAwait(false) ?? 0L);
            return await Task.Run(() =>
            {
                var rows = new List<HistoryChatRow>();
                using var command = connection!.CreateCommand();
                command.CommandText = "SELECT id, seq, reset_seq, payload FROM chats";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (Open(id, (byte[])reader[3]) is not { } plain) continue;
                    var chatId = ReadChatPayload(plain, out var state);
                    if (chatId is null) continue;
                    rows.Add(new(chatId, reader.GetInt64(1), reader.GetInt64(2), state));
                }
                return (IReadOnlyList<HistoryChatRow>)rows;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Writes one conversation's change in one transaction and says what it changed.</summary>
    public async Task<HistoryWritten> WriteAsync(HistoryEdit change, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() => Write(change), CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The newest <paramref name="limit"/> messages held for a conversation (not removed ones), newest first.</summary>
    public Task<IReadOnlyList<HistoryRow>> ReadNewestAsync(string chatId, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync(chatId, "removed=0 ORDER BY ord DESC LIMIT $limit", command => command.Parameters.AddWithValue("$limit", limit), cancellationToken);

    /// <summary>Messages held at or before <paramref name="order"/>, newest first, at most <paramref name="limit"/>; the caller orders ties within a moment.</summary>
    public Task<IReadOnlyList<HistoryRow>> ReadAtOrBeforeAsync(string chatId, long order, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync(chatId, "removed=0 AND ord<=$ord ORDER BY ord DESC LIMIT $limit", command =>
        {
            command.Parameters.AddWithValue("$ord", order);
            command.Parameters.AddWithValue("$limit", limit);
        }, cancellationToken);

    /// <summary>Messages held from <paramref name="order"/> on, oldest first, at most <paramref name="limit"/>.</summary>
    public Task<IReadOnlyList<HistoryRow>> ReadAtOrAfterAsync(string chatId, long order, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync(chatId, "removed=0 AND ord>=$ord ORDER BY ord ASC LIMIT $limit", command =>
        {
            command.Parameters.AddWithValue("$ord", order);
            command.Parameters.AddWithValue("$limit", limit);
        }, cancellationToken);

    /// <summary>Every message held between two moments, both included (removed ones too), oldest first.</summary>
    public Task<IReadOnlyList<HistoryRow>> ReadBetweenAsync(string chatId, long from, long to, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync(chatId, "ord>=$from AND ord<=$to ORDER BY ord ASC LIMIT $limit", command =>
        {
            command.Parameters.AddWithValue("$from", from);
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$limit", limit);
        }, cancellationToken);

    /// <summary>What changed in a conversation after <paramref name="since"/> (removed marks included), oldest change first, at most <paramref name="limit"/>.</summary>
    public Task<IReadOnlyList<HistoryRow>> ReadChangedAsync(string chatId, long since, int limit, CancellationToken cancellationToken = default) =>
        ReadAsync(chatId, "seq>$since ORDER BY seq ASC LIMIT $limit", command =>
        {
            command.Parameters.AddWithValue("$since", since);
            command.Parameters.AddWithValue("$limit", limit);
        }, cancellationToken);

    /// <summary>
    /// The conversations whose number is past <paramref name="since"/>, with their numbers, and the store's number, read together under the one gate so a
    /// write cannot fall between them: a revision handed out from this never skips a change. At most <paramref name="limit"/>.
    /// </summary>
    public async Task<(long Seq, IReadOnlyList<(string ChatId, long Seq)> Chats)> ReadChangedChatsAsync(long since, int limit, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() =>
            {
                var rows = new List<(string, long)>();
                using var command = connection!.CreateCommand();
                command.CommandText = "SELECT id, seq, payload FROM chats WHERE seq>$since ORDER BY seq LIMIT $limit";
                command.Parameters.AddWithValue("$since", since);
                command.Parameters.AddWithValue("$limit", limit);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (Open(reader.GetString(0), (byte[])reader[2]) is { } plain && ReadChatPayload(plain, out _) is { } chatId) rows.Add((chatId, reader.GetInt64(1)));
                return (Seq, (IReadOnlyList<(string, long)>)rows);
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>One message, removed or not; null when nothing is held under its id.</summary>
    public async Task<HistoryRow?> ReadOneAsync(string chatId, string messageId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() =>
            {
                var id = MessageKey(chatId, messageId);
                using var command = connection!.CreateCommand();
                command.CommandText = "SELECT id, ord, seq, removed, payload FROM messages WHERE id=$id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                return reader.Read() && Open(id, (byte[])reader[4]) is { } plain
                    ? new HistoryRow(messageId, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3) != 0, plain) : null;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>How many messages are held for a conversation (removed ones not counted).</summary>
    public async Task<int> CountAsync(string chatId, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() =>
            {
                using var command = connection!.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM messages WHERE chat=$chat AND removed=0";
                command.Parameters.AddWithValue("$chat", ChatKey(chatId));
                return Convert.ToInt32(command.ExecuteScalar());
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>Forgets conversations outright, rows and all (an account signed out or removed). Returns how many were held.</summary>
    public async Task<int> ForgetAsync(IReadOnlyCollection<string> chatIds, CancellationToken cancellationToken = default)
    {
        if (chatIds.Count == 0) return 0;
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() =>
            {
                var forgotten = 0;
                using var transaction = connection!.BeginTransaction();
                foreach (var chatId in chatIds)
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "DELETE FROM messages WHERE chat=$chat; DELETE FROM chats WHERE id=$chat;";
                    command.Parameters.AddWithValue("$chat", ChatKey(chatId));
                    forgotten += command.ExecuteNonQuery() > 0 ? 1 : 0;
                }
                transaction.Commit();
                return forgotten;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    /// <summary>The file's size in bytes, for the host's diagnostics.</summary>
    public long Bytes
    {
        get
        {
            try { return new FileInfo(path).Length + (File.Exists(path + "-wal") ? new FileInfo(path + "-wal").Length : 0); }
            catch (Exception) { return 0; }
        }
    }

    public void Dispose()
    {
        gate.Wait();
        try
        {
            disposed = true;
            connection?.Dispose();
            connection = null;
        }
        finally { gate.Release(); }
    }

    private async Task<IReadOnlyList<HistoryRow>> ReadAsync(string chatId, string where, Action<SqliteCommand> bind, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Ready();
            return await Task.Run(() =>
            {
                var rows = new List<HistoryRow>();
                using var command = connection!.CreateCommand();
                command.CommandText = "SELECT id, ord, seq, removed, payload FROM messages WHERE chat=$chat AND " + where;
                command.Parameters.AddWithValue("$chat", ChatKey(chatId));
                bind(command);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    // A row that does not open is a miss, as a damaged row of the search archive is.
                    if (Open(reader.GetString(0), (byte[])reader[4]) is not { } plain) continue;
                    var removed = reader.GetInt64(3) != 0;
                    var messageId = removed ? Encoding.UTF8.GetString(plain) : ReadMessageId(plain);
                    if (messageId is null) continue;
                    rows.Add(new(messageId, reader.GetInt64(1), reader.GetInt64(2), removed, plain));
                }
                return (IReadOnlyList<HistoryRow>)rows;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private HistoryWritten Write(HistoryEdit change)
    {
        var chat = ChatKey(change.ChatId);
        var seq = Seq;
        var put = new List<string>();
        var removed = new List<string>();
        var skipped = 0;
        using var transaction = connection!.BeginTransaction();
        if (change.Reset)
        {
            using var clear = connection.CreateCommand();
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM messages WHERE chat=$chat";
            clear.Parameters.AddWithValue("$chat", chat);
            clear.ExecuteNonQuery();
        }
        using (var find = connection.CreateCommand())
        using (var upsert = connection.CreateCommand())
        {
            find.Transaction = transaction;
            find.CommandText = "SELECT hash, removed FROM messages WHERE id=$id";
            var findId = find.Parameters.Add("$id", SqliteType.Text);
            upsert.Transaction = transaction;
            upsert.CommandText = "INSERT INTO messages(id, chat, ord, seq, removed, hash, payload) VALUES($id, $chat, $ord, $seq, $removed, $hash, $payload) " +
                "ON CONFLICT(id) DO UPDATE SET ord=excluded.ord, seq=excluded.seq, removed=excluded.removed, hash=excluded.hash, payload=excluded.payload";
            var id = upsert.Parameters.Add("$id", SqliteType.Text);
            upsert.Parameters.AddWithValue("$chat", chat);
            var ord = upsert.Parameters.Add("$ord", SqliteType.Integer);
            var rowSeq = upsert.Parameters.Add("$seq", SqliteType.Integer);
            var isRemoved = upsert.Parameters.Add("$removed", SqliteType.Integer);
            var hash = upsert.Parameters.Add("$hash", SqliteType.Blob);
            var payload = upsert.Parameters.Add("$payload", SqliteType.Blob);
            foreach (var item in change.Puts)
            {
                // Past what a row holds: left out and counted, and the caller reads the conversation again rather than call it whole.
                if (item.Payload.Length > RecordLimit) { skipped++; continue; }
                var rowId = MessageKey(change.ChatId, item.MessageId);
                var digest = Digest(item.Payload);
                findId.Value = rowId;
                using (var reader = find.ExecuteReader())
                {
                    if (reader.Read() && reader.GetInt64(1) == 0 && reader[0] is byte[] was && was.AsSpan().SequenceEqual(digest)) continue;
                }
                id.Value = rowId; ord.Value = item.Order; rowSeq.Value = ++seq; isRemoved.Value = 0; hash.Value = digest; payload.Value = Seal(rowId, item.Payload);
                upsert.ExecuteNonQuery();
                put.Add(item.MessageId);
            }
            using var mark = connection.CreateCommand();
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE messages SET seq=$seq, removed=1, hash=NULL, payload=$payload WHERE id=$id AND removed=0";
            var markId = mark.Parameters.Add("$id", SqliteType.Text);
            var markSeq = mark.Parameters.Add("$seq", SqliteType.Integer);
            var markPayload = mark.Parameters.Add("$payload", SqliteType.Blob);
            foreach (var messageId in change.Removes.Distinct(StringComparer.Ordinal))
            {
                var rowId = MessageKey(change.ChatId, messageId);
                markId.Value = rowId; markSeq.Value = seq + 1; markPayload.Value = Seal(rowId, Encoding.UTF8.GetBytes(messageId));
                if (mark.ExecuteNonQuery() == 0) continue;
                seq++;
                removed.Add(messageId);
            }
        }
        var changed = put.Count > 0 || removed.Count > 0 || change.Reset;
        if (change.Reset && put.Count == 0 && removed.Count == 0) seq++;
        if (changed || change.State is not null)
        {
            using var chatRow = connection.CreateCommand();
            chatRow.Transaction = transaction;
            // A conversation's number is its newest change's; a state alone moves nothing a copy holds, so it keeps the number it had.
            chatRow.CommandText = "INSERT INTO chats(id, seq, reset_seq, payload) VALUES($id, $seq, $reset, $payload) ON CONFLICT(id) DO UPDATE SET " +
                "seq=CASE WHEN $changed THEN excluded.seq ELSE chats.seq END, reset_seq=CASE WHEN $isReset THEN excluded.reset_seq ELSE chats.reset_seq END, " +
                "payload=CASE WHEN $hasState THEN excluded.payload ELSE chats.payload END";
            chatRow.Parameters.AddWithValue("$id", chat);
            chatRow.Parameters.AddWithValue("$seq", changed ? seq : 0L);
            chatRow.Parameters.AddWithValue("$reset", change.Reset ? seq : 0L);
            chatRow.Parameters.AddWithValue("$changed", changed ? 1 : 0);
            chatRow.Parameters.AddWithValue("$isReset", change.Reset ? 1 : 0);
            chatRow.Parameters.AddWithValue("$hasState", change.State is not null ? 1 : 0);
            chatRow.Parameters.AddWithValue("$payload", Seal(chat, ChatPayload(change.ChatId, change.State)));
            chatRow.ExecuteNonQuery();
        }
        if (seq != Seq) SetMeta(transaction, "seq", seq);
        transaction.Commit();
        Seq = seq;
        return new(seq, put, removed, change.Reset, skipped);
    }

    private void OpenFile()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var opened = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        try
        {
            opened.Open();
            using var command = opened.CreateCommand();
            // The write-ahead log: one change is one append rather than a rollback journal per statement (the Messenger cold start's lesson).
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
            command.ExecuteNonQuery();
            command.CommandText = "PRAGMA user_version";
            var version = Convert.ToInt32(command.ExecuteScalar());
            if (version > Version) throw new InvalidDataException("History store requires a newer server.");
            if (version == 0)
            {
                command.CommandText = "CREATE TABLE IF NOT EXISTS meta(name TEXT PRIMARY KEY, value);" +
                    "CREATE TABLE IF NOT EXISTS chats(id TEXT PRIMARY KEY, seq INTEGER NOT NULL, reset_seq INTEGER NOT NULL, payload BLOB NOT NULL);" +
                    "CREATE TABLE IF NOT EXISTS messages(id TEXT PRIMARY KEY, chat TEXT NOT NULL, ord INTEGER NOT NULL, seq INTEGER NOT NULL, removed INTEGER NOT NULL, hash BLOB, payload BLOB NOT NULL);" +
                    "CREATE INDEX IF NOT EXISTS messages_chat_ord ON messages(chat, ord);" +
                    "CREATE INDEX IF NOT EXISTS messages_chat_seq ON messages(chat, seq);" +
                    "CREATE INDEX IF NOT EXISTS chats_seq ON chats(seq);" +
                    $"PRAGMA user_version={Version};";
                command.ExecuteNonQuery();
            }
            connection = opened;
        }
        catch { opened.Dispose(); throw; }
    }

    private void Ready()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (connection is null || key.Length != 64) throw new InvalidOperationException("The history store is not open.");
    }

    private object? Meta(string name)
    {
        using var command = connection!.CreateCommand();
        command.CommandText = "SELECT value FROM meta WHERE name=$name";
        command.Parameters.AddWithValue("$name", name);
        var value = command.ExecuteScalar();
        return value is DBNull ? null : value;
    }

    private void SetMeta(SqliteTransaction transaction, string name, object value)
    {
        using var command = connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO meta(name, value) VALUES($name, $value) ON CONFLICT(name) DO UPDATE SET value=excluded.value";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private void Execute(SqliteTransaction transaction, string sql)
    {
        using var command = connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private string ChatKey(string chatId) => Convert.ToHexString(HMACSHA256.HashData(key.AsSpan(32, 32), Encoding.UTF8.GetBytes("chat\n" + chatId)));
    private string MessageKey(string chatId, string messageId) =>
        Convert.ToHexString(HMACSHA256.HashData(key.AsSpan(32, 32), Encoding.UTF8.GetBytes("message\n" + chatId + "\n" + messageId)));
    private byte[] Digest(byte[] payload) => HMACSHA256.HashData(key.AsSpan(32, 32), payload).AsSpan(0, 16).ToArray();

    private byte[] Seal(string rowId, byte[] plain)
    {
        var output = new byte[28 + plain.Length];
        RandomNumberGenerator.Fill(output.AsSpan(0, 12));
        using var aes = new AesGcm(key.AsSpan(0, 32), 16);
        aes.Encrypt(output.AsSpan(0, 12), plain, output.AsSpan(28), output.AsSpan(12, 16), Encoding.UTF8.GetBytes(rowId));
        return output;
    }

    private byte[]? Open(string rowId, byte[] sealedBytes)
    {
        if (sealedBytes.Length < 28 || sealedBytes.Length - 28 > RecordLimit) return null;
        try
        {
            var plain = new byte[sealedBytes.Length - 28];
            using var aes = new AesGcm(key.AsSpan(0, 32), 16);
            aes.Decrypt(sealedBytes.AsSpan(0, 12), sealedBytes.AsSpan(28), sealedBytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes(rowId));
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    // A conversation's row: its id's length, its id, then its owner's state.
    private static byte[] ChatPayload(string chatId, byte[]? state)
    {
        var id = Encoding.UTF8.GetBytes(chatId);
        var output = new byte[4 + id.Length + (state?.Length ?? 0)];
        BitConverter.TryWriteBytes(output.AsSpan(0, 4), id.Length);
        id.CopyTo(output, 4);
        state?.CopyTo(output, 4 + id.Length);
        return output;
    }

    private static string? ReadChatPayload(byte[] plain, out byte[]? state)
    {
        state = null;
        if (plain.Length < 4) return null;
        var length = BitConverter.ToInt32(plain, 0);
        if (length < 0 || length > plain.Length - 4) return null;
        if (plain.Length > 4 + length) state = plain[(4 + length)..];
        return Encoding.UTF8.GetString(plain, 4, length);
    }

    // A message's bytes are its JSON, whose "id" (or "Id") is read without binding the rest.
    private static string? ReadMessageId(byte[] plain)
    {
        try
        {
            var reader = new System.Text.Json.Utf8JsonReader(plain);
            if (!reader.Read() || reader.TokenType != System.Text.Json.JsonTokenType.StartObject) return null;
            while (reader.Read() && reader.TokenType == System.Text.Json.JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) return null;
                if (string.Equals(name, "id", StringComparison.OrdinalIgnoreCase)) return reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
            return null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
