using System.Net.Http;

namespace Dhole.Agent.Application.Runtime;

/// <summary>
/// Categorizes structured Maersk failures. This is a policy description only:
/// retries, session repair and circuit-breaker transitions belong to later phases.
/// </summary>
public static class MaerskFailureClassifier
{
    public static MaerskFailureClassification Classify(
        string? errorCode,
        int? httpStatusCode = null,
        Exception? exception = null)
    {
        var code = string.IsNullOrWhiteSpace(errorCode)
            ? null
            : errorCode.Trim().ToLowerInvariant();

        // Challenges and restrictions must never trigger an automatic profile reset,
        // a new browser identity or a switch to another environment.
        if (code is "maersk_hcaptcha_required"
            or "maersk_authentication_verification_required"
            or "maersk_browser_profile_blocked"
            or "maersk_browser_profile_repair_requires_manual_run")
            return Create(FailureCategory.ProviderVerificationRequired, code, requiresOperator: true, openCircuit: true);

        if (code is "maersk_authentication_edge_denied"
            or "maersk_authentication_forbidden")
            return Create(FailureCategory.ProviderAccessRestricted, code, requiresOperator: true, openCircuit: true);

        if (code is "maersk_authentication_rate_limited")
            return Create(FailureCategory.ProviderRateLimited, code, requiresOperator: true, openCircuit: true);

        // Authentication already failed in the provider. Phase 1 does not
        // automatically resubmit credentials or retry an interactive login.
        if (code is "maersk_authentication_unauthorized"
            or "maersk_authentication_expired")
            return Create(FailureCategory.AuthenticationExpired, code, requiresOperator: true);

        // Keep the existing WaitingForAuthentication behavior for failed login
        // interactions until an operator can determine whether a challenge occurred.
        if (code is "maersk_authentication_continue_not_clickable"
            or "maersk_authentication_callback_timeout"
            or "maersk_post_auth_navigation_failed")
            return Create(FailureCategory.ProviderOrUiTimeout, code, requiresOperator: true);

        if (code is "maersk_browser_profile_corrupt")
            return Create(FailureCategory.LocalProfileCorrupt, code, requiresOperator: true);

        if (code is "browser_profile_repair_failed"
            or "browser_session_invalid"
            or "maersk_worker_connection_failed"
            or "maersk_chromium_crashed")
            return Create(FailureCategory.InfrastructureTransient, code, canRetry: code != "browser_profile_repair_failed");

        if (code is "maersk_offer_timeout"
            or "maersk_authentication_service_error")
            return Create(FailureCategory.ProviderOrUiTimeout, code);

        if (code is "invalid_input"
            or "invalid_profile_snapshot"
            or "invalid_profile_route"
            or "invalid_profile_equipment"
            or "missing_credential")
            return Create(FailureCategory.ValidationPermanent, code);

        if (code is "maersk_all_searches_failed")
            return Create(FailureCategory.AggregateFailure, code);

        // HTTP evidence comes from Maersk's own response, not a substring in a
        // browser exception or user-controlled message.
        return httpStatusCode switch
        {
            401 => Create(FailureCategory.AuthenticationExpired, "maersk_authentication_unauthorized", requiresOperator: true),
            403 => Create(FailureCategory.ProviderAccessRestricted, "maersk_authentication_forbidden", requiresOperator: true, openCircuit: true),
            429 => Create(FailureCategory.ProviderRateLimited, "maersk_authentication_rate_limited", requiresOperator: true, openCircuit: true),
            >= 500 and <= 599 => Create(FailureCategory.ProviderOrUiTimeout, "maersk_provider_service_unavailable"),
            _ => ClassifyExceptionOrUnknown(code, exception)
        };
    }

    private static MaerskFailureClassification ClassifyExceptionOrUnknown(string? code, Exception? exception)
    {
        if (exception is TimeoutException)
            return Create(FailureCategory.ProviderOrUiTimeout, code ?? "maersk_offer_timeout");

        if (exception is IOException or HttpRequestException)
            return Create(FailureCategory.InfrastructureTransient, code ?? "maersk_infrastructure_transient", canRetry: true);

        // Unknown, cancelled, and aggregate errors do not justify an automatic
        // retry, profile rebuild or identity rotation.
        return Create(FailureCategory.UnknownPermanent, code ?? "maersk_unknown_failure");
    }

    private static MaerskFailureClassification Create(
        FailureCategory category,
        string canonicalErrorCode,
        bool canRetry = false,
        bool requiresOperator = false,
        bool openCircuit = false)
        => new(category, canonicalErrorCode, canRetry, requiresOperator, openCircuit, PreserveProfile: true);
}

public enum FailureCategory
{
    ProviderVerificationRequired,
    ProviderAccessRestricted,
    ProviderRateLimited,
    AuthenticationExpired,
    LocalProfileCorrupt,
    InfrastructureTransient,
    ProviderOrUiTimeout,
    ValidationPermanent,
    AggregateFailure,
    UnknownPermanent
}

public sealed record MaerskFailureClassification(
    FailureCategory Category,
    string CanonicalErrorCode,
    bool CanRetry,
    bool RequiresOperator,
    bool ShouldOpenCircuit,
    bool PreserveProfile);
