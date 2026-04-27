using System.IO;
using System.Windows;
using FEASolver.Core.Services;
using FEASolver.Services;
using FEASolver.Views;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace FEASolver;

public partial class App : Application
{
    public static IConfiguration Configuration { get; private set; } = null!;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();

        var config = new ConfigService();

        // Ensure workspace log dir exists before Serilog tries to write
        Directory.CreateDirectory(Path.Combine(config.Paths.WorkspaceRoot, "logs"));

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Debug()
            .WriteTo.File(
                Path.Combine(config.Paths.WorkspaceRoot, "logs", "feasolver_.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        Log.Information("FEA Solver starting up");

        // ── Startup validation ──────────────────────────────────────────────
        var validator = new ToolValidator(config);
        var report = await validator.ValidateAllAsync();

        if (!report.IsValid)
        {
            Log.Warning("Startup validation failed: {Errors}", report.FormatErrors());

            var result = MessageBox.Show(
                $"Some required tools are missing or misconfigured:\n\n{report.FormatErrors()}\n\n" +
                "Open Configuration dialog to set paths?",
                "Setup Required",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                var dlg = new ConfigDialog();
                dlg.ShowDialog();
            }
        }
        else if (report.HasWarnings)
        {
            Log.Warning("Startup warnings: {Warnings}", report.FormatWarnings());
        }

        var mainWindow = new MainWindow();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
