using CommunityToolkit.Mvvm.ComponentModel;
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
        var svc = new DefaultServiceFactory();
        var log = new LogService(action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
        Profiles = new ProfileManagerViewModel(svc, log);
        Dashboard = new DashboardViewModel(svc, log);
        Restore = new RestoreViewModel(svc, log);
        Log = new LogViewModel(log);
    }
}
