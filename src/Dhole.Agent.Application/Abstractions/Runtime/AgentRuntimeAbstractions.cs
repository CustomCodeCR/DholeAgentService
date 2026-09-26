using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Abstractions.Runtime;

public sealed record AgentExecutionContext(
    AgentExecution Execution,
    AgentDefinition Definition,
    AgentProvider Provider,
    AgentCredential? Credential,
    int? TimeoutSeconds = null);

public sealed record AgentProviderExecutionResult(
    bool Success,
    bool PartiallyCompleted,
    string? OutputJson,
    string? ResultType,
    string? SchemaVersion,
    string? DataJson,
    string? ErrorCode,
    string? ErrorMessage)
{
    public static AgentProviderExecutionResult Completed(string outputJson,string resultType,string schemaVersion,string dataJson)
        => new(true,false,outputJson,resultType,schemaVersion,dataJson,null,null);
    public static AgentProviderExecutionResult Partial(string outputJson,string resultType,string schemaVersion,string dataJson)
        => new(true,true,outputJson,resultType,schemaVersion,dataJson,null,null);
    public static AgentProviderExecutionResult Failed(string code,string message)
        => new(false,false,null,null,null,null,code,message);
}

public interface IAgentProvider
{
    string ProviderCode { get; }
    Task<AgentProviderExecutionResult> ExecuteAsync(AgentExecutionContext context,CancellationToken cancellationToken);
}

public interface IAgentProviderResolver
{
    IAgentProvider Resolve(string providerCode, AgentExecutionStrategy? executionStrategy = null);
}

public interface IAgentRuntime
{
    Task<string> ExecuteAsync(
        string instruction,
        string? contextJson,
        CancellationToken cancellationToken = default,
        int? timeoutSeconds = null);
}

public interface ISecretProvider
{
    Task<string?> GetSecretAsync(string key,CancellationToken cancellationToken=default);
}

public interface IBrowserSession:IAsyncDisposable { }

public sealed record BrowserProfileDescriptor(string ProviderCode,Guid CredentialId,string ProfileKey,string StoragePath);

public interface IBrowserManager
{
    Task<IBrowserSession> OpenPersistentAsync(BrowserProfileDescriptor profile,CancellationToken cancellationToken=default);
}

public interface IBrowserProfileManager
{
    string GetStoragePath(string providerCode,Guid credentialId);
}

public interface IAgentExecutionOrchestrator
{
    Task ExecuteAsync(Guid executionId,CancellationToken cancellationToken=default);
}
