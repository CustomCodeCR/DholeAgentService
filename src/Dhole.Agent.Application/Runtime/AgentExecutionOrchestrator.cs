using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Runtime;

public sealed class AgentExecutionOrchestrator(
    IAgentExecutionRepository executions,
    IAgentDefinitionRepository definitions,
    IAgentProviderRepository providers,
    IAgentCredentialRepository credentials,
    IAgentResultRepository results,
    IAgentProviderResolver providerResolver,
    IUnitOfWork unitOfWork):IAgentExecutionOrchestrator
{
    public async Task ExecuteAsync(Guid executionId,CancellationToken cancellationToken=default)
    {
        var execution=await executions.GetByIdAsync(executionId,cancellationToken);
        if(execution is null||execution.Status is AgentExecutionStatus.Completed or AgentExecutionStatus.Cancelled)return;

        var definition=await definitions.GetByIdAsync(execution.AgentDefinitionId,cancellationToken)
            ?? throw new InvalidOperationException($"Agent definition {execution.AgentDefinitionId} not found.");
        var provider=await providers.GetByIdAsync(execution.ProviderId,cancellationToken)
            ?? throw new InvalidOperationException($"Agent provider {execution.ProviderId} not found.");
        AgentCredential? credential=null;
        if(execution.CredentialId.HasValue)credential=await credentials.GetByIdAsync(execution.CredentialId.Value,cancellationToken);

        try
        {
            execution.Start(DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            var runner=providerResolver.Resolve(provider.Code);
            var result=await runner.ExecuteAsync(new AgentExecutionContext(execution,definition,provider,credential),cancellationToken);

            if(!result.Success)
            {
                execution.Fail(result.ErrorCode??"provider_failed",result.ErrorMessage??"Provider execution failed.",DateTime.UtcNow);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                return;
            }

            if(!string.IsNullOrWhiteSpace(result.ResultType)&&!string.IsNullOrWhiteSpace(result.DataJson))
            {
                var existing=await results.GetByExecutionIdAsync(execution.Id,cancellationToken);
                if(existing is null)
                    await results.AddAsync(AgentResult.Create(execution.Id,provider.Id,result.ResultType,result.SchemaVersion??"1.0",result.DataJson),cancellationToken);
            }

            execution.Complete(result.OutputJson,DateTime.UtcNow,result.PartiallyCompleted);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
        catch(Exception ex) when(execution.Status is not AgentExecutionStatus.Completed and not AgentExecutionStatus.Cancelled)
        {
            execution.Fail("agent_runtime_error",ex.Message,DateTime.UtcNow);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }
    }
}
