using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetExecutionPromptSnapshotQuery(Guid ExecutionId) : IQuery<Result<AgentExecutionPromptSnapshotDto>>;

public sealed class GetExecutionPromptSnapshotQueryHandler(IAgentExecutionRepository executions)
    : IQueryHandler<GetExecutionPromptSnapshotQuery, Result<AgentExecutionPromptSnapshotDto>>
{
    public async Task<Result<AgentExecutionPromptSnapshotDto>> HandleAsync(GetExecutionPromptSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        var execution = await executions.GetByIdAsync(query.ExecutionId, cancellationToken);
        if (execution is null)
            return Result.Failure<AgentExecutionPromptSnapshotDto>(new Error("Agent.ExecutionNotFound", "Agent execution not found."));

        return Result.Success(new AgentExecutionPromptSnapshotDto(
            execution.Id,
            execution.ExtractionProfileId,
            execution.PromptSnapshot,
            execution.ConfigurationSnapshotJson));
    }
}
