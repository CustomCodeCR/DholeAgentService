using Dhole.Agent.Api.Grpc;
using Dhole.Agent.Api.Endpoints;
using CustomCodeFramework.Api.DependencyInjection;
using CustomCodeFramework.Api.Swagger;
using CustomCodeFramework.Core.Abstractions;
using Dhole.Agent.Application.DependencyInjection;
using Dhole.Agent.Infrastructure.DependencyInjection;
using Dhole.Agent.Infrastructure.Time;
using Dhole.Agent.Persistence.DependencyInjection;
using Dhole.Agent.Persistence.Seeding;

var builder = WebApplication.CreateBuilder(args);
const string CorsPolicyName = "DholeWebCors";

builder.Services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
builder.Services.AddCustomCodeApiWithSwagger(title: "Dhole Agent Service", version: "v1");
builder.Services.AddCors(options => options.AddPolicy(
    CorsPolicyName,
    policy => policy
        .WithOrigins(
            "https://dhole.customcodecr.com",
            "https://sistema.logisticacastrofallas.com",
            "http://localhost:5173",
            "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));
builder.Services.AddGrpc();
builder.Services.AddApplication();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);

var app = builder.Build();

await app.Services.SeedAgentDataAsync();

app.UseCustomCodeApi();
app.UseCors(CorsPolicyName);
if (app.Environment.IsDevelopment())
{
    app.UseCustomCodeSwagger();
}

app.MapGet(
        "/health",
        () => Results.Ok(new
        {
            service = "DholeAgentService",
            status = "Healthy",
            timestamp = DateTimeOffset.UtcNow
        }))
    .AllowAnonymous();

app.UseAuthentication();
app.UseAuthorization();
app.UseWebSockets();
app.MapAgentEndpoints();
// Viewer proxy is independently secured by short-lived access tickets.
StandbyViewerGateway.Map(app);
app.MapGrpcService<AgentGrpcService>();

// Dhole API Manager: authenticated runtime inventory, not a public proxy.
app.MapGet("/api/internal/third-party-route-catalog", (HttpContext http) =>
{
    var allowed = http.User.Identity?.IsAuthenticated == true &&
        http.User.Claims.Where(c => c.Type is "scope" or "scp" or "scopes")
            .SelectMany(c => c.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Contains("integrations.thirdparty.manage", StringComparer.OrdinalIgnoreCase);
    if (!allowed) return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
    var routes = new List<object>();
    foreach (var source in ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources)
        foreach (var endpoint in source.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || endpoint.RoutePattern.RawText is not { } path) continue;
            foreach (var method in methods)
                if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
                    routes.Add(new { method, path = "/" + path.TrimStart('/') });
        }
    return (IResult)Results.Ok(routes);
}).ExcludeFromDescription();

app.MapGet("/api/internal/third-party-route-catalog", (HttpContext http) =>
{
    var allowed = http.User.Identity?.IsAuthenticated == true &&
        http.User.Claims.Where(c => c.Type is "scope" or "scp" or "scopes")
            .SelectMany(c => c.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Contains("integrations.thirdparty.manage", StringComparer.OrdinalIgnoreCase);
    if (!allowed) return (IResult)Results.StatusCode(StatusCodes.Status403Forbidden);
    var routes = new List<object>();
    foreach (var source in ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources)
        foreach (var endpoint in source.Endpoints.OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>())
        {
            var methods = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.HttpMethodMetadata>()?.HttpMethods;
            if (methods is null || endpoint.RoutePattern.RawText is not { } path) continue;
            foreach (var method in methods)
                if (method is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
                    routes.Add(new { method, path = "/" + path.TrimStart('/') });
        }
    return (IResult)Results.Ok(routes);
}).ExcludeFromDescription();


app.MapGet("/api/internal/third-party-route-catalog", (HttpContext ctx) =>
{
    if (ctx.User.Identity?.IsAuthenticated != true ||
        !ctx.User.Claims.Where(claim => claim.Type is "scope" or "scp" or "scopes")
            .SelectMany(claim => claim.Value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
            .Contains("integrations.thirdparty.manage", StringComparer.OrdinalIgnoreCase))
        return Results.Forbid();
    // Reflect the deployed route table, including MapGroup routes, without Swagger.
    var sources = ((Microsoft.AspNetCore.Routing.IEndpointRouteBuilder)app).DataSources;
    var routes = sources.SelectMany(source => source.Endpoints)
        .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.Routing.IHttpMethodMetadata>()?.HttpMethods
                ?? Array.Empty<string>())
            .Where(verb => verb is "GET" or "POST" or "PUT" or "PATCH" or "DELETE")
            .Select(verb => new { method = verb, path = "/" + endpoint.RoutePattern.RawText?.TrimStart('/') }))
        .Where(route => route.path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            && !route.path.StartsWith("/api/internal/", StringComparison.OrdinalIgnoreCase)
            && !route.path.Contains("/health", StringComparison.OrdinalIgnoreCase)
            && !route.path.Contains("/login", StringComparison.OrdinalIgnoreCase)
            && !route.path.Contains("/refresh", StringComparison.OrdinalIgnoreCase)
            && !route.path.Contains("/password", StringComparison.OrdinalIgnoreCase)
            && !route.path.Contains("/impersonation", StringComparison.OrdinalIgnoreCase))
        .Distinct().OrderBy(route => route.path).ThenBy(route => route.method)
        .Take(1500).ToArray();
    return Results.Ok(routes);
}).RequireAuthorization();

app.Run();
