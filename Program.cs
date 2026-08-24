using BranchCleanupService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.Configure<CleanupOptions>(
    builder.Configuration.GetSection(CleanupOptions.SectionName));

builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "BranchCleanupService";
});

builder.Services.AddSerilog((services, loggerConfig) => loggerConfig
    .ReadFrom.Services(services)
    .WriteTo.File(
        path: "Logs/branch-cleanup-.log",
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 14));

builder.Services.AddSingleton<BranchCleaner>();
builder.Services.AddHostedService<CleanupWorker>();

var host = builder.Build();

host.Services.GetRequiredService<ILogger<Program>>()
    .LogInformation("Branch Cleanup Service starting");

await host.RunAsync();
