using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Host.Network;

namespace NativeBeeper.Server.Linux;

/// <summary>
/// The server's door on the owner's tailnet (SWITCHBOARD.md 4.7, chosen 2026-10-15): <c>messages-tailnet</c> beside the server, a Tailscale node of
/// its own in this container (experiments/Tailnet/door), run while <c>MESSAGES_TAILNET=1</c>. It passes each tailnet device's connection to the network host's relay
/// socket behind a line naming the device (<see cref="TailnetRelay"/>), so Unraid publishes the port on the home network only and the
/// server still tells devices apart.
/// <para>
/// The door is started with an empty environment (it inherits no helper key, no password, no Tailscale auth key) and its node's state in the keys volume's
/// <c>Tailnet</c> folder. Its standard output is read line by line: the sign-in link and the Tailnet Lock key go to the container's own log, as a pairing code
/// does; its report for the server's page (state, sign-in link, owner, key expiry, Tailnet Lock, access rules) to the <see cref="TailnetBoard"/> the page reads,
/// held in memory and never logged; and every other line, fixed words and counts, to the server's log. A door that ends by itself is started again, waiting 2 s and doubling to 5 minutes (back
/// to 2 s after a run of 10 minutes). The stop ends its input, which it takes as the word to close its node, and kills it after 5 s.
/// </para>
/// </summary>
internal sealed partial class TailnetDoor
{
    public const string Executable = "messages-tailnet";
    public const string StateFolder = "Tailnet";

    /// <summary>The relay socket: a private folder in the container's own temporary space, never a volume, made fresh by each start.</summary>
    public static string RelaySocket => Path.Combine(Path.GetTempPath(), "messages-tailnet", "relay.sock");

    /// <summary>The first wait before a door that ended is started again; doubled each time up to <see cref="LongestWait"/>. A check shortens it.</summary>
    internal static TimeSpan FirstWait { get; set; } = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan LongestWait = TimeSpan.FromMinutes(5);
    /// <summary>A run this long resets the wait: the door ending after it is not a door that cannot start.</summary>
    internal static readonly TimeSpan Settled = TimeSpan.FromMinutes(10);
    /// <summary>How long the stop gives the door to close its node after its input ends.</summary>
    internal static TimeSpan StopWait { get; set; } = TimeSpan.FromSeconds(5);
    private const int LongestLine = 1024;

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$", RegexOptions.CultureInvariant)]
    private static partial Regex Name();

    // The door's own lines (main.go's say): "tailnet" and words, each alone or with a value of letters, digits, '_' and '-'.
    [GeneratedRegex("^tailnet(?: [a-z_]+(?:=[A-Za-z0-9_-]+)?)+$", RegexOptions.CultureInvariant)]
    private static partial Regex LogLine();

    [GeneratedRegex("^tailnet login url=https://[A-Za-z0-9.-]+(?::[0-9]+)?/[A-Za-z0-9/_.~%?=&+:-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex LoginLine();

    // What the server's page shows (page.go): one JSON object, held for the page and never written anywhere.
    [GeneratedRegex(@"^tailnet page \{.*\}$", RegexOptions.CultureInvariant)]
    private static partial Regex PageLine();

    [GeneratedRegex("^tailnet lock sign node_key=nodekey:[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LockLine();

    private readonly ProcessStartInfo start;
    private readonly Action<string> log, console;
    private readonly TailnetBoard? board;
    private readonly Func<ProcessStartInfo, Process> launch;
    private readonly CancellationTokenSource stopping = new();
    private readonly object gate = new();
    private Process? running;
    private Task supervising = Task.CompletedTask;
    private int unreadable;

    private TailnetDoor(ProcessStartInfo start, Action<string> log, Action<string> console, Func<ProcessStartInfo, Process> launch, TailnetBoard? board)
    {
        this.start = start; this.log = log; this.console = console; this.launch = launch; this.board = board;
    }

    /// <summary>
    /// Starts the door for a server on <paramref name="port"/> whose relay socket is <paramref name="socket"/>, or says why not (<c>server tailnet refused
    /// reason=name|no_door</c>) and returns null. <paramref name="console"/> is the container's own output; <paramref name="launch"/> a check's stand-in for the
    /// process start (the server's is <see cref="HelperJob.Start"/>, from the one thread a parent-death signal may come from).
    /// </summary>
    public static TailnetDoor? Start(string executable, string stateFolder, string socket, string name, int port, Action<string> log, Action<string> console,
        Func<ProcessStartInfo, Process>? launch = null, TailnetBoard? board = null)
    {
        if (!Name().IsMatch(name))
        {
            log("server tailnet refused reason=name");
            return null;
        }
        if (!File.Exists(executable))
        {
            log("server tailnet refused reason=no_door");
            return null;
        }
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(stateFolder);
        else Directory.CreateDirectory(stateFolder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var door = new TailnetDoor(StartInfo(executable, stateFolder, socket, name, port), log, console, launch ?? HelperJob.Start, board);
        door.supervising = Task.Run(door.SuperviseAsync);
        return door;
    }

    /// <summary>The door's command: its arguments, its pipes, and an environment holding nothing but the server's process id (parentwatch_linux.go).</summary>
    internal static ProcessStartInfo StartInfo(string executable, string stateFolder, string socket, string name, int port)
    {
        var start = new ProcessStartInfo(Path.GetFullPath(executable))
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in new[] { "--state", stateFolder, "--socket", socket, "--hostname", name, "--port", port.ToString(CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(argument);
        start.Environment.Clear();
        if (OperatingSystem.IsLinux()) start.Environment["MESSAGES_HELPER_PARENT_PID"] = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return start;
    }

    /// <summary>
    /// Where one of the door's lines goes: <c>page</c> (held for the server's page alone, <see cref="TailnetBoard"/>), <c>console</c> (the container's own log),
    /// <c>log</c> (the server's), or nowhere (<c>unreadable</c>).
    /// </summary>
    internal static string Route(string line) =>
        line.Length > LongestLine ? "unreadable"
        : PageLine().IsMatch(line) ? "page"
        : LoginLine().IsMatch(line) || LockLine().IsMatch(line) ? "console"
        : LogLine().IsMatch(line) && !line.StartsWith("tailnet login", StringComparison.Ordinal) && !line.StartsWith("tailnet lock sign", StringComparison.Ordinal) ? "log"
        : "unreadable";

    private void Take(string line)
    {
        switch (Route(line))
        {
            case "console": console(line); break;
            case "log": log(line); break;
            case "page":
                if (TailnetView.Parse(line["tailnet page ".Length..]) is { } view) board?.Set(view);
                else Interlocked.Increment(ref unreadable);
                break;
            default: Interlocked.Increment(ref unreadable); break;
        }
    }

    private async Task SuperviseAsync()
    {
        var wait = FirstWait;
        while (!stopping.IsCancellationRequested)
        {
            Process? process = null;
            try { process = launch(start); }
            catch (Exception failure) { log($"server tailnet start failed type={failure.GetType().Name}"); }
            if (process is not null)
            {
                var ran = Stopwatch.StartNew();
                lock (gate) running = process;
                log("server tailnet started");
                // A stop that came while it was starting found nothing to end.
                if (stopping.IsCancellationRequested) EndInput(process);
                var errorLines = 0;
                var errors = Task.Run(async () =>
                {
                    // Go writes a panic here; it can name addresses, so it is only counted.
                    try { while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is not null) errorLines++; }
                    catch (Exception) { }
                });
                try
                {
                    while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line) Take(line);
                }
                catch (Exception) { }
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch (Exception) { }
                await errors.ConfigureAwait(false);
                lock (gate) running = null;
                // Until the next run says otherwise, the page says Tailscale is starting rather than what a door that ended last said.
                board?.Set(null);
                var code = Code(process);
                var extra = (Interlocked.Exchange(ref unreadable, 0) is var dropped and > 0 ? $" unreadable_lines={dropped}" : "") + (errorLines > 0 ? $" error_lines={errorLines}" : "");
                process.Dispose();
                if (stopping.IsCancellationRequested)
                {
                    log($"server tailnet stopped code={code}{extra}");
                    break;
                }
                if (ran.Elapsed >= Settled) wait = FirstWait;
                log($"server tailnet exited code={code} restart_s={(long)Math.Ceiling(wait.TotalSeconds)}{extra}");
            }
            try { await Task.Delay(wait, stopping.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            wait = wait * 2 > LongestWait ? LongestWait : wait * 2;
        }
    }

    private static string Code(Process process)
    {
        try { return process.ExitCode.ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return "unknown"; }
    }

    private static void EndInput(Process process)
    {
        try { process.StandardInput.Close(); } catch (Exception) { }
    }

    /// <summary>
    /// Ends the door: no new start, its input ended (the door closes its node and exits), and after <see cref="StopWait"/> a kill. Call it after the network host
    /// has stopped, so the answers it drains still reach the devices through the door.
    /// </summary>
    public async Task StopAsync()
    {
        stopping.Cancel();
        Process? process;
        lock (gate) process = running;
        if (process is not null)
        {
            EndInput(process);
            try { await process.WaitForExitAsync().WaitAsync(StopWait).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                log("server tailnet stop forced");
                try { process.Kill(); } catch (Exception) { }
            }
            catch (Exception) { }
        }
        try { await supervising.WaitAsync(StopWait).ConfigureAwait(false); }
        catch (Exception) { }
    }
}
