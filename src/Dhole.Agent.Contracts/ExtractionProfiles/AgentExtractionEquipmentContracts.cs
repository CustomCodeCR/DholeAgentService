namespace Dhole.Agent.Contracts.ExtractionProfiles;

public sealed record AgentExtractionEquipmentDto(Guid Id, Guid ProfileId, string Code, string Name, int Quantity, decimal DefaultWeightKg, bool IsActive, int SortOrder);
public sealed record SaveAgentExtractionEquipmentRequest(string Code, string Name, int Quantity, decimal DefaultWeightKg, bool IsActive, int SortOrder);
