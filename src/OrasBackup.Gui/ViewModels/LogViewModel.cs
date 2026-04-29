using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrasBackup.Gui.Services;

namespace OrasBackup.Gui.ViewModels;

public partial class LogViewModel : ObservableObject
{
    private readonly LogService _log;

    public ObservableCollection<string> Entries => _log.Entries;

    public LogViewModel(LogService log) => _log = log;

    [RelayCommand]
    private void Clear() => _log.Entries.Clear();
}
