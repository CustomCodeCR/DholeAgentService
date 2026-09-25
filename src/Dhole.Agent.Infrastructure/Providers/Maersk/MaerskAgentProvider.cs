using System.Security.Cryptography;
using System.Text.Json;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Abstractions.Security;
using Dhole.Agent.Infrastructure.Browser;
using Dhole.Agent.Infrastructure.Providers.Maersk.Authentication;
using Dhole.Agent.Infrastructure.Providers.Maersk.Browser;
using Dhole.Agent.Infrastructure.Providers.Maersk.Models;
using Dhole.Agent.Infrastructure.Providers.Maersk.Network;
using Dhole.Agent.Infrastructure.Providers.Maersk.Parsers;
using Dhole.Agent.Infrastructure.Providers.Maersk.Resolvers;

namespace Dhole.Agent.Infrastructure.Providers.Maersk;

public sealed class MaerskAgentProvider(
    IBrowserManager browsers,
    IBrowserProfileManager profiles,
    MaerskLoginService login,
    MaerskBrowserAutomation automation,
    MaerskOfferInterceptor interceptor,
    MaerskOfferParser parser,
    MaerskLocationResolver locations,
    MaerskEquipmentResolver equipment,
    MaerskCommodityResolver commodities,
    ICredentialProtector credentialProtector,
    ISecretProvider legacySecrets) : IAgentProvider
{
    public string ProviderCode => "MAERSK";

    public async Task<AgentProviderExecutionResult> ExecuteAsync(AgentExecutionContext context, CancellationToken cancellationToken)
    {
        if (context.Credential is null)
            return AgentProviderExecutionResult.Failed("missing_credential", "Maersk execution requires a credential reference.");

        MaerskSearchInput input;
        try
        {
            input = JsonSerializer.Deserialize<MaerskSearchInput>(context.Execution.InputJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new JsonException("Input payload is empty.");
            input = input with
            {
                Pol = locations.Normalize(input.Pol),
                Pod = locations.Normalize(input.Pod),
                ContainerType = equipment.Normalize(input.ContainerType),
                Commodity = commodities.Normalize(input.Commodity)
            };
        }
        catch (Exception ex)
        {
            return AgentProviderExecutionResult.Failed("invalid_input", ex.Message);
        }

        var storagePath = profiles.GetStoragePath(ProviderCode, context.Credential.Id);
        var descriptor = new BrowserProfileDescriptor(ProviderCode, context.Credential.Id, context.Credential.Id.ToString("N"), storagePath);

        await using var session = await browsers.OpenPersistentAsync(descriptor, cancellationToken);
        if (session is not PlaywrightBrowserSession playwrightSession)
            return AgentProviderExecutionResult.Failed("browser_session_invalid", "The configured browser session is not Playwright.");

        var page = playwrightSession.Context.Pages.FirstOrDefault() ?? await playwrightSession.Context.NewPageAsync();

        string username;
        string password;
        if (context.Credential.HasEncryptedSecrets)
        {
            try
            {
                username = credentialProtector.Unprotect(context.Credential.UsernameEncrypted!);
                password = credentialProtector.Unprotect(context.Credential.PasswordEncrypted!);
            }
            catch (CryptographicException)
            {
                // Credentials created before the Data Protection key ring became shared/persistent
                // can reference a key that no longer exists after a container redeploy.
                // Keep scheduled extractions operational through the existing environment-secret
                // compatibility path, then let the credential be re-saved under the shared key ring.
                var fallbackUsername = await legacySecrets.GetSecretAsync("MAERSK_USERNAME", cancellationToken);
                var fallbackPassword = await legacySecrets.GetSecretAsync("MAERSK_PASSWORD", cancellationToken);

                if (string.IsNullOrWhiteSpace(fallbackUsername) || string.IsNullOrWhiteSpace(fallbackPassword))
                {
                    return AgentProviderExecutionResult.Failed(
                        "credential_key_unavailable",
                        "The stored Maersk credential was encrypted with a Data Protection key that is no longer available. Re-save the credential so it is encrypted with the current shared key ring, or configure MAERSK_USERNAME and MAERSK_PASSWORD as a temporary recovery path.");
                }

                username = fallbackUsername;
                password = fallbackPassword;

                // Self-heal the selected credential: the orchestrator keeps this entity
                // tracked and persists it together with the execution state.
                context.Credential.UpdateEncrypted(
                    context.Credential.Name,
                    credentialProtector.Protect(username),
                    credentialProtector.Protect(password),
                    additionalSecretsEncrypted: null);
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(context.Credential.UsernameSecretKey) ||
                string.IsNullOrWhiteSpace(context.Credential.PasswordSecretKey))
                return AgentProviderExecutionResult.Failed("missing_credential", "Maersk credential does not contain encrypted values or legacy secret references.");

            username = await legacySecrets.GetSecretAsync(context.Credential.UsernameSecretKey, cancellationToken)
                ?? throw new InvalidOperationException("Legacy Maersk username secret is not configured.");
            password = await legacySecrets.GetSecretAsync(context.Credential.PasswordSecretKey, cancellationToken)
                ?? throw new InvalidOperationException("Legacy Maersk password secret is not configured.");
        }

        await login.EnsureAuthenticatedAsync(page, username, password, cancellationToken);

        var captureTask = interceptor.WaitForOfferAsync(page, TimeSpan.FromSeconds(90), cancellationToken);
        var searchUrl = ResolveSearchUrl(context.Execution.ConfigurationSnapshotJson);
        await automation.FillSearchAsync(page, input, cancellationToken, searchUrl);
        var captured = await captureTask;

        if (captured.Status is < 200 or >= 300)
            return AgentProviderExecutionResult.Failed("maersk_offer_http_error", $"Maersk departures/offers returned HTTP {captured.Status}.");

        var normalized = parser.Parse(captured.ResponseJson);
        var dataJson = JsonSerializer.Serialize(normalized, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var output = JsonSerializer.Serialize(new
        {
            captured.Status,
            captured.CorrelationId,
            offerCount = normalized.Offers.Count
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        return AgentProviderExecutionResult.Completed(output, "OceanFreightRates", "1.0", dataJson);
    }
    private static string? ResolveSearchUrl(string? configurationSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(configurationSnapshotJson)) return null;

        try
        {
            using var document = JsonDocument.Parse(configurationSnapshotJson);
            if (document.RootElement.TryGetProperty("searchUrl", out var searchUrl) &&
                searchUrl.ValueKind == JsonValueKind.String)
            {
                return searchUrl.GetString();
            }
        }
        catch (JsonException)
        {
            // Keep backward compatibility with executions created before profile snapshots.
        }

        return null;
    }
}
