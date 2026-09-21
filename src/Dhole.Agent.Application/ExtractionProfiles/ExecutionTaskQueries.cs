using CustomCodeFramework.Cqrs.Queries;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetExecutionTasksQuery(Guid ExecutionId) : IQuery<IReadOnlyCollection<AgentExecutionTaskDto>>;

public sealed class GetExecutionTasksQueryHandler(IAgentExecutionTaskRepository tasks)
    : IQueryHandler<GetExecutionTasksQuery, IReadOnlyCollection<AgentExecutionTaskDto>>
{
    public async Task<IReadOnlyCollection<AgentExecutionTaskDto>> HandleAsync(GetExecutionTasksQuery query, CancellationToken cancellationToken = default)
        => (await tasks.GetByExecutionAsync(query.ExecutionId, cancellationToken))
            .Select(x => new AgentExecutionTaskDto(
                x.Id, x.ExecutionId, x.RouteId, x.EquipmentId, x.Status.ToString(), x.InputJson,
                x.StartedAt, x.CompletedAt, x.ErrorCode, x.ErrorMessage, x.SortOrder))
            .ToArray();
}
