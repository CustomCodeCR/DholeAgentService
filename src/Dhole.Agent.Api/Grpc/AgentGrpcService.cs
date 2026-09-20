using CustomCodeFramework.Cqrs.Dispatching;
using Dhole.Agent.Application.Agents;
using Dhole.Agent.Contracts.Grpc;
using Grpc.Core;

namespace Dhole.Agent.Api.Grpc;

public sealed class AgentGrpcService(
    ICommandDispatcher commands,
    IQueryDispatcher queries) : AgentService.AgentServiceBase
{
    public override async Task<SearchOceanRatesResponse> SearchOceanRates(
        SearchOceanRatesRequest request,
        ServerCallContext context)
    {
        var definitionId = ParseGuid(request.AgentDefinitionId, nameof(request.AgentDefinitionId));
        var providerId = ParseGuid(request.ProviderId, nameof(request.ProviderId));
        Guid? credentialId = string.IsNullOrWhiteSpace(request.CredentialId)
            ? null
            : ParseGuid(request.CredentialId, nameof(request.CredentialId));

        var result = await commands.DispatchAsync(
            new CreateAgentExecutionCommand(
                definitionId,
                providerId,
                credentialId,
                request.Priority,
                request.InputJson,
                request.MaxAttempts <= 0 ? 3 : request.MaxAttempts,
                string.IsNullOrWhiteSpace(request.CorrelationId) ? null : request.CorrelationId,
                string.IsNullOrWhiteSpace(request.TraceId) ? null : request.TraceId,
                null),
            context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.FailedPrecondition, result.Error.Message));

        return new SearchOceanRatesResponse
        {
            ExecutionId = result.Value.ToString(),
            Status = "Queued"
        };
    }

    public override async Task<GetExecutionResponse> GetExecution(
        GetExecutionRequest request,
        ServerCallContext context)
    {
        var id = ParseGuid(request.ExecutionId, nameof(request.ExecutionId));
        var result = await queries.DispatchAsync(new GetAgentExecutionByIdQuery(id), context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error.Message));

        var execution = result.Value;
        return new GetExecutionResponse
        {
            ExecutionId = execution.Id.ToString(),
            Status = execution.Status,
            ExecutionType = execution.ExecutionType,
            Priority = execution.Priority,
            InputJson = execution.InputJson ?? string.Empty,
            OutputJson = execution.OutputJson ?? string.Empty,
            StartedAt = Format(execution.StartedAt),
            CompletedAt = Format(execution.CompletedAt),
            DurationMs = execution.DurationMs ?? 0,
            Attempt = execution.Attempt,
            MaxAttempts = execution.MaxAttempts,
            ErrorCode = execution.ErrorCode ?? string.Empty,
            ErrorMessage = execution.ErrorMessage ?? string.Empty,
            CorrelationId = execution.CorrelationId ?? string.Empty,
            TraceId = execution.TraceId ?? string.Empty
        };
    }

    public override async Task<GetExecutionResultResponse> GetExecutionResult(
        GetExecutionResultRequest request,
        ServerCallContext context)
    {
        var id = ParseGuid(request.ExecutionId, nameof(request.ExecutionId));
        var result = await queries.DispatchAsync(new GetAgentExecutionResultQuery(id), context.CancellationToken);

        if (!result.IsSuccess)
            throw new RpcException(new Status(StatusCode.NotFound, result.Error.Message));

        var executionResult = result.Value;
        return new GetExecutionResultResponse
        {
            ExecutionId = executionResult.ExecutionId.ToString(),
            ResultType = executionResult.ResultType,
            SchemaVersion = executionResult.SchemaVersion,
            DataJson = executionResult.DataJson,
            CreatedAt = executionResult.CreatedAtUtc.ToString("O")
        };
    }

    private static Guid ParseGuid(string value, string field)
        => Guid.TryParse(value, out var id)
            ? id
            : throw new RpcException(new Status(StatusCode.InvalidArgument, $"{field} must be a valid GUID."));

    private static string Format(DateTime? value) => value?.ToString("O") ?? string.Empty;
}
