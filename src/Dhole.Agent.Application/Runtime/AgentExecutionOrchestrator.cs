using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Domain.Agents;
using Microsoft.Extensions.Logging;

namespace Dhole.Agent.Application.Runtime;

public sealed class AgentExecutionOrchestrator(
    IAgentExecutionRepository executions,
    IAgentDefinitionRepository definitions,
    IAgentProviderRepository providers,
    IAgentCredentialRepository credentials,
    IAgentResultRepository results,
    IAgentProviderResolver providerResolver,
    IUnitOfWork unitOfWork,
    ILogger<AgentExecutionOrchestrator> logger):IAgentExecutionOrchestrator
{
    public async Task ExecuteAsync(Guid executionId,CancellationToken cancellationToken=default)
    {
        var execution=await executions.GetByIdAsync(executionId,cancellationToken);
        if(execution is null)
        {
            logger.LogError("AGENT_EXECUTION_NOT_FOUND execution={ExecutionId}", executionId);
            return;
        }

        if(execution.Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Cancelled or AgentExecutionStatus.Failed)
        {
            logger.LogInformation(
                "AGENT_EXECUTION_SKIPPED execution={ExecutionId} status={Status}",
                execution.Id,
                execution.Status);
            return;
        }

        if(execution.Status == AgentExecutionStatus.Running)
        {
            logger.LogInformation(
                "AGENT_EXECUTION_ALREADY_RUNNING execution={ExecutionId}",
                execution.Id);
            return;
        }

        try
        {
            logger.LogWarning(
                "AGENT_EXECUTION_STARTING execution={ExecutionId} status={Status} attempt={Attempt}",
                execution.Id,
                execution.Status,
                execution.Attempt);

            execution.Start(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogWarning(
                "AGENT_EXECUTION_RUNNING execution={ExecutionId} attempt={Attempt} started={StartedAt}",
                execution.Id,
                execution.Attempt,
                execution.StartedAt);

            var definition=await definitions.GetByIdAsync(execution.AgentDefinitionId,cancellationToken)
                ?? throw new InvalidOperationException($"Agent definition {execution.AgentDefinitionId} not found.");
            var provider=await providers.GetByIdAsync(execution.ProviderId,cancellationToken)
                ?? throw new InvalidOperationException($"Agent provider {execution.ProviderId} not found.");
            AgentCredential? credential=null;
            if(execution.CredentialId.HasValue)
                credential=await credentials.GetByIdAsync(execution.CredentialId.Value,cancellationToken);

            var runner=providerResolver.Resolve(provider.Code);
            var result=await runner.ExecuteAsync(
                new AgentExecutionContext(execution,definition,provider,credential),
                cancellationToken);

            if(!result.Success)
            {
                execution.Fail(
                    result.ErrorCode??"provider_failed",
                    result.ErrorMessage??"Provider execution failed.",
                    DateTime.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            if(!string.IsNullOrWhiteSpace(result.ResultType)&&!string.IsNullOrWhiteSpace(result.DataJson))
            {
                var existing=await results.GetByExecutionIdAsync(execution.Id,cancellationToken);
                if(existing is null)
                    await results.AddAsync(
                        AgentResult.Create(
                            execution.Id,
                            provider.Id,
                            result.ResultType,
                            result.SchemaVersion??"1.0",
                            result.DataJson),
                        cancellationToken);
            }

            execution.Complete(result.OutputJson,DateTime.UtcNow,result.PartiallyCompleted);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch(Exception ex) when(execution.Status is not AgentExecutionStatus.Completed and not AgentExecutionStatus.Cancelled)
        {
            logger.LogError(
                ex,
                "AGENT_EXECUTION_ERROR execution={ExecutionId} status={Status}",
                execution.Id,
                execution.Status);

            execution.Fail("agent_runtime_error",ex.Message,DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
