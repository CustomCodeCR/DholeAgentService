namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExecutionPromptSnapshotDto(
    Guid ExecutionId,
    Guid? ExtractionProfileId,
    string? PromptSnapshot,
    string? ConfigurationSnapshotJson);
