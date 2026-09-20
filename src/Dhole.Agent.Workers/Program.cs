using Dhole.Agent.Infrastructure.DependencyInjection;
using Dhole.Agent.Workers.DependencyInjection;
using Dhole.Agent.Application.DependencyInjection;
using Dhole.Agent.Persistence.DependencyInjection;
using Dhole.Agent.Persistence.Seeding;

var contentRoot = Path.Combine(Directory.GetCurrentDirectory(), "src", "Dhole.Agent.Workers");
if (!Directory.Exists(contentRoot))
{
    contentRoot = Directory.GetCurrentDirectory();
}

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = contentRoot
});

builder.Configuration.Sources.Clear();
builder.Configuration
    .SetBasePath(contentRoot)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

builder.Services.AddApplication();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddAgentWorker(builder.Configuration);

var host = builder.Build();
await host.Services.SeedAgentDataAsync();
await host.RunAsync();
