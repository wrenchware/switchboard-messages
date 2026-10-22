using System.Text.Json;
using NativeBeeper.Core.Services;

namespace NativeBeeper.Protocol;

// accounts.google.qrPairing and accounts.google.qrPairingCancel (ProtocolMethods.QrPairing.cs): Google Messages paired by QR code.
//
// The start is a journaled mutation like the sign-in it stands beside, so a repeated operation id answers the first answer and a connection that dropped mid-way leaves an
// unknown outcome in the journal, never a second run. It is bound to this connection: the call's own cancel, qrPairingCancel, the connection closing (every running request is
// cancelled) and the host's own limit all end it. Each picture of the code goes to this connection only, as a `qrCode` event written straight to its channel, never through the
// event log, so a client that resumes is given none and no other connection sees any. The code is a credential: no picture is logged here, and the only thing the journal keeps of
// the start is its operation id's hash and its answer.
public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddQrPairingRoutes(Dictionary<string, Route> routes)
    {
        // Only the owner device pairs (OwnerOnly): a pairing replaces the server's Google Messages session.
        routes[ProtocolMethods.QrPairingMethod] = OwnerOnly(ProtocolMethods.QrPairingMethod, new(typeof(QrPairingParams), typeof(Done), null, (connection, services, request) =>
        {
            var parameters = ReadParams<QrPairingParams>(request);
            if (parameters.ClientOpId is not { Length: > 0 and <= 128 } || !ParamsShape.HasRequiredValues(typeof(QrPairingParams), request.Params))
                throw new ProtocolException(new ProtocolError(ProtocolErrorCodes.InvalidRequest, "The request's params are missing or not valid."));
            var port = services.QrPairing ?? throw new NotSupportedException();
            if (connection.negotiated < ProtocolVersion.QrPairing) throw new NotSupportedException();
            return new Prepared(parameters.ClientOpId, async cancellationToken =>
            {
                await using var view = new ConnectionQr(connection, services, parameters.ClientOpId);
                await Coded(async () =>
                {
                    await port.PairAsync(new QrPairingRequest(connection, parameters.ClientOpId), view, cancellationToken).ConfigureAwait(false);
                    // As accounts.google.signIn does once a sign-in has been accepted: the host's keeper is told, and a failure to tell is never the pairing's.
                    try { services.GoogleSignedIn?.Invoke(); }
                    catch (Exception) { }
                    return 0;
                }).ConfigureAwait(false);
                return JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options);
            });
        }, Settles: true));

        // Not the owner's alone: it ends this connection's own pairing and no other.
        routes[ProtocolMethods.QrPairingCancelMethod] = new(null, typeof(Done), null, Own: (connection, _, _, _) => connection.QrCancelAsync());
        return routes;
    }

    private Task<byte[]> QrCancelAsync()
    {
        var port = services.QrPairing ?? throw new NotSupportedException();
        if (negotiated < ProtocolVersion.QrPairing) throw new NotSupportedException();
        port.Cancel(this);
        return Task.FromResult(JsonSerializer.SerializeToUtf8Bytes(new Done(), ProtocolJson.Options));
    }

    /// <summary>
    /// Where a running pairing puts the pictures of its code: the newest only (one the connection is too slow for is replaced, never queued), written by one pump in order, finished
    /// before the call is, so nothing follows the answer. Nothing it carries is logged or stored.
    /// </summary>
    private sealed class ConnectionQr(ProtocolDispatcher connection, ProtocolServices services, string clientOpId) : IGoogleQrOutput, IAsyncDisposable
    {
        private readonly object gate = new();
        private readonly SemaphoreSlim wake = new(0);
        private QrCodeEvent? latest;
        private bool closed;
        private Task? pump;

        public void Code(ReadOnlyMemory<byte> png, int number)
        {
            // Over the limit, or not a picture at all: not sent. Nothing is said, since what it holds is the code.
            if (png.IsEmpty || number < 1) return;
            var data = Convert.ToBase64String(png.Span);
            if (data.Length > QrPairing.MaxPictureCharacters) return;
            lock (gate)
            {
                if (closed) return;
                latest = new QrCodeEvent(clientOpId, number, data);
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
                QrCodeEvent? picture;
                lock (gate)
                {
                    picture = latest;
                    latest = null;
                    if (picture is null && closed) return;
                }
                if (picture is not null)
                    await connection.SendTextAsync(Frames.Serialize(new EventFrame(0, epoch, EventKinds.QrCode, ProtocolJson.ToElement(picture)))).ConfigureAwait(false);
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
