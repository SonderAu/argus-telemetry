using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using TelemetryWorkerService;

var appDir = AppContext.BaseDirectory;
var logDir = Path.Combine(appDir, "Logs");

// Ensure Logs folder exists
Directory.CreateDirectory(logDir);

// Use this in Serilog
var logPath = Path.Combine(logDir, "telemetry.log");

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "telemetry.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();


try
{
    Log.Information("Starting host...");
    Host.CreateDefaultBuilder(args)
        .UseWindowsService()
        .UseSerilog()
        .ConfigureServices((hostContext, services) =>
        {
            services.AddHostedService<Worker>();
        })
        .Build()
        .Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Host terminated unexpectedly.");
}
finally
{
    Log.CloseAndFlush();
}
