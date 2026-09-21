namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentPromptPreviewRequest(DateOnly? CargoReadyDate, Guid? ExecutionId);
public sealed record AgentPromptPreviewDto(string Prompt, IReadOnlyCollection<string> AvailableVariables);
