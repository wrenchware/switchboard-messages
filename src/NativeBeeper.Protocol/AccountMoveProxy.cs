namespace NativeBeeper.Protocol;

/// <summary>
/// A server's account move, over the wire (<c>accounts.move.*</c>, protocol 16). The writers are journaled mutations under the caller's own operation id and never retried
/// (<see cref="ProtocolClient.MutateAsync{TParams, TResult}"/>): one whose connection ended before its answer is an unknown-outcome <see cref="SendException"/> carrying that id,
/// which the caller settles by asking <see cref="StatusAsync"/> what the server holds, never by guessing. The reads are plain calls.
/// </summary>
public sealed class AccountMoveProxy(ProtocolClient client) : IAccountMove
{
    public Task<AccountMoveStatus> StatusAsync(AccountMoveStatusParams request, CancellationToken cancellationToken = default) =>
        client.CallAsync<AccountMoveStatusParams, AccountMoveStatus>(ProtocolMethods.AccountMoveStatus, request, cancellationToken);

    public Task<MovePiece> StageAsync(AccountMoveStageParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveStageParams, MovePiece>(ProtocolMethods.AccountMoveStage, request, cancellationToken);

    public Task<MoveHistoryPutResult> PutHistoryAsync(MoveHistoryPutParams request, CancellationToken cancellationToken = default) =>
        client.CallAsync<MoveHistoryPutParams, MoveHistoryPutResult>(ProtocolMethods.AccountMoveHistoryPut, request, cancellationToken);

    public Task<AccountMoveVerified> VerifyAsync(AccountMoveVerifyParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveVerifyParams, AccountMoveVerified>(ProtocolMethods.AccountMoveVerify, request, cancellationToken);

    public Task<AccountMoveActivated> ActivateAsync(AccountMoveActivateParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveActivateParams, AccountMoveActivated>(ProtocolMethods.AccountMoveActivate, request, cancellationToken);

    public Task<AccountMoveStatus> AbortAsync(AccountMoveAbortParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveAbortParams, AccountMoveStatus>(ProtocolMethods.AccountMoveAbort, request, cancellationToken);

    public Task<AccountMoveStatus> HoldAsync(AccountMoveHoldParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveHoldParams, AccountMoveStatus>(ProtocolMethods.AccountMoveHold, request, cancellationToken);

    public Task<AccountMoveChunk> ReadAsync(AccountMoveReadParams request, CancellationToken cancellationToken = default) =>
        client.CallAsync<AccountMoveReadParams, AccountMoveChunk>(ProtocolMethods.AccountMoveRead, request, cancellationToken);

    public Task<AccountMoveHistoryPage> GetHistoryAsync(AccountMoveHistoryGetParams request, CancellationToken cancellationToken = default) =>
        client.CallAsync<AccountMoveHistoryGetParams, AccountMoveHistoryPage>(ProtocolMethods.AccountMoveHistoryGet, request, cancellationToken);

    public Task<AccountMoveStatus> ReleaseAsync(AccountMoveReleaseParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveReleaseParams, AccountMoveStatus>(ProtocolMethods.AccountMoveRelease, request, cancellationToken);

    public Task<AccountMoveStatus> ResumeAsync(AccountMoveResumeParams request, CancellationToken cancellationToken = default) =>
        client.MutateAsync<AccountMoveResumeParams, AccountMoveStatus>(ProtocolMethods.AccountMoveResume, request, cancellationToken);
}
