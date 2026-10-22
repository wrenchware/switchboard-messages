using Microsoft.Data.Sqlite;

namespace NativeBeeper.App.Services.Messenger;

/// <summary>
/// The schema version a Messenger device database (<c>e2ee.db</c>) records, read the way whatsmeow's store reads it (go.mau.fi/util dbutil <c>getVersion</c>): the one row of
/// <c>whatsmeow_version</c>, and 0 when there is no such table or row (a database nothing has upgraded yet). Opened read-only and unpooled, so nothing is created, upgraded or held open.
/// </summary>
/// <remarks>
/// 2026-10-12 (SWITCHBOARD.md slice 13): the server updates itself nightly from main and this PC only when released, so a move back can bring home a database a newer helper has
/// upgraded. The number to compare it with is the PC helper's own (<c>deviceSchema</c> in its hello, the length of whatsmeow's upgrade list), never a constant kept here.
/// </remarks>
internal static class MessengerDeviceSchema
{
    /// <summary>The version recorded in the database at <paramref name="path"/>; null when it is not there or is not a database this can read (which no check may take for an old one).</summary>
    public static int? Read(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            // A helper writing the file meanwhile holds the reader off for the length of a commit at most; five seconds, not the default thirty, under the server's move lock.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
            connection.Open();
            using var table = connection.CreateCommand();
            table.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND tbl_name='whatsmeow_version'";
            if (Convert.ToInt64(table.ExecuteScalar()) == 0) return 0;
            using var version = connection.CreateCommand();
            version.CommandText = "SELECT version FROM whatsmeow_version LIMIT 1";
            // No row is version 0, as dbutil reads it; a NULL in the row is an error there too, so not a version here.
            return version.ExecuteScalar() switch
            {
                null => 0,
                long number when number is >= 0 and <= int.MaxValue => (int)number,
                _ => null,
            };
        }
        catch (SqliteException) { return null; }
    }

    /// <summary>The helper's own hello: the newest device-database schema it opens, or null when it does not say (a helper too old to be asked).</summary>
    public static int? SupportedBy(System.Text.Json.JsonElement hello) =>
        hello.ValueKind == System.Text.Json.JsonValueKind.Object && hello.TryGetProperty("deviceSchema", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Number && value.TryGetInt32(out var number) && number > 0 ? number : null;
}
