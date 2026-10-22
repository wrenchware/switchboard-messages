using System.Collections.Concurrent;
using System.Text;

namespace NativeBeeper.App.Services;

// The application's own diagnostic log, beside each provider's: how the interface itself behaved, in durations and
// counts. Nothing here records a conversation, a person or anything typed. A run replaces the previous file and stops
// at its own cap, so the space it takes is bounded by the cap rather than by how long the app has been in use.
//
// Lines are queued and written on a background thread. This log exists to find work that holds up the interface
// thread, and a file append on that thread is exactly such work: an instrument that disturbs what it measures is
// worse than none.
internal static class AppLog
{
    private const long Limit = 1 << 20;
    // A diagnostic waiting for something rare (the history layout watch) keeps a small reserve past the cap. The periodic
    // responsiveness lines fill a megabyte in under two hours of active use, and a glitch seen by someone who has had the
    // app open all day would otherwise leave nothing behind.
    private const long ReservedLimit = Limit + (256 << 10);
    // The record of a crash gets a reserve of its own past both. A crash in the long sessions where the paste crash was
    // seen would otherwise find the file already full and leave nothing at all.
    private const long CrashLimit = ReservedLimit + (64 << 10);
    private static readonly ConcurrentQueue<(string Text, long Cap)> Pending = new();
    private static readonly object Gate = new();
    private static int draining;
    private static string? path;
    private static long written;

    private static string fileName = "app.log", openingLine = "app started";

    // A process that is not the window (MessagesServer.exe) keeps its own file, chosen before its first line.
    public static void UseFile(string name, string opening) { fileName = name; openingLine = opening; }

    public static string FilePath => AppPaths.Combine(fileName);

    // Also given every line, timestamped and as written, on the drain's thread: the Linux server's standard output, which is
    // where a container's log is read. Set before the first line.
    public static Action<string>? Mirror { get; set; }

    private static string? Path
    {
        get
        {
            if (path is not null) return path.Length == 0 ? null : path;
            try
            {
                Directory.CreateDirectory(AppPaths.DataRoot);
                path = FilePath;
                LogRotation.StartFresh(path, openingLine);
                written = 0;
            }
            catch (Exception) { path = ""; return null; }
            return path;
        }
    }

    public static void Write(string line) => Write(line, reserved: false);

    public static void Write(string line, bool reserved) => Enqueue(line, reserved ? ReservedLimit : Limit);

    // For a process about to end: queued with its own reserve and written before this returns, on the calling thread.
    // WinUI ends the process the moment an unhandled exception's handler returns, and a line left to the background
    // drain was, in all likelihood, never written.
    public static void WriteNow(string line)
    {
        Enqueue(line, CrashLimit);
        Flush();
    }

    // Writes whatever is queued, now, on the calling thread: the last lines of a run, which the process ending would
    // otherwise take with it. The same loop and lock as the background drain, so order and caps are unchanged.
    public static void Flush()
    {
        try { DrainPending(); } catch (Exception) { }
    }

    private static void Enqueue(string line, long cap)
    {
        if (written > cap || !Diagnostics.Recording) return;
        Pending.Enqueue(($"{DateTimeOffset.Now:HH:mm:ss.fff} {line}\n", cap));
        if (Interlocked.CompareExchange(ref draining, 1, 0) == 0) _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        do
        {
            DrainPending();
            Interlocked.Exchange(ref draining, 0);
            // A line queued between the last dequeue and releasing the flag would otherwise wait for the next write.
        } while (!Pending.IsEmpty && Interlocked.CompareExchange(ref draining, 1, 0) == 0);
    }

    private static void DrainPending()
    {
        lock (Gate)
        {
            while (Pending.TryDequeue(out var entry))
            {
                if (Mirror is { } mirror) try { mirror(entry.Text); } catch (Exception) { }
                if (Path is not { } file || written > entry.Cap) continue;
                try { File.AppendAllText(file, entry.Text, Encoding.UTF8); written += entry.Text.Length; }
                catch (Exception) { path = ""; }
            }
        }
    }
}
