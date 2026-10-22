using System.Text.Json;
using NativeBeeper.Presentation.Services;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

// Ordinary sends use the same shared warning and dismissal as scheduled sends, without keeping their words.
internal sealed class HostUnsettledSends(SharedStateStore store, Func<CancellationToken, Task<string?>> localKey, TimeProvider? time = null)
{
    internal static string RetirementId(string operationId) => "outbox:" + operationId;

    /// <summary>A send's warning as its row holds it, in System.Text.Json's default spelling, which every client reads it in (ScheduledHostLink): the support window holds the bytes.</summary>
    internal static byte[] RowValue(SharedUnsettledSend send) => JsonSerializer.SerializeToUtf8Bytes(send);

    public async Task RecordAsync(UnsettledSendOperation operation)
    {
        var profile = NotificationIdentity.ProfileOf(await localKey(CancellationToken.None).ConfigureAwait(false))
            ?? throw new IOException("There is no key to file the send under.");
        var send = new SharedUnsettledSend(operation.OperationId, operation.ChatId, null,
            operation.OutcomeUnknown ? UnsettledStates.Unknown : UnsettledStates.Failed, operation.Started,
            RecordedAt: (time ?? TimeProvider.System).GetUtcNow());
        try
        {
            await store.WriteOwnBatchAsync([(SharedCategories.KeyOf(profile, SharedCategories.Outbox, operation.OperationId), RowValue(send))],
                pin: true, makes: [RetirementId(operation.OperationId)]).ConfigureAwait(false);
        }
        // A crash between publishing and flushing the journal can replay publication. Dismissal retired the id in the same commit as its row,
        // so that replay cannot resurrect a warning the user already dismissed.
        catch (RetiredIdException) { }
    }
}
