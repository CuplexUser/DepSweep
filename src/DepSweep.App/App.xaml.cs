using System.Windows;
using System.Windows.Threading;
using DepSweep.App.Services;
using DepSweep.App.ViewModels;
using DepSweep.Core.Config;
using DepSweep.Core.Processes;
using DepSweep.Core.Reporting;
using DepSweep.Core.Sweep;
using Microsoft.Extensions.DependencyInjection;

namespace DepSweep.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;

        _services = new ServiceCollection()
            .AddSingleton<ConfigStore>(_ => new ConfigStore())
            .AddSingleton<ReportStore>(_ => new ReportStore())
            .AddSingleton<IProcessRunner, ProcessRunner>()
            .AddSingleton<SweepService>(sp => new SweepService(sp.GetRequiredService<IProcessRunner>()))
            .AddSingleton<IDialogService, DialogService>()
            .AddSingleton<MainViewModel>()
            .AddSingleton<MainWindow>()
            .BuildServiceProvider();

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
        window.Show();
#if DEBUG
        DebugSnapshots.StartIfRequested(window);
#endif
        await _services.GetRequiredService<MainViewModel>().InitializeAsync();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }

    private static void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "DepSweep — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
