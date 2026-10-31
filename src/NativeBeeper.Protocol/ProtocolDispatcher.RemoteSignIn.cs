using System.Text.Json;

namespace NativeBeeper.Protocol;

// accounts.google.remoteSignIn and accounts.google.remoteSignInInput (ProtocolMethods.RemoteSignIn.cs): a Google sign-in made in the host's own browser.
//
// The start is a journaled mutation like the sign-in it replaces, so a repeated operation id answers the first answer and a connection that dropped mid-way leaves an
// unknown outcome in the journal, never a second run. It is bound to this connection: the call's own cancel, the connection closing (every running request is cancelled)
// and the host's limits all end it. What the browser shows goes to this connection only, as `signInView` events written straight to its channel, never through the
// event log, so a client that resumes is given none and no other connection sees any. Neither a picture nor an input event is logged here, and the only thing the
// journal keeps of the start is its operation id's hash and its answer.
//
// Every method runs its sign-in through the host's one RemoteSignInSessions (services.SignIns) with this connection as the driver. The server's page drives the same sessions from
// its own socket (SWITCHBOARD.md 10.6), so one sign-in is open at a time across both, and input reaches a sign-in only from whoever started it.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddRemoteSignInRoutes(Dictionary<string, Route> routes)
    {
        // Only the owner device signs in (OwnerOnly): the browser it opens on the server is a copy of the owner's kept Google profile.
        routes[ProtocolMethods.RemoteSignInMethod] = OwnerOnly(ProtocolMethods.RemoteSignInMethod, new(typeof(RemoteSignInParams), typeof(Done), null, (connection, services, request) =>
        {
            var parameters = ReadParams<RemoteSignInParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(RemoteSignInParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            _ = services.RemoteSignIn ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.RemoteSignIn) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                // The keeper is told afterwards, as accounts.google.signIn does (RemoteSignInSessions).
                await connection.SignInOnHostAsync(services, RemoteSignInServices.Google, parameters.ClientOpId, parameters.AsNewDevice, parameters.Width, parameters.Height, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
            });
        }, Settles: true));

        // Protocol 15: the same for a Google Voice account. Nothing of Messages' keeper is told afterwards: it renews Messages, and the host adds the account itself.
        routes[ProtocolMethods.VoiceRemoteSignInMethod] = OwnerOnly(ProtocolMethods.VoiceRemoteSignInMethod, new(typeof(VoiceRemoteSignInParams), typeof(Done), null, (connection, services, request) =>
        {
            var parameters = ReadParams<VoiceRemoteSignInParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(VoiceRemoteSignInParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            _ = services.VoiceRemoteSignIn ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.VoiceRemoteSignIn) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await connection.SignInOnHostAsync(services, RemoteSignInServices.Voice, parameters.ClientOpId, false, parameters.Width, parameters.Height, cancellationToken).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
            });
        }, Settles: true));

        // Protocol 27: one method for any service. Google and Voice run exactly as their own methods do; Messenger and Instagram are the browser part on the host's cookie port, then the
        // connect their own method makes (RemoteSignInFinish), so a sign-in made here and one handed up from a PC's window connect the same way. The owner's alone, like every sign-in.
        routes[ProtocolMethods.ServerSignInMethod] = OwnerOnly(ProtocolMethods.ServerSignInMethod, new(typeof(ServerSignInParams), typeof(RemoteSignInResult), null, (connection, services, request) =>
        {
            var parameters = ReadParams<ServerSignInParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !RemoteSignInServices.Known(parameters.Service) || !ParamsShape.HasRequiredValues(typeof(ServerSignInParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            if (connection.negotiated < ProtocolVersion.ServerSignIn) throw new NotSupportedException();
            // Checked before the browser runs, so a host that cannot connect the account never shows a sign-in for it.
            if (!RemoteSignInSessions.Offers(services, parameters.Service)) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                // Messages' keeper is told after Google's, Voice's add is the host's own, and Messenger and Instagram connect as their own methods do (RemoteSignInSessions).
                var result = await connection.SignInOnHostAsync(services, parameters.Service, parameters.ClientOpId, parameters.AsNewDevice, parameters.Width, parameters.Height, cancellationToken)
                    .ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJson.Options);
            });
        }, Settles: true));

        // Not the owner's alone: the host answers input only from the connection that is signing in, which a refused start never is.
        routes[ProtocolMethods.RemoteSignInInputMethod] = new(typeof(RemoteSignInInputParams), typeof(Done), null,
            Own: (connection, request, _, cancellationToken) => connection.RemoteSignInInputAsync(request, cancellationToken));
        return routes;
    }

    // One sign-in with this connection as its driver: the pictures to this connection alone, Google's emoji and the connect's stages as its progress.
    private async Task<RemoteSignInResult> SignInOnHostAsync(ProtocolServices services, string service, string clientOpId, bool asNewDevice, int width, int height,
        CancellationToken cancellationToken)
    {
        await using var progress = new ConnectionProgress(this, services, clientOpId);
        await using var view = new ConnectionView(this, services, clientOpId);
        var driver = new RemoteSignInDriver(this, RemoteSignInFaces.App, view, progress, Log);
        return await Coded(() => services.SignIns.RunAsync(services, service, driver, clientOpId, asNewDevice, width, height, cancellationToken)).ConfigureAwait(false);
    }

    // Not a mutation, so never journaled: it is the person's keystrokes. It belongs to this connection (the host answers input from the connection that is signing in only).
    private async Task<byte[]> RemoteSignInInputAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<RemoteSignInInputParams>(request);
        if (services.RemoteSignIn is null && services.VoiceRemoteSignIn is null && services.MetaRemoteSignIn is null) throw new NotSupportedException();
        if (negotiated < ProtocolVersion.RemoteSignIn) throw new NotSupportedException();
        if (parameters.Events.Count > RemoteSignIn.MaxEvents || parameters.Events.Any(item => item is null))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        // A connection below 27 never says it is done: the kind is not its to send.
        if (negotiated < ProtocolVersion.ServerSignIn && parameters.Events.Any(item => item.Kind == RemoteSignIn.DoneKind))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        await Coded(async () =>
        {
            // To the port of the sign-in this connection holds, whichever service it is; holding none is no_sign_in.
            await services.SignIns.InputAsync(this, parameters.Events, cancellationToken).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
    }

    // The connection's own log, for the fixed lines of a sign-in's connect (RemoteSignInFinish).
    private Action<string>? Log => log;

    /// <summary>
    /// Where a running remote sign-in puts what the browser shows: the newest picture only (one the connection is too slow for is replaced, never queued), written by one pump in order,
    /// finished before the call is, so nothing follows the answer. Nothing it carries is logged or stored.
    /// </summary>
    private sealed class ConnectionView(ProtocolDispatcher connection, ProtocolServices services, string clientOpId) : IRemoteSignInOutput, IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim wake = new(0);
        private RemoteSignInFrame? latest;
        private bool signedIn, closed;
        private Task? pump;

        public void Frame(RemoteSignInFrame frame)
        {
            lock (gate)
            {
                if (closed || signedIn) return;
                latest = frame;
                pump ??= Task.Run(PumpAsync);
            }
            Wake();
        }

        public void SignedIn()
        {
            lock (gate)
            {
                if (closed) return;
                signedIn = true;
                latest = null;
                pump ??= Task.Run(PumpAsync);
            }
            Wake();
        }

        private void Wake()
        {
            try { wake.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        // A write that cannot be made (the connection is gone) has nobody to tell; the connection's own close ends the call.
        private async Task PumpAsync()
        {
            try { await WriteAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }

        private async Task WriteAsync()
        {
            var epoch = services.Events?.Epoch ?? "none";
            while (true)
            {
                RemoteSignInFrame? frame;
                bool done;
                lock (gate)
                {
                    frame = latest;
                    latest = null;
                    done = signedIn;
                    if (frame is null && !done && closed) return;
                }
                if (frame is not null)
                    await connection.SendTextAsync(Frames.Serialize(new EventFrame(0, epoch, EventKinds.SignInView,
                        ProtocolJson.ToElement(new SignInViewEvent(clientOpId, RemoteSignIn.FrameKind, frame.Jpeg, frame.ViewWidth, frame.ViewHeight))))).ConfigureAwait(false);
                else if (done)
                {
                    await connection.SendTextAsync(Frames.Serialize(new EventFrame(0, epoch, EventKinds.SignInView,
                        ProtocolJson.ToElement(new SignInViewEvent(clientOpId, RemoteSignIn.SignedInKind))))).ConfigureAwait(false);
                    return;
                }
                else await wake.WaitAsync().ConfigureAwait(false);
            }
        }

        public async ValueTask DisposeAsync()
        {
            Task? pending;
            lock (gate)
            {
                closed = true;
                latest = null;
                pending = pump;
            }
            // Wake a pump that is waiting, so it sees it is closed; one that is writing finishes that write and stops.
            Wake();
            if (pending is not null) await pending.ConfigureAwait(false);
            wake.Dispose();
        }
    }
}
