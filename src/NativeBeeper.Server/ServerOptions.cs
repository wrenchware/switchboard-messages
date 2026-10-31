namespace NativeBeeper.Server;

/// <summary>
/// What MessagesServer.exe was started with. <c>--restore</c> is what lets the providers connect: without it the server holds its lease and its
/// window and serves nothing from any account, so it can be run on any data root, real or scratch, with no network effect (SWITCHBOARD.md 3.1).
/// </summary>
/// <param name="Quit">Ask the server of this data root to quit, and wait for it.</param>
/// <param name="Background">Started with Windows (3.3); accepted and ignored until then.</param>
/// <param name="Restore">Reconnect the saved accounts the way a launch of the app does.</param>
/// <param name="OwnerPid">The app that started this server and decides when it stops (3.4); recorded only.</param>
internal sealed record ServerOptions(bool Quit, bool Background, bool Restore, int? OwnerPid)
{
    public static ServerOptions Parse(IReadOnlyList<string> args)
    {
        bool quit = false, background = false, restore = false;
        int? owner = null;
        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--quit": quit = true; break;
                case "--background": background = true; break;
                case "--restore": restore = true; break;
                case "--owner" when index + 1 < args.Count && int.TryParse(args[index + 1], out var pid) && pid > 0: owner = pid; index++; break;
            }
        }
        return new(quit, background, restore, owner);
    }
}
