namespace Dhole.Agent.Api.Authorization;

internal static class ScopeAuthorizationExtensions
{
    private const string ScopePolicyPrefix = "Scope:";

    public static TBuilder RequireScope<TBuilder>(this TBuilder builder,string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);
        builder.RequireAuthorization($"{ScopePolicyPrefix}{scope.Trim()}");
        return builder;
    }
}
