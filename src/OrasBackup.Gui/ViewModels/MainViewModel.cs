using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using OrasBackup.Cli;
using OrasBackup.Gui.Services;

namespace OrasBackup.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    public ProfileManagerViewModel Profiles { get; }
    public DashboardViewModel Dashboard { get; }
    public RestoreViewModel Restore { get; }
    public LogViewModel Log { get; }

    public MainViewModel()
    {
        var log = new LogService(action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(new LogServiceProvider(log)).SetMinimumLevel(LogLevel.Debug));
        var svc = new DefaultServiceFactory(loggerFactory);
        Profiles = new ProfileManagerViewModel(svc, log);
        Dashboard = new DashboardViewModel(svc, log, Profiles.Profiles);
        Restore = new RestoreViewModel(svc, log, Profiles.Profiles);
        Log = new LogViewModel(log);
    }
}
