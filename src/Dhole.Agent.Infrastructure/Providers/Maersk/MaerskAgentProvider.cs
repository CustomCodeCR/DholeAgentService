using System.Security.Cryptography;
using System.Text.Json;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
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
    ISecretProvider legacySecrets,
    IBrowserProfileRepository browserProfiles,
    IUnitOfWork unitOfWork) : IAgentProvider
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string CurrentMaerskBookingUrl = "https://www.maersk.com/book/";

    public string ProviderCode => "MAERSK";

    public async Task<AgentProviderExecutionResult> ExecuteAsync(
        AgentExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Credential is null)
            return AgentProviderExecutionResult.Failed(
                "missing_credential",
                "Maersk execution requires a credential reference.");

        var plan = BuildPlan(context.Execution.ConfigurationSnapshotJson, context.Execution.InputJson);
        if (!plan.Success)
            return AgentProviderExecutionResult.Failed(plan.ErrorCode!, plan.ErrorMessage!);

        var browserProfile = await browserProfiles.GetByProviderCredentialAsync(
            context.Provider.Id,
            context.Credential.Id,
            cancellationToken);

        if (browserProfile?.Status is BrowserProfileStatus.Blocked or BrowserProfileStatus.LoginRequired)
        {
            return AgentProviderExecutionResult.Failed(
                browserProfile.Status == BrowserProfileStatus.Blocked
                    ? "maersk_authentication_blocked"
                    : "maersk_authentication_login_required",
                browserProfile.Status == BrowserProfileStatus.Blocked
                    ? "Maersk authentication is blocked for this browser profile. Re-authenticate the browser profile before running scheduled extraction again."
                    : "Maersk login is required for this browser profile. Update/re-authenticate the credential before running scheduled extraction again.");
        }

        var storagePath = profiles.GetStoragePath(ProviderCode, context.Credential.Id);
        var descriptor = new BrowserProfileDescriptor(
            ProviderCode,
            context.Credential.Id,
            context.Credential.Id.ToString("N"),
            storagePath);

        await using var session = await browsers.OpenPersistentAsync(descriptor, cancellationToken);
        if (session is not PlaywrightBrowserSession playwrightSession)
            return AgentProviderExecutionResult.Failed(
                "browser_session_invalid",
                "The configured browser session is not Playwright.");

        var page = playwrightSession.Context.Pages.FirstOrDefault()
                   ?? await playwrightSession.Context.NewPageAsync();

        var credentialResult = await ResolveCredentialsAsync(context.Credential, cancellationToken);
        if (!credentialResult.Success)
            return AgentProviderExecutionResult.Failed(
                credentialResult.ErrorCode!,
                credentialResult.ErrorMessage!);

        try
        {
            await login.EnsureAuthenticatedAsync(
                page,
                credentialResult.Username!,
                credentialResult.Password!,
                cancellationToken);

            if (browserProfile is not null)
            {
                browserProfile.Authenticate(DateTime.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
        }
        catch (MaerskAuthenticationException ex)
        {
            if (browserProfile is not null)
            {
                browserProfile.SetStatus(MapBrowserProfileStatus(ex.ErrorCode));
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return AgentProviderExecutionResult.Failed(
                ex.ErrorCode,
                ex.Message);
        }
        catch (Exception ex)
        {
            if (browserProfile is not null)
            {
                browserProfile.SetStatus(BrowserProfileStatus.Error);
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return AgentProviderExecutionResult.Failed(
                "maersk_authentication_failed",
                ex.Message);
        }

        var results = new List<object>();
        var errors = new List<object>();
        var completed = 0;
        var available = 0;
        var failed = 0;

        foreach (var search in plan.Searches!)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var captured = await ExecuteSearchAsync(
                    page,
                    search.Input,
                    plan.SearchUrl,
                    cancellationToken);

                if (captured.Status is < 200 or >= 300)
                    throw new InvalidOperationException(
                        $"Maersk departures/offers returned HTTP {captured.Status}.");

                var normalized = parser.Parse(captured.ResponseJson);
                var status = normalized.Offers.Count > 0 ? "Available" : "Unavailable";

                completed++;
                if (normalized.Offers.Count > 0)
                    available++;

                results.Add(new
                {
                    taskIndex = search.Index,
                    routeId = search.RouteId,
                    equipmentId = search.EquipmentId,
                    route = search.Route,
                    equipment = search.Equipment,
                    status,
                    fields = BuildConfiguredFields(plan.FieldKeys!, normalized),
                    offers = normalized.Offers,
                    captured.Status,
                    captured.CorrelationId,
                    error = (string?)null
                });
            }
            catch (Exception ex)
            {
                failed++;
                var errorCode = ex is TimeoutException
                    ? "maersk_offer_timeout"
                    : "maersk_search_failed";

                errors.Add(new
                {
                    taskIndex = search.Index,
                    routeId = search.RouteId,
                    equipmentId = search.EquipmentId,
                    errorCode,
                    errorMessage = ex.Message
                });

                results.Add(new
                {
                    taskIndex = search.Index,
                    routeId = search.RouteId,
                    equipmentId = search.EquipmentId,
                    route = search.Route,
                    equipment = search.Equipment,
                    status = "Error",
                    fields = new Dictionary<string, object?>(),
                    offers = Array.Empty<object>(),
                    error = ex.Message
                });
            }
        }

        if (completed == 0)
        {
            return AgentProviderExecutionResult.Failed(
                "maersk_all_searches_failed",
                $"Maersk completed 0 of {plan.Searches!.Count} planned searches. "
                + string.Join(
                    " | ",
                    errors.Take(5).Select(x => JsonSerializer.Serialize(x, JsonOptions))));
        }

        var outputJson = SanitizeJsonForPostgres(JsonSerializer.Serialize(
            new
            {
                provider = ProviderCode,
                providerName = "Maersk",
                extractionProfileId = context.Execution.ExtractionProfileId,
                strategy = "NativeBrowserNetworkCapture",
                action = context.Definition.ActionType.ToString(),
                plannedSearchCount = plan.Searches!.Count,
                completedSearchCount = completed,
                availableSearchCount = available,
                failedSearchCount = failed,
                data = new
                {
                    results,
                    errors
                }
            },
            JsonOptions));

        return failed > 0
            ? AgentProviderExecutionResult.Partial(
                outputJson,
                "OceanFreightRates",
                "3.0",
                outputJson)
            : AgentProviderExecutionResult.Completed(
                outputJson,
                "OceanFreightRates",
                "3.0",
                outputJson);
    }

    private async Task<CapturedMaerskOfferResponse> ExecuteSearchAsync(
        Microsoft.Playwright.IPage page,
        MaerskSearchInput input,
        string? configuredSearchUrl,
        CancellationToken cancellationToken)
    {
        var searchUrl = ResolveBrowserSearchUrl(configuredSearchUrl);
        var captureTask = interceptor.WaitForOfferAsync(
            page,
            TimeSpan.FromSeconds(90),
            cancellationToken);

        await automation.FillSearchAsync(
            page,
            input,
            cancellationToken,
            searchUrl);

        return await captureTask;
    }

    private async Task<CredentialResolution> ResolveCredentialsAsync(
        Domain.Agents.AgentCredential credential,
        CancellationToken cancellationToken)
    {
        if (credential.HasEncryptedSecrets)
        {
            try
            {
                return CredentialResolution.Ok(
                    credentialProtector.Unprotect(credential.UsernameEncrypted!),
                    credentialProtector.Unprotect(credential.PasswordEncrypted!));
            }
            catch (CryptographicException)
            {
                var fallbackUsername = await legacySecrets.GetSecretAsync(
                    "MAERSK_USERNAME",
                    cancellationToken);
                var fallbackPassword = await legacySecrets.GetSecretAsync(
                    "MAERSK_PASSWORD",
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(fallbackUsername)
                    || string.IsNullOrWhiteSpace(fallbackPassword))
                {
                    return CredentialResolution.Fail(
                        "credential_key_unavailable",
                        "The stored Maersk credential was encrypted with a Data Protection key that is no longer available. Re-save the credential or configure the temporary MAERSK_USERNAME/MAERSK_PASSWORD recovery secrets.");
                }

                credential.UpdateEncrypted(
                    credential.Name,
                    credentialProtector.Protect(fallbackUsername),
                    credentialProtector.Protect(fallbackPassword),
                    additionalSecretsEncrypted: null);

                return CredentialResolution.Ok(
                    fallbackUsername,
                    fallbackPassword);
            }
        }

        if (string.IsNullOrWhiteSpace(credential.UsernameSecretKey)
            || string.IsNullOrWhiteSpace(credential.PasswordSecretKey))
        {
            return CredentialResolution.Fail(
                "missing_credential",
                "Maersk credential does not contain encrypted values or legacy secret references.");
        }

        var username = await legacySecrets.GetSecretAsync(
            credential.UsernameSecretKey,
            cancellationToken);
        var password = await legacySecrets.GetSecretAsync(
            credential.PasswordSecretKey,
            cancellationToken);

        return string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)
            ? CredentialResolution.Fail(
                "missing_credential",
                "The configured Maersk credential secrets are not available.")
            : CredentialResolution.Ok(username, password);
    }

    private PlanResolution BuildPlan(
        string? configurationSnapshotJson,
        string? runtimeInputJson)
    {
        if (string.IsNullOrWhiteSpace(configurationSnapshotJson))
            return BuildLegacyPlan(runtimeInputJson);

        try
        {
            using var document = JsonDocument.Parse(configurationSnapshotJson);
            var root = document.RootElement;

            var routes = ReadArray(root, "routes");
            var equipmentItems = ReadArray(root, "equipment");
            var fieldKeys = ReadArray(root, "fields")
                .Select(x => TryGetString(x, "key"))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!)
                .ToArray();

            if (routes.Count == 0 || equipmentItems.Count == 0)
            {
                return PlanResolution.Fail(
                    "extraction_profile_plan_empty",
                    "The selected Maersk profile must contain at least one active route and one active equipment.");
            }

            var runtime = ParseRuntime(runtimeInputJson);
            if (!runtime.CargoReadyDate.HasValue)
            {
                return PlanResolution.Fail(
                    "missing_cargo_ready_date",
                    "cargoReadyDate is required for Maersk searches.");
            }

            var commodity = commodities.Normalize(runtime.Commodity ?? "General Cargo");
            var searches = new List<PlannedSearch>();
            var index = 1;

            foreach (var route in routes)
            {
                var pol = TryGetString(route, "polName");
                var destination =
                    TryGetString(route, "poeName")
                    ?? TryGetString(route, "podName");

                if (string.IsNullOrWhiteSpace(pol)
                    || string.IsNullOrWhiteSpace(destination))
                {
                    return PlanResolution.Fail(
                        "invalid_profile_route",
                        "Every Maersk extraction route must define POL and POE or POD.");
                }

                foreach (var item in equipmentItems)
                {
                    var code = TryGetString(item, "code");
                    var quantity = TryGetInt(item, "quantity") ?? 1;
                    var weightKg = TryGetDecimal(item, "defaultWeightKg");

                    if (string.IsNullOrWhiteSpace(code) || !weightKg.HasValue || weightKg <= 0)
                    {
                        return PlanResolution.Fail(
                            "invalid_profile_equipment",
                            "Every Maersk extraction equipment must define code and a positive defaultWeightKg.");
                    }

                    var input = new MaerskSearchInput(
                        locations.Normalize(pol),
                        locations.Normalize(destination),
                        equipment.Normalize(code),
                        Math.Max(1, quantity),
                        weightKg.Value,
                        commodity,
                        runtime.CargoReadyDate.Value);

                    searches.Add(new PlannedSearch(
                        index++,
                        TryGetString(route, "id"),
                        TryGetString(item, "id"),
                        route.Clone(),
                        item.Clone(),
                        input));
                }
            }

            return PlanResolution.Ok(
                searches,
                fieldKeys,
                TryGetString(root, "searchUrl"));
        }
        catch (JsonException ex)
        {
            return PlanResolution.Fail(
                "invalid_profile_snapshot",
                $"The Maersk extraction profile snapshot is invalid JSON: {ex.Message}");
        }
    }

    private PlanResolution BuildLegacyPlan(string? runtimeInputJson)
    {
        try
        {
            var input = JsonSerializer.Deserialize<MaerskSearchInput>(
                runtimeInputJson ?? string.Empty,
                JsonOptions)
                ?? throw new JsonException("Input payload is empty.");

            input = input with
            {
                Pol = locations.Normalize(input.Pol),
                Pod = locations.Normalize(input.Pod),
                ContainerType = equipment.Normalize(input.ContainerType),
                Commodity = commodities.Normalize(input.Commodity)
            };

            var route = JsonSerializer.SerializeToElement(
                new { polName = input.Pol, poeName = input.Pod },
                JsonOptions);
            var item = JsonSerializer.SerializeToElement(
                new
                {
                    code = input.ContainerType,
                    quantity = input.Quantity,
                    defaultWeightKg = input.WeightKg
                },
                JsonOptions);

            return PlanResolution.Ok(
                [
                    new PlannedSearch(
                        1,
                        null,
                        null,
                        route,
                        item,
                        input)
                ],
                [],
                CurrentMaerskBookingUrl);
        }
        catch (Exception ex)
        {
            return PlanResolution.Fail("invalid_input", ex.Message);
        }
    }

    private static Dictionary<string, object?> BuildConfiguredFields(
        IReadOnlyCollection<string> fieldKeys,
        NormalizedOceanFreightRates normalized)
    {
        var first = normalized.Offers.FirstOrDefault();
        var values = new Dictionary<string, object?>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in fieldKeys)
        {
            values[key] = key.Trim().ToLowerInvariant() switch
            {
                "etd" => first?.Etd,
                "eta" => first?.Eta,
                "transittime" or "transitdays" => first?.TransitDays,
                "vessel" => first?.Vessel,
                "voyage" => first?.Voyage,
                "price" or "oceanfreight" or "totalbasicfreightamount" => first?.OceanFreight?.Amount,
                "currency" => first?.OceanFreight?.Currency ?? first?.AllIn?.Currency,
                "allin" => first?.AllIn?.Amount,
                "pricebreakdown" or "charges" => first?.Charges,
                "route" or "legs" => first?.Legs,
                "availability" or "available" => first?.Available,
                _ => null
            };
        }

        return values;
    }

    private static string ResolveBrowserSearchUrl(string? configuredSearchUrl)
    {
        if (!Uri.TryCreate(configuredSearchUrl, UriKind.Absolute, out var uri))
            return CurrentMaerskBookingUrl;

        if (uri.Host.Equals("api.maersk.com", StringComparison.OrdinalIgnoreCase))
            return CurrentMaerskBookingUrl;

        if (uri.Host.EndsWith("maersk.com", StringComparison.OrdinalIgnoreCase))
            return uri.ToString();

        return CurrentMaerskBookingUrl;
    }

    private static BrowserProfileStatus MapBrowserProfileStatus(string errorCode)
        => errorCode switch
        {
            "maersk_authentication_forbidden" => BrowserProfileStatus.Blocked,
            "maersk_authentication_rate_limited" => BrowserProfileStatus.Blocked,
            "maersk_authentication_unauthorized" => BrowserProfileStatus.LoginRequired,
            "maersk_authentication_verification_required" => BrowserProfileStatus.LoginRequired,
            "maersk_authentication_service_error" => BrowserProfileStatus.Error,
            _ => BrowserProfileStatus.Error
        };

    private static RuntimeInput ParseRuntime(string? inputJson)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
            return new RuntimeInput(null, null);

        try
        {
            using var document = JsonDocument.Parse(inputJson);
            var root = document.RootElement;
            DateOnly? cargoReadyDate = null;

            var rawDate = TryGetString(root, "cargoReadyDate");
            if (DateOnly.TryParse(rawDate, out var parsedDate))
                cargoReadyDate = parsedDate;

            return new RuntimeInput(
                cargoReadyDate,
                TryGetString(root, "commodity"));
        }
        catch (JsonException)
        {
            return new RuntimeInput(null, null);
        }
    }

    private static IReadOnlyList<JsonElement> ReadArray(
        JsonElement root,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(root, propertyName, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Select(x => x.Clone())
            .ToArray();
    }

    private static string? TryGetString(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : value.ValueKind == JsonValueKind.Null
                ? null
                : value.ToString();
    }

    private static int? TryGetInt(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var number))
            return number;

        return int.TryParse(value.ToString(), out number)
            ? number
            : null;
    }

    private static decimal? TryGetDecimal(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
            return null;

        if (value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out var number))
            return number;

        return decimal.TryParse(
            value.ToString(),
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out number)
            ? number
            : null;
    }

    private static string SanitizeJsonForPostgres(string json)
    {
        if (string.IsNullOrEmpty(json))
            return json;

        // PostgreSQL jsonb rejects U+0000 even when it is represented as \u0000.
        // Carrier responses can occasionally contain control characters in free-text
        // labels, so strip only the NUL codepoint before persistence.
        return json
            .Replace("\\u0000", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("\0", string.Empty, StringComparison.Ordinal);
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out value))
                return true;

            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(
                        propertyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private sealed record RuntimeInput(
        DateOnly? CargoReadyDate,
        string? Commodity);

    private sealed record PlannedSearch(
        int Index,
        string? RouteId,
        string? EquipmentId,
        JsonElement Route,
        JsonElement Equipment,
        MaerskSearchInput Input);

    private sealed record PlanResolution(
        bool Success,
        IReadOnlyList<PlannedSearch>? Searches,
        IReadOnlyCollection<string>? FieldKeys,
        string? SearchUrl,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static PlanResolution Ok(
            IReadOnlyList<PlannedSearch> searches,
            IReadOnlyCollection<string> fieldKeys,
            string? searchUrl)
            => new(true, searches, fieldKeys, searchUrl, null, null);

        public static PlanResolution Fail(string code, string message)
            => new(false, null, null, null, code, message);
    }

    private sealed record CredentialResolution(
        bool Success,
        string? Username,
        string? Password,
        string? ErrorCode,
        string? ErrorMessage)
    {
        public static CredentialResolution Ok(string username, string password)
            => new(true, username, password, null, null);

        public static CredentialResolution Fail(string code, string message)
            => new(false, null, null, code, message);
    }
}
