using CustomCodeFramework.Core.Results;
using CustomCodeFramework.Cqrs.Commands;
using CustomCodeFramework.Cqrs.Queries;
using CustomCodeFramework.Persistence.Abstractions;
using Dhole.Agent.Application.Abstractions.Repositories;
using Dhole.Agent.Contracts.ExtractionProfiles;
using Dhole.Agent.Domain.Agents;

namespace Dhole.Agent.Application.ExtractionProfiles;

public sealed record GetEndpointCapturesQuery(Guid ProfileId) : IQuery<IReadOnlyCollection<AgentEndpointCaptureDto>>;
public sealed class GetEndpointCapturesQueryHandler(IAgentEndpointCaptureRepository captures)
    : IQueryHandler<GetEndpointCapturesQuery, IReadOnlyCollection<AgentEndpointCaptureDto>>
{
    public async Task<IReadOnlyCollection<AgentEndpointCaptureDto>> HandleAsync(GetEndpointCapturesQuery query, CancellationToken cancellationToken = default)
        => (await captures.GetByProfileAsync(query.ProfileId, cancellationToken)).Select(Map).ToArray();

    internal static AgentEndpointCaptureDto Map(AgentEndpointCapture x)
        => new(x.Id, x.ProfileId, x.Name, x.HttpMethod, x.UrlPattern, x.MatchType.ToString(), x.ContentType, x.CaptureRequest, x.CaptureResponse, x.IsRequired, x.TimeoutSeconds, x.IsActive, x.SortOrder);
}

public sealed record CreateEndpointCaptureCommand(Guid ProfileId, SaveAgentEndpointCaptureRequest Request, Guid? ActorId) : ICommand<Result<Guid>>;
public sealed class CreateEndpointCaptureCommandHandler(IAgentExtractionProfileRepository profiles, IAgentEndpointCaptureRepository captures, EndpointCaptureMatcher matcher, IUnitOfWork unitOfWork)
    : ICommandHandler<CreateEndpointCaptureCommand, Result<Guid>>
{
    public async Task<Result<Guid>> HandleAsync(CreateEndpointCaptureCommand command, CancellationToken cancellationToken = default)
    {
        var profile = await profiles.GetByIdAsync(command.ProfileId, cancellationToken);
        if (profile is null || profile.IsDeleted)
            return Result.Failure<Guid>(new Error("Agent.ExtractionProfileNotFound", "Extraction profile not found."));
        if (!Enum.TryParse<AgentEndpointMatchType>(command.Request.MatchType, true, out var matchType))
            return Result.Failure<Guid>(new Error("Agent.InvalidEndpointMatchType", "Endpoint match type is invalid."));
        try { matcher.ValidatePattern(matchType, command.Request.UrlPattern); }
        catch (Exception ex) { return Result.Failure<Guid>(new Error("Agent.InvalidEndpointPattern", ex.Message)); }

        var r = command.Request;
        var entity = AgentEndpointCapture.Create(command.ProfileId, r.Name, r.HttpMethod, r.UrlPattern, matchType, r.ContentType, r.CaptureRequest, r.CaptureResponse, r.IsRequired, r.TimeoutSeconds, r.IsActive, r.SortOrder, command.ActorId);
        await captures.AddAsync(entity, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success(entity.Id);
    }
}

public sealed record UpdateEndpointCaptureCommand(Guid ProfileId, Guid CaptureId, SaveAgentEndpointCaptureRequest Request, Guid? ActorId) : ICommand<Result>;
public sealed class UpdateEndpointCaptureCommandHandler(IAgentEndpointCaptureRepository captures, EndpointCaptureMatcher matcher, IUnitOfWork unitOfWork)
    : ICommandHandler<UpdateEndpointCaptureCommand, Result>
{
    public async Task<Result> HandleAsync(UpdateEndpointCaptureCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await captures.GetByIdAsync(command.CaptureId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.EndpointCaptureNotFound", "Endpoint capture rule not found."));
        if (!Enum.TryParse<AgentEndpointMatchType>(command.Request.MatchType, true, out var matchType))
            return Result.Failure(new Error("Agent.InvalidEndpointMatchType", "Endpoint match type is invalid."));
        try { matcher.ValidatePattern(matchType, command.Request.UrlPattern); }
        catch (Exception ex) { return Result.Failure(new Error("Agent.InvalidEndpointPattern", ex.Message)); }

        var r = command.Request;
        entity.Update(r.Name, r.HttpMethod, r.UrlPattern, matchType, r.ContentType, r.CaptureRequest, r.CaptureResponse, r.IsRequired, r.TimeoutSeconds, r.IsActive, r.SortOrder, command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record DeleteEndpointCaptureCommand(Guid ProfileId, Guid CaptureId, Guid? ActorId) : ICommand<Result>;
public sealed class DeleteEndpointCaptureCommandHandler(IAgentEndpointCaptureRepository captures, IUnitOfWork unitOfWork)
    : ICommandHandler<DeleteEndpointCaptureCommand, Result>
{
    public async Task<Result> HandleAsync(DeleteEndpointCaptureCommand command, CancellationToken cancellationToken = default)
    {
        var entity = await captures.GetByIdAsync(command.CaptureId, cancellationToken);
        if (entity is null || entity.IsDeleted || entity.ProfileId != command.ProfileId)
            return Result.Failure(new Error("Agent.EndpointCaptureNotFound", "Endpoint capture rule not found."));
        entity.Delete(command.ActorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}

public sealed record TestEndpointCaptureQuery(Guid ProfileId, Guid CaptureId, TestAgentEndpointCaptureRequest Request) : IQuery<Result<TestAgentEndpointCaptureResponse>>;
public sealed class TestEndpointCaptureQueryHandler(IAgentEndpointCaptureRepository captures, EndpointCaptureMatcher matcher)
    : IQueryHandler<TestEndpointCaptureQuery, Result<TestAgentEndpointCaptureResponse>>
{
    public async Task<Result<TestAgentEndpointCaptureResponse>> HandleAsync(TestEndpointCaptureQuery query, CancellationToken cancellationToken = default)
    {
        var rule = await captures.GetByIdAsync(query.CaptureId, cancellationToken);
        if (rule is null || rule.IsDeleted || rule.ProfileId != query.ProfileId)
            return Result.Failure<TestAgentEndpointCaptureResponse>(new Error("Agent.EndpointCaptureNotFound", "Endpoint capture rule not found."));
        var matches = matcher.IsMatch(rule, query.Request.HttpMethod, query.Request.Url, query.Request.ContentType);
        return Result.Success(new TestAgentEndpointCaptureResponse(matches, rule.Name, rule.MatchType.ToString(), rule.UrlPattern));
    }
}
