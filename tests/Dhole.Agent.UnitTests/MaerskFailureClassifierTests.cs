using System.Reflection;
using Dhole.Agent.Application.Runtime;
using Dhole.Agent.Domain.Agents;
using Dhole.Agent.Infrastructure.Providers.Maersk;

namespace Dhole.Agent.UnitTests;

[TestClass]
public sealed class MaerskFailureClassifierTests
{
    [DataTestMethod]
    [DataRow("maersk_hcaptcha_required")]
    [DataRow("maersk_authentication_verification_required")]
    [DataRow("maersk_browser_profile_blocked")]
    [DataRow("maersk_browser_profile_repair_requires_manual_run")]
    public void ProviderVerification_PreservesProfileAndRequiresOperator(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.ProviderVerificationRequired, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsTrue(result.ShouldOpenCircuit);
        Assert.IsTrue(result.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow("maersk_authentication_edge_denied")]
    [DataRow("maersk_authentication_forbidden")]
    public void AccessRestrictions_MustNotRetryOrRotate(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.ProviderAccessRestricted, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsTrue(result.ShouldOpenCircuit);
        Assert.IsTrue(result.PreserveProfile);
    }

    [TestMethod]
    public void RateLimited_RequiresPauseWithoutChangingIdentity()
    {
        var result = MaerskFailureClassifier.Classify("maersk_authentication_rate_limited");

        Assert.AreEqual(FailureCategory.ProviderRateLimited, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsTrue(result.ShouldOpenCircuit);
        Assert.IsTrue(result.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow("maersk_authentication_unauthorized")]
    [DataRow("maersk_authentication_expired")]
    public void ExpiredAuthentication_DoesNotBlindlyReauthenticate(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.AuthenticationExpired, result.Category);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow("maersk_authentication_continue_not_clickable")]
    [DataRow("maersk_authentication_callback_timeout")]
    [DataRow("maersk_post_auth_navigation_failed")]
    public void AmbiguousLoginUiFailures_KeepLegacyWaitingState(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.ProviderOrUiTimeout, result.Category);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsFalse(result.CanRetry);
    }

    [DataTestMethod]
    [DataRow("invalid_input")]
    [DataRow("invalid_profile_route")]
    [DataRow("invalid_profile_equipment")]
    [DataRow("invalid_profile_snapshot")]
    [DataRow("missing_credential")]
    public void InvalidInputs_AreTerminal(string code)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.ValidationPermanent, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsFalse(result.RequiresOperator);
    }

    [TestMethod]
    public void LocalCorruption_CannotRebuildProfileWithoutOperator()
    {
        var result = MaerskFailureClassifier.Classify("maersk_browser_profile_corrupt");

        Assert.AreEqual(FailureCategory.LocalProfileCorrupt, result.Category);
        Assert.IsTrue(result.RequiresOperator);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow("browser_session_invalid", true)]
    [DataRow("maersk_worker_connection_failed", true)]
    [DataRow("maersk_chromium_crashed", true)]
    [DataRow("browser_profile_repair_failed", false)]
    public void InfrastructureFailure_RecordsRetryEligibilityOnly(string code, bool canRetry)
    {
        var result = MaerskFailureClassifier.Classify(code);

        Assert.AreEqual(FailureCategory.InfrastructureTransient, result.Category);
        Assert.AreEqual(canRetry, result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
        Assert.IsFalse(result.ShouldOpenCircuit);
    }

    [TestMethod]
    public void OfferTimeout_IsDiagnosticAndCannotTriggerBlindRetry()
    {
        var result = MaerskFailureClassifier.Classify("maersk_offer_timeout");

        Assert.AreEqual(FailureCategory.ProviderOrUiTimeout, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsFalse(result.RequiresOperator);
        Assert.IsTrue(result.PreserveProfile);
    }

    [TestMethod]
    public void AggregateFailure_DoesNotRetryWholeBatch()
    {
        var result = MaerskFailureClassifier.Classify("maersk_all_searches_failed");

        Assert.AreEqual(FailureCategory.AggregateFailure, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
    }

    [DataTestMethod]
    [DataRow(401, FailureCategory.AuthenticationExpired)]
    [DataRow(403, FailureCategory.ProviderAccessRestricted)]
    [DataRow(429, FailureCategory.ProviderRateLimited)]
    [DataRow(503, FailureCategory.ProviderOrUiTimeout)]
    public void HttpStatusEvidence_IsStructured(int httpStatus, FailureCategory expected)
    {
        var result = MaerskFailureClassifier.Classify(null, httpStatusCode: httpStatus);

        Assert.AreEqual(expected, result.Category);
        Assert.IsTrue(result.PreserveProfile);
        Assert.IsFalse(result.CanRetry);
        if (httpStatus is 403 or 429)
            Assert.IsTrue(result.ShouldOpenCircuit);
    }

    [TestMethod]
    public void KnownCode_TakesPrecedenceOverUnrelatedHttpFailure()
    {
        var result = MaerskFailureClassifier.Classify("maersk_hcaptcha_required", httpStatusCode: 503);

        Assert.AreEqual(FailureCategory.ProviderVerificationRequired, result.Category);
    }

    [TestMethod]
    public void ErrorMessageText_DoesNotOverrideExplicitErrorCode()
    {
        var result = MaerskFailureClassifier.Classify(
            "maersk_search_failed",
            exception: new InvalidOperationException("some hcaptcha text from an arbitrary message"));

        Assert.AreEqual(FailureCategory.UnknownPermanent, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsFalse(result.RequiresOperator);
    }

    [TestMethod]
    public void TypedNetworkFailure_IsEligibleForFutureBoundedRetry()
    {
        var result = MaerskFailureClassifier.Classify(null, exception: new IOException("socket disconnected"));

        Assert.AreEqual(FailureCategory.InfrastructureTransient, result.Category);
        Assert.IsTrue(result.CanRetry);
        Assert.IsTrue(result.PreserveProfile);
    }

    [TestMethod]
    public void UnknownError_IsFailClosed()
    {
        var result = MaerskFailureClassifier.Classify("maersk_something_unexpected");

        Assert.AreEqual(FailureCategory.UnknownPermanent, result.Category);
        Assert.IsFalse(result.CanRetry);
        Assert.IsFalse(result.RequiresOperator);
        Assert.IsFalse(result.ShouldOpenCircuit);
    }

    [TestMethod]
    public void ErrorCodes_AreNormalizedWithoutParsingFreeFormMessages()
    {
        var result = MaerskFailureClassifier.Classify(" MAERSK_HCAPTCHA_REQUIRED ");

        Assert.AreEqual("maersk_hcaptcha_required", result.CanonicalErrorCode);
        Assert.AreEqual(FailureCategory.ProviderVerificationRequired, result.Category);
    }

    [DataTestMethod]
    [DataRow("maersk_hcaptcha_required", BrowserProfileStatus.Blocked)]
    [DataRow("maersk_authentication_edge_denied", BrowserProfileStatus.Blocked)]
    [DataRow("maersk_authentication_rate_limited", BrowserProfileStatus.Blocked)]
    [DataRow("maersk_authentication_unauthorized", BrowserProfileStatus.LoginRequired)]
    [DataRow("maersk_authentication_verification_required", BrowserProfileStatus.LoginRequired)]
    [DataRow("maersk_post_auth_navigation_failed", BrowserProfileStatus.LoginRequired)]
    [DataRow("maersk_authentication_service_error", BrowserProfileStatus.Error)]
    public void ProviderBrowserStatusMapping_PreservesExistingPolicy(string code, BrowserProfileStatus expected)
    {
        var method = typeof(MaerskAgentProvider).GetMethod(
            "MapBrowserProfileStatus", BindingFlags.NonPublic | BindingFlags.Static);

        Assert.IsNotNull(method);
        var actual = (BrowserProfileStatus)method!.Invoke(null, [code])!;
        Assert.AreEqual(expected, actual);
    }
}
