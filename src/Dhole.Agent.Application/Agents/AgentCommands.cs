using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Application.Abstractions.Runtime;
using Dhole.Agent.Application.Abstractions.Security;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.Agents;

public sealed record CreateAgentProviderCommand(string Code,string Name,AgentProviderType ProviderType,string? BaseUrl,AgentExecutionStrategy DefaultExecutionStrategy,bool IsSystem,string? MetadataJson,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateAgentProviderCommandHandler(IAgentProviderRepository repo,IUnitOfWork uow):ICommandHandler<CreateAgentProviderCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateAgentProviderCommand c,CancellationToken ct=default)
    {
        if(await repo.ExistsByCodeAsync(c.Code,cancellationToken:ct)) return Result.Failure<Guid>(AgentErrors.ProviderCodeExists);
        var e=AgentProvider.Create(c.Code,c.Name,c.ProviderType,c.BaseUrl,c.DefaultExecutionStrategy,c.IsSystem,c.MetadataJson,c.ActorId);
        await repo.AddAsync(e,ct); await uow.SaveChangesAsync(ct); return Result.Success(e.Id);
    }
}
public sealed record UpdateAgentProviderCommand(Guid Id,string Name,AgentProviderType ProviderType,string? BaseUrl,AgentExecutionStrategy DefaultExecutionStrategy,string? MetadataJson,Guid? ActorId):ICommand<Result>;
public sealed class UpdateAgentProviderCommandHandler(IAgentProviderRepository repo,IUnitOfWork uow):ICommandHandler<UpdateAgentProviderCommand,Result>
{
    public async Task<Result> HandleAsync(UpdateAgentProviderCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.ProviderNotFound);e.Update(c.Name,c.ProviderType,c.BaseUrl,c.DefaultExecutionStrategy,c.MetadataJson,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}
public sealed record SetAgentProviderActiveCommand(Guid Id,bool IsActive,Guid? ActorId):ICommand<Result>;
public sealed class SetAgentProviderActiveCommandHandler(IAgentProviderRepository repo,IUnitOfWork uow):ICommandHandler<SetAgentProviderActiveCommand,Result>
{
    public async Task<Result> HandleAsync(SetAgentProviderActiveCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.ProviderNotFound);e.SetActive(c.IsActive,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}

public sealed record CreateAgentDefinitionCommand(Guid ProviderId,string Code,string Name,string? Description,AgentActionType ActionType,AgentExecutionStrategy ExecutionStrategy,string? ConfigurationJson,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateAgentDefinitionCommandHandler(IAgentDefinitionRepository repo,IAgentProviderRepository providers,IUnitOfWork uow):ICommandHandler<CreateAgentDefinitionCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateAgentDefinitionCommand c,CancellationToken ct=default){var p=await providers.GetByIdAsync(c.ProviderId,ct);if(p is null||p.IsDeleted)return Result.Failure<Guid>(AgentErrors.ProviderNotFound);if(await repo.ExistsByCodeAsync(c.Code,cancellationToken:ct))return Result.Failure<Guid>(AgentErrors.DefinitionCodeExists);var e=AgentDefinition.Create(c.ProviderId,c.Code,c.Name,c.Description,c.ActionType,c.ExecutionStrategy,c.ConfigurationJson,c.ActorId);await repo.AddAsync(e,ct);await uow.SaveChangesAsync(ct);return Result.Success(e.Id);}
}
public sealed record UpdateAgentDefinitionCommand(Guid Id,string Name,string? Description,AgentActionType ActionType,AgentExecutionStrategy ExecutionStrategy,string? ConfigurationJson,Guid? ActorId):ICommand<Result>;
public sealed class UpdateAgentDefinitionCommandHandler(IAgentDefinitionRepository repo,IUnitOfWork uow):ICommandHandler<UpdateAgentDefinitionCommand,Result>
{
    public async Task<Result> HandleAsync(UpdateAgentDefinitionCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.DefinitionNotFound);e.Update(c.Name,c.Description,c.ActionType,c.ExecutionStrategy,c.ConfigurationJson,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}
public sealed record SetAgentDefinitionActiveCommand(Guid Id,bool IsActive,Guid? ActorId):ICommand<Result>;
public sealed class SetAgentDefinitionActiveCommandHandler(IAgentDefinitionRepository repo,IUnitOfWork uow):ICommandHandler<SetAgentDefinitionActiveCommand,Result>
{
    public async Task<Result> HandleAsync(SetAgentDefinitionActiveCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.DefinitionNotFound);e.SetActive(c.IsActive,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}

public sealed record CreateAgentCredentialCommand(Guid ProviderId,string Name,string Username,string Password,string? AdditionalSecretsJson,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateAgentCredentialCommandHandler(
    IAgentCredentialRepository repo,
    IAgentProviderRepository providers,
    ICredentialProtector protector,
    IUnitOfWork uow):ICommandHandler<CreateAgentCredentialCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateAgentCredentialCommand c,CancellationToken ct=default)
    {
        var p=await providers.GetByIdAsync(c.ProviderId,ct);
        if(p is null||p.IsDeleted)return Result.Failure<Guid>(AgentErrors.ProviderNotFound);

        var e=AgentCredential.CreateEncrypted(
            c.ProviderId,
            c.Name,
            protector.Protect(c.Username),
            protector.Protect(c.Password),
            string.IsNullOrWhiteSpace(c.AdditionalSecretsJson)?null:protector.Protect(c.AdditionalSecretsJson),
            c.ActorId);

        await repo.AddAsync(e,ct);
        await uow.SaveChangesAsync(ct);
        return Result.Success(e.Id);
    }
}

public sealed record UpdateAgentCredentialCommand(Guid Id,string Name,string Username,string? Password,string? AdditionalSecretsJson,Guid? ActorId):ICommand<Result>;
public sealed class UpdateAgentCredentialCommandHandler(
    IAgentCredentialRepository repo,
    ICredentialProtector protector,
    IUnitOfWork uow):ICommandHandler<UpdateAgentCredentialCommand,Result>
{
    public async Task<Result> HandleAsync(UpdateAgentCredentialCommand c,CancellationToken ct=default)
    {
        var e=await repo.GetByIdAsync(c.Id,ct);
        if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.CredentialNotFound);

        string passwordEncrypted;
        if(!string.IsNullOrWhiteSpace(c.Password))
            passwordEncrypted=protector.Protect(c.Password);
        else if(!string.IsNullOrWhiteSpace(e.PasswordEncrypted))
            passwordEncrypted=e.PasswordEncrypted;
        else
            return Result.Failure(AgentErrors.CredentialPasswordRequired);

        e.UpdateEncrypted(
            c.Name,
            protector.Protect(c.Username),
            passwordEncrypted,
            string.IsNullOrWhiteSpace(c.AdditionalSecretsJson)?null:protector.Protect(c.AdditionalSecretsJson),
            c.ActorId);

        await uow.SaveChangesAsync(ct);
        return Result.Success();
    }
}

public sealed record SetAgentCredentialActiveCommand(Guid Id,bool IsActive,Guid? ActorId):ICommand<Result>;
public sealed class SetAgentCredentialActiveCommandHandler(IAgentCredentialRepository repo,IUnitOfWork uow):ICommandHandler<SetAgentCredentialActiveCommand,Result>
{
    public async Task<Result> HandleAsync(SetAgentCredentialActiveCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.CredentialNotFound);e.SetActive(c.IsActive,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}

public sealed record VerifyAgentCredentialCommand(Guid Id):ICommand<Result>;
public sealed class VerifyAgentCredentialCommandHandler(
    IAgentCredentialRepository repo,
    ICredentialProtector protector,
    ISecretProvider legacySecrets):ICommandHandler<VerifyAgentCredentialCommand,Result>
{
    public async Task<Result> HandleAsync(VerifyAgentCredentialCommand c,CancellationToken ct=default)
    {
        var e=await repo.GetByIdAsync(c.Id,ct);
        if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.CredentialNotFound);

        try
        {
            if(e.HasEncryptedSecrets)
            {
                var username=protector.Unprotect(e.UsernameEncrypted!);
                var password=protector.Unprotect(e.PasswordEncrypted!);
                return string.IsNullOrWhiteSpace(username)||string.IsNullOrWhiteSpace(password)
                    ? Result.Failure(AgentErrors.CredentialVerificationFailed)
                    : Result.Success();
            }

            if(string.IsNullOrWhiteSpace(e.UsernameSecretKey)||string.IsNullOrWhiteSpace(e.PasswordSecretKey))
                return Result.Failure(AgentErrors.CredentialVerificationFailed);

            var username=await legacySecrets.GetSecretAsync(e.UsernameSecretKey,ct);
            var password=await legacySecrets.GetSecretAsync(e.PasswordSecretKey,ct);
            return string.IsNullOrWhiteSpace(username)||string.IsNullOrWhiteSpace(password)
                ? Result.Failure(AgentErrors.CredentialVerificationFailed)
                : Result.Success();
        }
        catch
        {
            return Result.Failure(AgentErrors.CredentialVerificationFailed);
        }
    }
}

public sealed record CreateBrowserProfileCommand(Guid ProviderId,Guid CredentialId,string Name,string ProfileKey,string StoragePath,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateBrowserProfileCommandHandler(IBrowserProfileRepository repo,IAgentProviderRepository providers,IAgentCredentialRepository credentials,IUnitOfWork uow):ICommandHandler<CreateBrowserProfileCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateBrowserProfileCommand c,CancellationToken ct=default){var p=await providers.GetByIdAsync(c.ProviderId,ct);if(p is null||p.IsDeleted)return Result.Failure<Guid>(AgentErrors.ProviderNotFound);var cr=await credentials.GetByIdAsync(c.CredentialId,ct);if(cr is null||cr.IsDeleted)return Result.Failure<Guid>(AgentErrors.CredentialNotFound);var e=BrowserProfile.Create(c.ProviderId,c.CredentialId,c.Name,c.ProfileKey,c.StoragePath,c.ActorId);await repo.AddAsync(e,ct);await uow.SaveChangesAsync(ct);return Result.Success(e.Id);}
}
public sealed record AuthenticateBrowserProfileCommand(Guid Id,Guid? ActorId):ICommand<Result>;
public sealed class AuthenticateBrowserProfileCommandHandler(IBrowserProfileRepository repo,IUnitOfWork uow):ICommandHandler<AuthenticateBrowserProfileCommand,Result>
{
    public async Task<Result> HandleAsync(AuthenticateBrowserProfileCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.BrowserProfileNotFound);e.SetStatus(BrowserProfileStatus.Authenticating,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}

public sealed record CreateAgentScheduleCommand(string Name,Guid AgentDefinitionId,Guid ProviderId,Guid? CredentialId,AgentScheduleType ScheduleType,string? CronExpression,int? IntervalMinutes,DateTime? ExecuteAt,string Timezone,string InputJson,int MaxRetries,int TimeoutSeconds,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateAgentScheduleCommandHandler(IAgentScheduleRepository repo,IAgentDefinitionRepository defs,IAgentProviderRepository providers,IUnitOfWork uow):ICommandHandler<CreateAgentScheduleCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateAgentScheduleCommand c,CancellationToken ct=default){var d=await defs.GetByIdAsync(c.AgentDefinitionId,ct);if(d is null||d.IsDeleted)return Result.Failure<Guid>(AgentErrors.DefinitionNotFound);var p=await providers.GetByIdAsync(c.ProviderId,ct);if(p is null||p.IsDeleted)return Result.Failure<Guid>(AgentErrors.ProviderNotFound);var e=AgentSchedule.Create(c.Name,c.AgentDefinitionId,c.ProviderId,c.CredentialId,c.ScheduleType,c.CronExpression,c.IntervalMinutes,c.ExecuteAt,c.Timezone,c.InputJson,c.MaxRetries,c.TimeoutSeconds,c.ActorId);if(c.ScheduleType==AgentScheduleType.Once)e.SetNextExecution(c.ExecuteAt,c.ActorId);await repo.AddAsync(e,ct);await uow.SaveChangesAsync(ct);return Result.Success(e.Id);}
}
public sealed record UpdateAgentScheduleCommand(Guid Id,string Name,Guid? CredentialId,AgentScheduleType ScheduleType,string? CronExpression,int? IntervalMinutes,DateTime? ExecuteAt,string Timezone,string InputJson,int MaxRetries,int TimeoutSeconds,DateTime? NextExecutionAt,Guid? ActorId):ICommand<Result>;
public sealed class UpdateAgentScheduleCommandHandler(IAgentScheduleRepository repo,IUnitOfWork uow):ICommandHandler<UpdateAgentScheduleCommand,Result>
{
    public async Task<Result> HandleAsync(UpdateAgentScheduleCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.ScheduleNotFound);e.Update(c.Name,c.CredentialId,c.ScheduleType,c.CronExpression,c.IntervalMinutes,c.ExecuteAt,c.Timezone,c.InputJson,c.MaxRetries,c.TimeoutSeconds,c.NextExecutionAt,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}
public sealed record SetAgentScheduleActiveCommand(Guid Id,bool IsActive,Guid? ActorId):ICommand<Result>;
public sealed class SetAgentScheduleActiveCommandHandler(IAgentScheduleRepository repo,IUnitOfWork uow):ICommandHandler<SetAgentScheduleActiveCommand,Result>
{
    public async Task<Result> HandleAsync(SetAgentScheduleActiveCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null||e.IsDeleted)return Result.Failure(AgentErrors.ScheduleNotFound);e.SetActive(c.IsActive,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}
public sealed record RunAgentScheduleCommand(Guid Id,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class RunAgentScheduleCommandHandler(IAgentScheduleRepository schedules,IAgentExecutionRepository executions,IUnitOfWork uow):ICommandHandler<RunAgentScheduleCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(RunAgentScheduleCommand c,CancellationToken ct=default){var s=await schedules.GetByIdAsync(c.Id,ct);if(s is null||s.IsDeleted)return Result.Failure<Guid>(AgentErrors.ScheduleNotFound);var e=AgentExecution.Create(s.AgentDefinitionId,s.ProviderId,s.Id,s.CredentialId,AgentExecutionType.Scheduled,0,s.InputJson,s.MaxRetries+1,Guid.NewGuid().ToString("N"),createdBy:c.ActorId);e.Queue(c.ActorId);await executions.AddAsync(e,ct);await uow.SaveChangesAsync(ct);return Result.Success(e.Id);}
}

public sealed record CreateAgentExecutionCommand(Guid AgentDefinitionId,Guid ProviderId,Guid? CredentialId,int Priority,string InputJson,int MaxAttempts,string? CorrelationId,string? TraceId,Guid? ActorId):ICommand<Result<Guid>>;
public sealed class CreateAgentExecutionCommandHandler(IAgentExecutionRepository repo,IAgentDefinitionRepository defs,IAgentProviderRepository providers,IUnitOfWork uow):ICommandHandler<CreateAgentExecutionCommand,Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateAgentExecutionCommand c,CancellationToken ct=default){var d=await defs.GetByIdAsync(c.AgentDefinitionId,ct);if(d is null||d.IsDeleted)return Result.Failure<Guid>(AgentErrors.DefinitionNotFound);var p=await providers.GetByIdAsync(c.ProviderId,ct);if(p is null||p.IsDeleted)return Result.Failure<Guid>(AgentErrors.ProviderNotFound);var e=AgentExecution.Create(c.AgentDefinitionId,c.ProviderId,null,c.CredentialId,AgentExecutionType.Manual,c.Priority,c.InputJson,c.MaxAttempts,string.IsNullOrWhiteSpace(c.CorrelationId)?Guid.NewGuid().ToString("N"):c.CorrelationId,c.TraceId,c.ActorId);e.Queue(c.ActorId);await repo.AddAsync(e,ct);await uow.SaveChangesAsync(ct);return Result.Success(e.Id);}
}
public sealed record CancelAgentExecutionCommand(Guid Id,Guid? ActorId):ICommand<Result>;
public sealed class CancelAgentExecutionCommandHandler(IAgentExecutionRepository repo,IUnitOfWork uow):ICommandHandler<CancelAgentExecutionCommand,Result>
{
    public async Task<Result> HandleAsync(CancelAgentExecutionCommand c,CancellationToken ct=default){var e=await repo.GetByIdAsync(c.Id,ct);if(e is null)return Result.Failure(AgentErrors.ExecutionNotFound);e.Cancel(DateTime.UtcNow,c.ActorId);await uow.SaveChangesAsync(ct);return Result.Success();}
}
