using System.Text.Json;

namespace NativeBeeper.Protocol;

// accounts.google.remoteSignIn and accounts.google.remoteSignInInput (ProtocolMethods.RemoteSignIn.cs): a Google sign-in made in the host's own browser.
//
// The start is a journaled mutation like the sign-in it replaces, so a repeated operation id answers the first answer and a connection that dropped mid-way leaves an
// unknown outcome in the journal, never a second run. It is bound to this connection: the call's own cancel, the connection closing (every running request is cancelled)
// and the host's limits all end it. What the browser shows goes to this connection only, as `signInView` events written straight to its channel, never through the
// event log, so a client that resumes is given none and no other connection sees any. Neither a picture nor an input event is logged here, and the only thing the
// journal keeps of the start is its operation id's hash and its answer.
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
            var port = services.RemoteSignIn ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.RemoteSignIn) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await using var progress = new ConnectionProgress(connection, services, parameters.ClientOpId);
                await using var view = new ConnectionView(connection, services, parameters.ClientOpId);
                await Coded(async () =>
                {
                    await port.SignInAsync(new RemoteSignInRequest(connection, parameters.ClientOpId, parameters.AsNewDevice, parameters.Width, parameters.Height), view, progress,
                        cancellationToken).ConfigureAwait(false);
                    // As accounts.google.signIn does once a sign-in has been accepted: the host's keeper is told, and a failure to tell is never the sign-in's.
                    try { services.GoogleSignedIn?.Invoke(); }
                    catch (Exception) { }
                    return 0;
                }).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
            });
        }, Settles: true));

        // Protocol 15: the same for a Google Voice account. Nothing of Messages' keeper is told afterwards: it renews Messages, and the host adds the account itself.
        routes[ProtocolMethods.VoiceRemoteSignInMethod] = OwnerOnly(ProtocolMethods.VoiceRemoteSignInMethod, new(typeof(VoiceRemoteSignInParams), typeof(Done), null, (connection, services, request) =>
        {
            var parameters = ReadParams<VoiceRemoteSignInParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(VoiceRemoteSignInParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var port = services.VoiceRemoteSignIn ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.VoiceRemoteSignIn) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await using var progress = new ConnectionProgress(connection, services, parameters.ClientOpId);
                await using var view = new ConnectionView(connection, services, parameters.ClientOpId);
                await Coded(async () =>
                {
                    await port.SignInAsync(new RemoteSignInRequest(connection, parameters.ClientOpId, false, parameters.Width, parameters.Height), view, progress, cancellationToken).ConfigureAwait(false);
                    return 0;
                }).ConfigureAwait(false);
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
            var service = parameters.Service;
            var signInRequest = new RemoteSignInRequest(connection, parameters.ClientOpId, service == RemoteSignInServices.Google && parameters.AsNewDevice, parameters.Width, parameters.Height);
            if (!RemoteSignInServices.EndsWithDone(service))
            {
                var port = (service == RemoteSignInServices.Google ? services.RemoteSignIn : services.VoiceRemoteSignIn) ?? throw new NotSupportedException();
                return new Prepared(parameters.ClientOpId, async cancellationToken =>
                {
                    await using var progress = new ConnectionProgress(connection, services, parameters.ClientOpId);
                    await using var view = new ConnectionView(connection, services, parameters.ClientOpId);
                    await Coded(async () =>
                    {
                        await port.SignInAsync(signInRequest, view, progress, cancellationToken).ConfigureAwait(false);
                        // As accounts.google.remoteSignIn does: Messages' keeper is told; Voice's add is the host's own.
                        if (service == RemoteSignInServices.Google)
                        {
                            try { services.GoogleSignedIn?.Invoke(); }
                            catch (Exception) { }
                        }
                        return 0;
                    }).ConfigureAwait(false);
                    return JsonSerializer.SerializeToUtf8Bytes(new RemoteSignInResult(), ProtocolJson.Options);
                });
            }
            var cookieHost = services.MetaRemoteSignIn is { } offered && offered.Offers(service) ? offered : throw new NotSupportedException();
            // Checked before the browser runs, so a host that cannot connect the account never shows a sign-in for it.
            if (service == RemoteSignInServices.Messenger ? services.MessengerAccount is null : services.InstagramAccount is null) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await using var progress = new ConnectionProgress(connection, services, parameters.ClientOpId);
                await using var view = new ConnectionView(connection, services, parameters.ClientOpId);
                // This connection's input goes to the port its sign-in runs on, whichever object the host gave for it.
                Volatile.Write(ref connection.signInInput, cookieHost.InputAsync);
                RemoteSignInResult result;
                try
                {
                    result = await Coded(async () =>
                    {
                        var cookies = await cookieHost.SignInAsync(service, signInRequest, view, cancellationToken).ConfigureAwait(false);
                        return await RemoteSignInFinish.ConnectAsync(services, service, cookies, progress, connection.Log, cancellationToken).ConfigureAwait(false);
                    }).ConfigureAwait(false);
                }
                finally { Volatile.Write(ref connection.signInInput, null); }
                return JsonSerializer.SerializeToUtf8Bytes(result, ProtocolJson.Options);
            });
        }, Settles: true));

        // Not the owner's alone: the host answers input only from the connection that is signing in, which a refused start never is.
        routes[ProtocolMethods.RemoteSignInInputMethod] = new(typeof(RemoteSignInInputParams), typeof(Done), null,
            Own: (connection, request, _, cancellationToken) => connection.RemoteSignInInputAsync(request, cancellationToken));
        return routes;
    }

    // Not a mutation, so never journaled: it is the person's keystrokes. It belongs to this connection (the host answers input from the connection that is signing in only).
    private async Task<byte[]> RemoteSignInInputAsync(RequestFrame request, CancellationToken cancellationToken)
    {
        var parameters = ReadParams<RemoteSignInInputParams>(request);
        // The port this connection's Messenger or Instagram sign-in runs on; otherwise one window serves every service (the host's ports share the one browser), so any of them answers
        // for the connection that is signing in.
        var input = Volatile.Read(ref signInInput)
            ?? (services.RemoteSignIn is { } google ? google.InputAsync
            : services.VoiceRemoteSignIn is { } voice ? voice.InputAsync
            : services.MetaRemoteSignIn is { } meta ? meta.InputAsync
            : throw new NotSupportedException());
        if (negotiated < ProtocolVersion.RemoteSignIn) throw new NotSupportedException();
        if (parameters.Events.Count > RemoteSignIn.MaxEvents || parameters.Events.Any(item => item is null))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        // A connection below 27 never says it is done: the kind is not its to send.
        if (negotiated < ProtocolVersion.ServerSignIn && parameters.Events.Any(item => item.Kind == RemoteSignIn.DoneKind))
            throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
        await Coded(async () =>
        {
            await input(this, parameters.Events, cancellationToken).ConfigureAwait(false);
            return 0;
        }).ConfigureAwait(false);
        return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
    }

    // The connection's own log, for the fixed lines of a sign-in's connect (RemoteSignInFinish).
    private Action<string>? Log => log;

    // Where this connection's input goes while one of its Messenger or Instagram sign-ins runs (protocol 27); null otherwise.
    private Func<object, IReadOnlyList<RemoteSignInInput>, CancellationToken, Task>? signInInput;

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
