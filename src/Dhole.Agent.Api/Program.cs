using CustomCodeFramework.Auth.Scopes;
using CustomCodeFramework.ServiceDefaults.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCustomCodeServiceDefaults(
    builder.Configuration,
    options =>
    {
        options.AddMongo = false;
        options.AddRedisStreams = false;
    }
);

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health");

app.MapGet("/api/v1/ping", () => Results.Ok(new { service = "Dhole.Agent", status = "ok" }))
    .WithName("Ping")
    .AllowAnonymous();

app.MapGet("/api/v1/secure-ping", () => Results.Ok(new { service = "Dhole.Agent", status = "authorized" }))
    .WithName("SecurePing")
    .RequireAuthorization(new RequireScopeAttribute("dhole.agent.view").Policy!);

app.Run();
