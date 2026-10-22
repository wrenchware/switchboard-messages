using System.Diagnostics;
using System.Runtime.InteropServices;
using NativeBeeper.App.Services;
using NativeBeeper.App.Services.GoogleMessages;
using NativeBeeper.App.Services.GoogleVoice;
using NativeBeeper.App.Services.Instagram;
using NativeBeeper.App.Services.Messenger;
using NativeBeeper.GoogleMessages.Spike;
using NativeBeeper.Host;

namespace NativeBeeper.Server.Linux;

// messages-server (SWITCHBOARD.md stage 4): the backends for one data root on Linux, as a console service. One per root (the lease). Logs go to
// standard output, where a container's log is read, and to <data>/server.log, in the Windows server's words. Nothing connects unless it was started
// to restore.
public static class Program
{
    private const string PagePasswordVariable = "MESSAGES_ADMIN_PASSWORD";

    private static int Main(string[] args)
    {
        var prelude = Stopwatch.StartNew();
        // Before anything is read, written or connected: an argument this server does not know is a typo, and ignoring it would start a full server (and connect the accounts)
        // where a reset or a pairing was meant.
        if (LinuxServerOptions.Problem(args) is { } problem)
        {
            Console.Error.WriteLine("messages-server: " + problem + ". Nothing was started.");
            return 2;
        }
        // Everything this host and its helpers create (stores, logs, the helper's databases) is its owner's alone, whatever mask the shell or the
        // image left; the helpers inherit it.
        var mask = PrivateFiles();
        // The server page's password, taken out of the environment before any helper or browser can be started, so none of them inherits it (ServerPage).
        var pagePassword = Environment.GetEnvironmentVariable(PagePasswordVariable);
        Environment.SetEnvironmentVariable(PagePasswordVariable, null);
        var options = LinuxServerOptions.Parse(args, Environment.GetEnvironmentVariable);
        var root = Path.GetFullPath(options.DataRoot);
        // Before anything is written: no log can be opened in a folder that is not there, so the refusal goes to standard output only.
        if (DataFolder.Refusal(root) is { } unusable) return Refused(unusable, toFile: false);
        AppPaths.UseHostRoot(root);
        // A reset of the shared state is a run of its own (stage 6 slice 13d): no log of its own, no keys, no helper; it takes the lease itself.
        if (args.Contains(SpaceReset.Argument)) return ResetSpace(root);
        // Forgetting the hold the accounts that moved back left is a run of its own as well (it takes the lease and needs the word typed).
        if (args.Contains(HeldClear.Argument)) return ClearHeld(root, options.KeysRoot);
        // Before the log: a refused start must not replace the log of the server that holds the lease.
        using var lease = HostLease.TryAcquire(root);
        if (lease is null) return Refused("host_locked", toFile: true);

        AppLog.Mirror = Console.Out.Write;
        // The keeper's renewal lines (`browser early=...`) and the rest of the sign-in history, beside the server's own: otherwise only a file in the container.
        GoogleMessagesLog.HistoryMirror = Console.Out.Write;
        AppLog.UseFile("server.log", "server log opened");
        Diagnostics.ProviderLogFile = "server.log";
        var previousExit = RunMarker.Begin(RunMarkerPath);
        AppDomain.CurrentDomain.UnhandledException += (_, error) =>
        {
            // Code locations only: an exception's message can carry private content.
            AppLog.WriteNow($"unhandled exception type={(error.ExceptionObject as Exception)?.GetType().Name ?? "unknown"} thread=worker");
            FlushLogs();
        };
        var (keys, refusal) = HostKeys.Prepare(root, options.KeysRoot, AppLog.Write);
        if (keys is null)
        {
            AppLog.Write($"server start refused reason={refusal}");
            Console.Error.WriteLine("Messages Server didn't start: " + Explain(refusal ?? ""));
            RunMarker.End(RunMarkerPath);
            FlushLogs();
            return 3;
        }
        // The Messenger helper reads its key from the file named here (protection_linux.go); each helper inherits this process's environment.
        Environment.SetEnvironmentVariable("MESSAGES_HELPER_KEY_FILE", keys.HelperKey);
        // And keeps everything it writes in the keys folder, never on the data volume (storage.go): e2ee.db's Signal keys are unsealed, and
        // messages.db's ids and times say who talks to whom.
        Environment.SetEnvironmentVariable(MessengerKeptAccount.PrivateRootVariable, keys.HelperFolder);
        HelperConnection.DataRoot = root;
        HelperConnection.Trace = AppLog.Write;
        // Messenger's and Instagram's. Instagram's helper reads neither variable above: it keeps nothing on disk (the host seals its session).
        HelperConnection.Allowed = UnavailableProviders.Runs;
        // Google Messages only when asked for and able to keep its sign-in (LinuxGoogle); then its helper may start too, and nothing else does.
        var google = LinuxGoogle.Prepare(options, keys, AppLog.Write);
        if (google is not null)
        {
            google.UseProfilesFolder();
            LinuxGoogle.SweepProfiles(AppLog.Write, google.VoiceKept(keys));
            HelperConnection.Allowed = name => UnavailableProviders.Runs(name) || name == LinuxGoogle.Helper;
            // Google Voice's helper too, but only beside the server's own Chrome, which signs Voice in and renews it (SWITCHBOARD.md 5.4).
            if (google.VoiceRuns) { var allowed = HelperConnection.Allowed; HelperConnection.Allowed = name => allowed(name) || name == LinuxGoogle.VoiceHelper; }
        }
        AppLog.Write($"server started version={typeof(Program).Assembly.GetName().Version?.ToString(3)} host=linux lease=ok previous_exit={previousExit} restore={options.Restore} umask={mask}");
        // Whether each helper this host runs is beside it, said once at start: a missing one otherwise shows only when that provider is first connected.
        // Google Messages' is named only when Google runs here (Prepare has already refused it, with no_helper, when it is missing).
        var helpers = google is null ? new[] { UnavailableProviders.MessengerHelper, UnavailableProviders.InstagramHelper }
            : new[] { UnavailableProviders.MessengerHelper, UnavailableProviders.InstagramHelper, LinuxGoogle.Helper }.Concat(google.VoiceRuns ? [LinuxGoogle.VoiceHelper] : []).ToArray();
        AppLog.Write("server helpers " + string.Join(' ', helpers
            .Select(name => $"{name}={(File.Exists(HelperExecutables.PathFor(name)) ? "present" : "missing")}")));
        AppLog.Write($"server compose step=prelude ms={prelude.ElapsedMilliseconds}");

        var run = new LinuxServerRun(root, keys, options, AppLog.Write, previousExit, google);
        // Taken from the runtime's default (which ends the process at once), so the stop below runs: a send on the wire gets its bound, and every helper
        // is closed before the process ends.
        using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, Stop(run, "sigterm"));
        using var interrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT, Stop(run, "sigint"));
        using var hangUp = PosixSignalRegistration.Create(PosixSignal.SIGHUP, Stop(run, "sighup"));
        var code = run.RunAsync(LinuxServerRun.Network(options, keys, AppLog.Write, name => name == PagePasswordVariable ? pagePassword : Environment.GetEnvironmentVariable(name))).GetAwaiter().GetResult();
        RunMarker.End(RunMarkerPath);
        FlushLogs();
        GC.KeepAlive(lease);
        return code;
    }

    private static Action<PosixSignalContext> Stop(LinuxServerRun run, string reason) => context =>
    {
        context.Cancel = true;
        run.RequestStop(reason);
    };

    private static string RunMarkerPath => AppPaths.Combine("server-run.marker");

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint SetUmask(uint mask);

    private static string PrivateFiles()
    {
        if (!OperatingSystem.IsLinux()) return "none";
        try
        {
            SetUmask(0b000_111_111);
            return "private";
        }
        catch (Exception failure) when (failure is DllNotFoundException or EntryPointNotFoundException) { return "unavailable"; }
    }

    // What whoever installed the server reads when it will not start, beside the log line's fixed word: the only output of a container that stops at once.
    internal static string Explain(string reason) => reason.Split(' ')[0] switch
    {
        "data_missing" => "The data folder doesn't exist. Create it, or set MESSAGES_DATA to the folder the server should use.",
        "data_not_writable" => "The server can't write to its data folder. Check that the folder belongs to the user the server runs as.",
        "host_locked" => "Another Messages Server is already running on this data folder.",
        "keys_folder_missing" => "The keys folder doesn't exist. Create it (with Docker, it's the keys volume).",
        "keys_in_data" => "The keys folder and the data folder overlap. Keep them apart.",
        "key_missing" => "The data folder has data but the keys folder has no key for it: the keys were lost, or belong with other data.",
        "key_mismatch" => "The keys folder belongs to a different server's data.",
        "key_invalid" => "The key in the keys folder can't be read.",
        _ => "The server couldn't start.",
    };

    // Standard output always; appended to the running server's log (never rotated) when there is a folder to write it in.
    private static int Refused(string reason, bool toFile)
    {
        var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} server start refused reason={reason}\n";
        Console.Out.Write(line);
        Console.Error.WriteLine("Messages Server didn't start: " + Explain(reason));
        if (toFile && Diagnostics.Recording)
        {
            try { File.AppendAllText(AppPaths.Combine("server.log"), line); }
            catch (Exception) { }
        }
        return 3;
    }

    // Appended to the server's log, never rotated, as a refused start is: the line is fixed words and a count.
    private static int ResetSpace(string root)
    {
        var outcome = SpaceReset.Run(root, HostLease.TryAcquire, !Console.IsInputRedirected, Console.In, Console.Out);
        if (Diagnostics.Recording)
        {
            try { File.AppendAllText(AppPaths.Combine("server.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} {SpaceReset.LogLine(outcome)}\n"); }
            catch (Exception) { }
        }
        return outcome.ExitCode;
    }

    private static int ClearHeld(string root, string keysRoot)
    {
        var outcome = HeldClear.Run(root, keysRoot, HostLease.TryAcquire, !Console.IsInputRedirected, Console.In, Console.Out);
        if (Diagnostics.Recording)
        {
            try { File.AppendAllText(AppPaths.Combine("server.log"), $"{DateTimeOffset.Now:HH:mm:ss.fff} {HeldClear.LogLine(outcome)}\n"); }
            catch (Exception) { }
        }
        return outcome.ExitCode;
    }

    internal static void FlushLogs()
    {
        AppLog.Flush();
        GoogleMessagesLog.Flush();
        MessengerLog.Flush();
        GoogleVoiceLog.Flush();
        InstagramLog.Flush();
    }
}
