using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Abstractions.Runtime;

/// <summary>
/// A mutually exclusive claim on the physical persistent Chromium profile.
/// Must be held until the browser context has been closed.
/// </summary>
public interface IBrowserProfileExclusiveLock
{
    Task<IAsyncDisposable?> TryAcquireAsync(
        string providerCode,
        Guid credentialId,
        CancellationToken cancellationToken = default);
}

public enum MaerskSessionRecoveryAction
{
    ReusePersistentSession,
    ApprovedTechnicalRepair,
    StopForProviderVerification,
    AwaitManualRepair
}

public sealed record MaerskSessionRecoveryDecision(
    MaerskSessionRecoveryAction Action,
    string? ErrorCode = null,
    string? Explanation = null)
{
    public bool CanContinue => Action is
        MaerskSessionRecoveryAction.ReusePersistentSession or
        MaerskSessionRecoveryAction.ApprovedTechnicalRepair;
}

public interface IMaerskSessionRecoveryOrchestrator
{
    Task<IAsyncDisposable?> TryEnterAsync(
        string providerCode, Guid credentialId, CancellationToken cancellationToken);

    MaerskSessionRecoveryDecision Evaluate(
        BrowserProfileStatus? status, AgentExecutionType executionType);

    void RepairApprovedTechnicalProfile(
        string providerCode, Guid credentialId,
        BrowserProfileStatus status, AgentExecutionType executionType);
}
