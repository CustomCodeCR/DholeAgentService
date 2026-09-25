using CustomCodeFramework.Core.Results;

namespace Dhole.Agent.Application.Agents;

public static class AgentErrors
{
    public static readonly Error ProviderNotFound = new("Agent.ProviderNotFound","Agent provider not found.");
    public static readonly Error ProviderCodeExists = new("Agent.ProviderCodeExists","An agent provider with the same code already exists.");
    public static readonly Error DefinitionNotFound = new("Agent.DefinitionNotFound","Agent definition not found.");
    public static readonly Error DefinitionCodeExists = new("Agent.DefinitionCodeExists","An agent definition with the same code already exists.");
    public static readonly Error CredentialNotFound = new("Agent.CredentialNotFound","Agent credential not found.");
    public static readonly Error CredentialPasswordRequired = new("Agent.CredentialPasswordRequired","A password is required when migrating a legacy credential.");
    public static readonly Error CredentialVerificationFailed = new("Agent.CredentialVerificationFailed","The stored credential could not be decrypted or resolved.");
    public static readonly Error BrowserProfileNotFound = new("Agent.BrowserProfileNotFound","Browser profile not found.");
    public static readonly Error ScheduleNotFound = new("Agent.ScheduleNotFound","Agent schedule not found.");
    public static readonly Error ExecutionNotFound = new("Agent.ExecutionNotFound","Agent execution not found.");
    public static readonly Error ExecutionResultNotFound = new("Agent.ExecutionResultNotFound","Agent execution result not found.");
    public static readonly Error ExtractionProfileNotFound = new("Agent.ExtractionProfileNotFound","Agent extraction profile not found.");
    public static readonly Error ExtractionProfileCredentialProviderMismatch = new("Agent.ExtractionProfileCredentialProviderMismatch","The selected credential belongs to a different provider.");
    public static readonly Error ExtractionProfileProviderMismatch = new("Agent.ExtractionProfileProviderMismatch","The selected extraction profile belongs to a different provider.");
}
