using System.IO;
using WindowsClientCenter.Host.Runtime;
using WindowsClientCenter.Host.ViewModels;
using WindowsClientCenter.Intune.Services.Runtime;
using WindowsClientCenter.Plugin.Abstractions.Contracts;
using WindowsClientCenter.Plugin.Host;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace WindowsClientCenter.Host;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private IHostStatusLogSink? _hostStatusLogSink;
    private ILogger<App>? _logger;
    private CancellationTokenSource? _screenshotCaptureCancellationTokenSource;
    private int _unhandledExceptionDialogOpen;

    private static void WriteStartupFailure(Exception exception)
    {
        try
        {
            var logDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            var logPath = Path.Combine(logDirectory, "startup-failure.log");
            File.AppendAllText(
                logPath,
                $"{DateTimeOffset.Now:O} {exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Best effort only.
        }
    }

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        var startupArguments = HostStartupArgumentParser.Parse(e.Args);
        try
        {
            base.OnStartup(e);

            var configuration = BuildConfiguration(startupArguments);
            var runtimeOptions = new HostRuntimeOptions();
            configuration.GetSection("Runtime").Bind(runtimeOptions);

            var pluginOptions = new HostPluginOptions();
            configuration.GetSection("Plugins").Bind(pluginOptions);

            var explorerOptions = new HostExplorerOptions();
            configuration.GetSection("Explorer").Bind(explorerOptions);

            var intuneOptions = new IntuneRuntimeOptions();
            configuration.GetSection("Intune").Bind(intuneOptions);
            if (!Enum.TryParse(configuration["Intune:Mode"], ignoreCase: true, out IntuneRuntimeMode configuredMode))
            {
                configuredMode = IntuneRuntimeMode.Mock;
            }

            intuneOptions.Mode = configuredMode;
            if (double.TryParse(configuration["Defender:SecurityIntelligenceWarningThresholdHours"], out var defenderWarningThreshold) &&
                defenderWarningThreshold > 0)
            {
                intuneOptions.DefenderSecurityIntelligenceWarningThresholdHours = defenderWarningThreshold;
            }

            if (double.TryParse(configuration["Defender:SecurityIntelligenceCriticalThresholdHours"], out var defenderCriticalThreshold) &&
                defenderCriticalThreshold > 0)
            {
                intuneOptions.DefenderSecurityIntelligenceCriticalThresholdHours = defenderCriticalThreshold;
            }

            var logger = new LoggerConfiguration()
                .MinimumLevel.Debug()
                .WriteTo.File(
                    Path.Combine(AppContext.BaseDirectory, "logs", "icc-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 30)
                .CreateLogger();

            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.AddSingleton(runtimeOptions);
            services.AddSingleton(pluginOptions);
            services.AddSingleton(explorerOptions);
            services.AddSingleton(intuneOptions);
            services.AddLogging(builder => builder.AddSerilog(logger, dispose: true));
            services.AddIntuneRuntime(intuneOptions);

            services.AddSingleton<IPluginLifecycle, PluginLifecycle>();
            services.AddSingleton<IPluginLoader, PluginLoader>();
            services.AddSingleton<IPluginRegistry, PluginRegistry>();
            if (startupArguments.ScreenshotCapture is null)
            {
                services.AddSingleton<IHostUserSettingsStore, JsonHostUserSettingsStore>();
            }
            else
            {
                services.AddSingleton<IHostUserSettingsStore, NullHostUserSettingsStore>();
            }

            services.AddSingleton<HostStatusLogDispatcher>();

            services.AddSingleton<MainWindowViewModel>();
            services.AddSingleton<IHostStatusLogSink>(sp => sp.GetRequiredService<HostStatusLogDispatcher>());
            services.AddSingleton<IHostBusyStateSink>(sp => sp.GetRequiredService<MainWindowViewModel>());
            services.AddSingleton<IHostRibbonRefreshSink>(sp => sp.GetRequiredService<MainWindowViewModel>());
            services.AddTransient<MainWindow>();

            _serviceProvider = services.BuildServiceProvider();
            _hostStatusLogSink = _serviceProvider.GetRequiredService<IHostStatusLogSink>();
            _logger = _serviceProvider.GetRequiredService<ILogger<App>>();
            RegisterGlobalExceptionHandlers();

            var mainWindowViewModel = _serviceProvider.GetRequiredService<MainWindowViewModel>();
            mainWindowViewModel.SetStartupHost(startupArguments.StartupHost);

            var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = mainWindow;
            if (startupArguments.ScreenshotCapture is not null)
            {
                Properties["ScreenshotCaptureMode"] = true;
            }

            mainWindow.Show();

            if (startupArguments.ScreenshotCapture is not null)
            {
                StartScreenshotCapture(mainWindow, startupArguments.ScreenshotCapture);
            }
        }
        catch (Exception ex)
        {
            WriteStartupFailure(ex);
            if (startupArguments.ScreenshotCapture is null)
            {
                System.Windows.MessageBox.Show(
                    ex.Message,
                    "Startup Failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }

            Shutdown(-1);
        }
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        DispatcherUnhandledException -= OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException -= OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        _screenshotCaptureCancellationTokenSource?.Cancel();
        _screenshotCaptureCancellationTokenSource?.Dispose();
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private void RegisterGlobalExceptionHandlers()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnCurrentDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        ReportUnhandledException("UI", e.Exception);
        e.Handled = true;
        ShowUnhandledException("UI", e.Exception);
    }

    private void OnCurrentDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            ReportUnhandledException("AppDomain", ex);
            ShowUnhandledException("AppDomain", ex);
            return;
        }

        _hostStatusLogSink?.Append($"[Unhandled][AppDomain] Non-exception fault: {e.ExceptionObject}");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ReportUnhandledException("Task", e.Exception);
        ShowUnhandledException("Task", e.Exception);
        e.SetObserved();
    }

    private void ReportUnhandledException(string source, Exception exception)
    {
        _hostStatusLogSink?.Append($"[Unhandled][{source}] {exception.GetType().Name}: {exception.Message}");
        _logger?.LogError(exception, "Unhandled {Source} exception.", source);
        WriteStartupFailure(exception);
    }

    private void ShowUnhandledException(string source, Exception exception)
    {
        if (Interlocked.Exchange(ref _unhandledExceptionDialogOpen, 1) != 0)
        {
            return;
        }

        try
        {
            var message =
                $"Unhandled {source} exception:{Environment.NewLine}{exception.GetType().FullName}: {exception.Message}" +
                $"{Environment.NewLine}{Environment.NewLine}Details were written to the log file in the application 'logs' folder.";

            System.Windows.MessageBox.Show(
                message,
                "Application Error",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Error);
        }
        catch
        {
            // Best effort only.
        }
        finally
        {
            Interlocked.Exchange(ref _unhandledExceptionDialogOpen, 0);
        }
    }

    private void StartScreenshotCapture(MainWindow mainWindow, ScreenshotCaptureOptions options)
    {
        if (_serviceProvider is null)
        {
            throw new InvalidOperationException("Service provider is not available.");
        }

        var runner = new PublicScreenshotCaptureRunner(
            mainWindow,
            _serviceProvider.GetRequiredService<MainWindowViewModel>(),
            _serviceProvider.GetRequiredService<IHostStatusLogSink>(),
            _serviceProvider.GetRequiredService<ILogger<PublicScreenshotCaptureRunner>>());
        _screenshotCaptureCancellationTokenSource = new CancellationTokenSource();
        _ = Task.Run(() => RunScreenshotCaptureAsync(runner, options));
        return;

        async Task RunScreenshotCaptureAsync(PublicScreenshotCaptureRunner captureRunner, ScreenshotCaptureOptions captureOptions)
        {
            try
            {
                await captureRunner.RunAsync(captureOptions, _screenshotCaptureCancellationTokenSource.Token);
                _hostStatusLogSink?.Append($"Screenshot export completed in '{captureOptions.OutputDirectory}'.");
                await mainWindow.Dispatcher.InvokeAsync(() => Shutdown(0));
            }
            catch (OperationCanceledException)
            {
                await mainWindow.Dispatcher.InvokeAsync(() => Shutdown(-1));
            }
            catch (Exception ex)
            {
                WriteStartupFailure(ex);
                _hostStatusLogSink?.Append($"Screenshot export failed: {ex.Message}");
                _logger?.LogError(ex, "Screenshot export failed.");
                await mainWindow.Dispatcher.InvokeAsync(() => Shutdown(-1));
            }
        }
    }

    private static IConfiguration BuildConfiguration(HostStartupArguments startupArguments)
    {
        var envFromVariable = Environment.GetEnvironmentVariable("ICC_ENV");
        var overrides = new Dictionary<string, string?>
        {
            ["Runtime:Environment"] = string.IsNullOrWhiteSpace(envFromVariable) ? "dev" : envFromVariable
        };

        if (!string.IsNullOrWhiteSpace(startupArguments.IntuneModeOverride))
        {
            overrides["Intune:Mode"] = startupArguments.IntuneModeOverride;
        }

        if (startupArguments.ScreenshotCapture is not null)
        {
            overrides["Intune:Mode"] = startupArguments.ScreenshotCapture.IntuneMode;
        }

        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .AddJsonFile("appsettings.Local.json", optional: true)
            .AddEnvironmentVariables(prefix: "ICC_")
            .AddInMemoryCollection(overrides)
            .Build();
    }

    private static string ResolveRelativePath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return path;
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }
}
