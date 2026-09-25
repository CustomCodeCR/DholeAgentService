using Dhole.Agent.Application.Abstractions.Security;
using Dhole.Agent.Contracts.Agents;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Agents;

internal static class AgentMappings
{
    public static AgentProviderDto ToDto(this AgentProvider x) => new(x.Id,x.Code,x.Name,x.ProviderType.ToString(),x.BaseUrl,x.DefaultExecutionStrategy.ToString(),x.IsSystem,x.IsActive,x.MetadataJson,x.CreatedAtUtc,x.UpdatedAtUtc);
    public static AgentDefinitionDto ToDto(this AgentDefinition x) => new(x.Id,x.ProviderId,x.Code,x.Name,x.Description,x.ActionType.ToString(),x.ExecutionStrategy.ToString(),x.ConfigurationJson,x.IsActive,x.CreatedAtUtc,x.UpdatedAtUtc);
    public static AgentCredentialDto ToDto(this AgentCredential x, ICredentialProtector protector)
    {
        var usernameMasked = "********";
        if (!string.IsNullOrWhiteSpace(x.UsernameEncrypted))
        {
            try { usernameMasked = MaskUsername(protector.Unprotect(x.UsernameEncrypted)); }
            catch { usernameMasked = "********"; }
        }
        else if (!string.IsNullOrWhiteSpace(x.UsernameSecretKey))
        {
            usernameMasked = "configured (legacy)";
        }

        return new(x.Id,x.ProviderId,x.Name,usernameMasked,x.HasEncryptedSecrets || !string.IsNullOrWhiteSpace(x.PasswordSecretKey),x.IsActive,x.CreatedAtUtc,x.UpdatedAtUtc);
    }
    public static BrowserProfileDto ToDto(this BrowserProfile x) => new(x.Id,x.ProviderId,x.CredentialId,x.Name,x.ProfileKey,x.StoragePath,x.Status.ToString(),x.LastLoginAt,x.LastUsedAt,x.SessionExpiresAt,x.IsActive);
    public static AgentScheduleDto ToDto(this AgentSchedule x) => new(x.Id,x.Name,x.AgentDefinitionId,x.ProviderId,x.CredentialId,x.ScheduleType.ToString(),x.CronExpression,x.IntervalMinutes,x.ExecuteAt,x.Timezone,x.InputJson,x.IsActive,x.LastExecutionAt,x.NextExecutionAt,x.MaxRetries,x.TimeoutSeconds);
    public static AgentExecutionDto ToDto(this AgentExecution x) => new(x.Id,x.AgentDefinitionId,x.ProviderId,x.ScheduleId,x.CredentialId,x.ExtractionProfileId,x.ExecutionType.ToString(),x.Status.ToString(),x.Priority,x.InputJson,x.OutputJson,x.StartedAt,x.CompletedAt,x.DurationMs,x.Attempt,x.MaxAttempts,x.ErrorCode,x.ErrorMessage,x.CorrelationId,x.TraceId,x.CreatedAtUtc);
    public static AgentResultDto ToDto(this AgentResult x) => new(x.Id,x.ExecutionId,x.ProviderId,x.ResultType,x.SchemaVersion,x.DataJson,x.CreatedAtUtc);

    private static string MaskUsername(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "********";
        var at = value.IndexOf('@');
        var local = at >= 0 ? value[..at] : value;
        var domain = at >= 0 ? value[at..] : string.Empty;
        var visibleLength = Math.Min(local.Length, local.Length <= 1 ? 1 : 2);
        var visible = local[..visibleLength];
        return string.Concat(visible, new string('*', Math.Max(3, local.Length - visible.Length)), domain);
    }
}
