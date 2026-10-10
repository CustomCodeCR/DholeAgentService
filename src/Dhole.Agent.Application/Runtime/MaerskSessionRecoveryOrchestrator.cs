using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Runtime;

/// <summary>
/// Centralizes safe recovery policy. It deliberately never treats CAPTCHA,
/// edge blocks, or rate limits as technical corruption. The existing login
/// flow first attempts to reuse the persistent authenticated session.
/// </summary>
public sealed class MaerskSessionRecoveryOrchestrator(
    IBrowserProfileExclusiveLock exclusiveLock,
    IBrowserProfileManager profileManager) : IMaerskSessionRecoveryOrchestrator
{
    public Task<IAsyncDisposable?> TryEnterAsync(
        string providerCode, Guid credentialId, CancellationToken cancellationToken)
        => exclusiveLock.TryAcquireAsync(providerCode, credentialId, cancellationToken);

    public MaerskSessionRecoveryDecision Evaluate(
        BrowserProfileStatus? status, AgentExecutionType executionType)
        => status switch
        {
            BrowserProfileStatus.Blocked => new(
                MaerskSessionRecoveryAction.StopForProviderVerification,
                "maersk_browser_profile_blocked",
                "Provider verification is required. Preserve the original profile."),

            BrowserProfileStatus.ResetRequested when executionType != AgentExecutionType.Manual => new(
                MaerskSessionRecoveryAction.AwaitManualRepair,
                "maersk_browser_profile_repair_requires_manual_run",
                "An explicitly approved technical repair requires an operator-initiated execution."),

            BrowserProfileStatus.ResetRequested => new(
                MaerskSessionRecoveryAction.ApprovedTechnicalRepair),

            // Expired, LoginRequired and Error are not evidence of local
            // Chromium corruption. Reuse the existing profile and authenticate
            // normally; only an approved ResetRequested can archive it.
            _ => new(MaerskSessionRecoveryAction.ReusePersistentSession)
        };

    public void RepairApprovedTechnicalProfile(
        string providerCode, Guid credentialId,
        BrowserProfileStatus status, AgentExecutionType executionType)
    {
        if (Evaluate(status, executionType).Action
            != MaerskSessionRecoveryAction.ApprovedTechnicalRepair)
            throw new InvalidOperationException(
                "Profile repair requires an explicit technical repair request and a manual execution.");

        // Must be invoked under the exclusive profile lock, before any new
        // Chromium context opens. Backup remains available for rollback.
        profileManager.ResetStoragePath(providerCode, credentialId);
    }
}
