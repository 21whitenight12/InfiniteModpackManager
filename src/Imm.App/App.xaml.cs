using System.IO;
using System.Windows;
using System.Windows.Threading;
using Imm.Core.IO;
using Imm.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Wpf.Ui;
using Wpf.Ui.Extensions;
using Wpf.Ui.Abstractions;

namespace Imm.App;

/// <summary>
/// Composition root (IMM.md Infrastructure Rules): Generic Host, services are singletons,
/// ViewModels are transient. Global exception handlers log and surface a dialog — never a silent fall.
/// </summary>
public partial class App : Application
{
    private readonly IHost _host = BuildHost();

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    private static IHost BuildHost()
    {
        var paths = ImmPaths.Resolve();
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsDirectory, "imm-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 10,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(dispose: true);

        builder.Services.AddSingleton(paths);
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

        _host.Start();
        _host.Services.GetRequiredService<MainWindow>().Show();
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
        ShowFatalDialog("UI error", args.Exception);
        args.Handled = true;
    }

    private void OnAppDomainUnhandledException(object? sender, UnhandledExceptionEventArgs args)
    {
        if (args.ExceptionObject is Exception exception)
        {
            Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", args.IsTerminating);
            ShowFatalDialog("Fatal error", exception);
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs args)
    {
        Log.Error(args.Exception, "Unobserved task exception.");
        args.SetObserved();
    }

    private void ShowFatalDialog(string title, Exception exception)
    {
        var message = $"{exception.Message}\n\nDetails are in the log file.";
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
                    CloseButtonText = "Close"
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
