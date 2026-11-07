using NativeBeeper.App.Services;
using NativeBeeper.Core.Backend;
using NativeBeeper.Protocol;

namespace NativeBeeper.Host;

// The legacy device-move package has no slot registry. Refuse a partial move instead of leaving saved devices running on both hosts.
internal sealed class MultipleAccountMove(IAccountMove original, ProviderAccounts accounts) : IAccountMove
{
    private IAccountMove Checked()
    {
        if (accounts.RequiresAccountProtocol) throw new BackendException(BackendFailure.Unsupported, "Moving accounts is unavailable after additional accounts have been added to this server.");
        return original;
    }
    public Task<AccountMoveStatus> StatusAsync(AccountMoveStatusParams request, CancellationToken cancellationToken = default) => Checked().StatusAsync(request, cancellationToken);
    public Task<MovePiece> StageAsync(AccountMoveStageParams request, CancellationToken cancellationToken = default) => Checked().StageAsync(request, cancellationToken);
    public Task<MoveHistoryPutResult> PutHistoryAsync(MoveHistoryPutParams request, CancellationToken cancellationToken = default) => Checked().PutHistoryAsync(request, cancellationToken);
    public Task<AccountMoveVerified> VerifyAsync(AccountMoveVerifyParams request, CancellationToken cancellationToken = default) => Checked().VerifyAsync(request, cancellationToken);
    public Task<AccountMoveActivated> ActivateAsync(AccountMoveActivateParams request, CancellationToken cancellationToken = default) => Checked().ActivateAsync(request, cancellationToken);
    public Task<AccountMoveStatus> AbortAsync(AccountMoveAbortParams request, CancellationToken cancellationToken = default) => original.AbortAsync(request, cancellationToken);
    public Task<AccountMoveStatus> HoldAsync(AccountMoveHoldParams request, CancellationToken cancellationToken = default) => Checked().HoldAsync(request, cancellationToken);
    public Task<AccountMoveChunk> ReadAsync(AccountMoveReadParams request, CancellationToken cancellationToken = default) => Checked().ReadAsync(request, cancellationToken);
    public Task<AccountMoveHistoryPage> GetHistoryAsync(AccountMoveHistoryGetParams request, CancellationToken cancellationToken = default) => Checked().GetHistoryAsync(request, cancellationToken);
    public Task<AccountMoveStatus> ReleaseAsync(AccountMoveReleaseParams request, CancellationToken cancellationToken = default) => Checked().ReleaseAsync(request, cancellationToken);
    public Task<AccountMoveStatus> ResumeAsync(AccountMoveResumeParams request, CancellationToken cancellationToken = default) => original.ResumeAsync(request, cancellationToken);
}
