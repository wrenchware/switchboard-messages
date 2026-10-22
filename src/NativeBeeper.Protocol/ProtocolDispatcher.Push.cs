namespace NativeBeeper.Protocol;

public sealed partial class ProtocolDispatcher
{
    private static Dictionary<string, Route> AddPushRoutes(Dictionary<string, Route> routes)
    {
        routes[ProtocolMethods.PushOffer] = Of<IPushService, PushOffer>(s => s.Push, (port, ct) => port.OfferAsync(ct));
        routes[ProtocolMethods.PushRegister] = Of<IPushService, PushRegisterParams, Done>(s => s.Push, async (port, registration, ct) =>
        {
            await port.RegisterAsync(registration, ct).ConfigureAwait(false);
            return new Done();
        });
        return routes;
    }
}
