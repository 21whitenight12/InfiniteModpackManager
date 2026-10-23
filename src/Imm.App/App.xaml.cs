using System.IO;
using System.Windows;
using System.Windows.Threading;
using Imm.App.Services;
using Imm.Core.Ini;
using Imm.Core.IO;
using Imm.Core.Launch;
using Imm.Core.Library;
using Imm.Core.Logging;
using Imm.Core.Scanning;
using Imm.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Appearance;
using Wpf.Ui.Extensions;

namespace Imm.App;

/// <summary>
/// Composition root (IMM.md Infrastructure Rules): Generic Host, services are singletons,
/// ViewModels are transient. Global exception handlers log and surface a dialog — never a silent fall.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host = BuildHost();
    private volatile bool _startupCompleted;

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static IHost BuildHost()
    {
        var paths = ImmPaths.Resolve();
        var uiSink = new UiLogSink();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Sink(uiSink)
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "imm-.log"),
                rollingInterval: RollingInterval.Day,
                rollOnFileSizeLimit: true,
                fileSizeLimitBytes: 20_000_000,
                retainedFileCountLimit: 10,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);
        builder.Services.AddSingleton(Log.Logger);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(uiSink);
        builder.Services.AddSingleton<IRegistryService, WindowsRegistryService>();
        builder.Services.AddSingleton<SystemGameLocator>();
        builder.Services.AddSingleton<IModpackScanner, ModpackScanner>();
        builder.Services.AddSingleton<ILibraryService, LibraryService>();
        builder.Services.AddSingleton<ModOrganizerIniPatcher>();
        builder.Services.AddSingleton<ILaunchStrategy, DirectLaunchStrategy>();
        builder.Services.AddSingleton<ILaunchStrategy, Mo2LaunchStrategy>();
        builder.Services.AddSingleton<ILaunchService, LaunchService>();
        builder.Services.AddSingleton<LogViewModel>();
        builder.Services.AddSingleton<ILogWindowService, LogWindowService>();
        builder.Services.AddSingleton<IContentDialogService, ContentDialogService>();
        builder.Services.AddSingleton<ISnackbarService, SnackbarService>();
        builder.Services.AddTransient<MainViewModel>();
        builder.Services.AddSingleton<MainWindow>();

        return builder.Build();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Log.Information("Application starting. Portable={Portable}, Library={Library}",
            _host.Services.GetRequiredService<ImmPaths>().Portable,
            _host.Services.GetRequiredService<ImmPaths>().LibraryFilePath);

        // Canon accent must win over the Windows system accent: ThemesDictionary regenerates
        // WPF-UI accent brushes (green on this machine), which static XAML overrides can't beat.
        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x2E, 0x8B, 0xFF),
            ApplicationTheme.Dark,
            systemGlassColor: false);

        _host.Start();
        var mainWindow = _host.Services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow;
        mainWindow.Show();
        _startupCompleted = true;
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        Log.Information("Application exiting with code {ExitCode}.", e.ApplicationExitCode);
        await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        _host.Dispose();
        await Log.CloseAndFlushAsync().ConfigureAwait(true);
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        Log.Fatal(args.Exception, "Unhandled UI-thread exception.");
        ShowFatalDialog("Ошибка интерфейса", args.Exception);
        args.Handled = true;

        // Anti-zombie: fatal before startup completes → an invisible process is pointless.
        if (_startupCompleted is false)
        {
            Log.Fatal("Startup was not completed — shutting down with code 1.");
            Shutdown(1);
        }
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", args.IsTerminating);
            ShowFatalDialog("Критическая ошибка", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unobserved task exception.");
        args.SetObserved();
    }

    private void ShowFatalDialog(string title, Exception exception)
    {
        var message = $"{exception.Message}\n\nПодробности — в логе.";
        try
        {
            var mainWindow = _host.Services.GetService<MainWindow>();
            if (mainWindow is null)
            {
                System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            mainWindow.Dispatcher.Invoke(() =>
            {
                var dialogService = _host.Services.GetRequiredService<IContentDialogService>();
                _ = dialogService.ShowSimpleDialogAsync(new SimpleContentDialogCreateOptions
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "Закрыть"
                });
            });
        }
        catch (Exception dialogFailure)
        {
            Log.Error(dialogFailure, "Fatal dialog itself failed; falling back to MessageBox.");
            System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
