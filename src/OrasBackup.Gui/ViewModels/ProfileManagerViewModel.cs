using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrasBackup.Cli;
using OrasBackup.Core.Config;
using OrasBackup.Gui.Services;

namespace OrasBackup.Gui.ViewModels;

public partial class ProfileManagerViewModel : ObservableObject
{
    private readonly IServiceFactory _svc;
    private readonly LogService _log;

    [ObservableProperty] private string _newName = "";
    [ObservableProperty] private string _newSource = "";
    [ObservableProperty] private string _newRegistry = "";
    [ObservableProperty] private string? _selectedProfile;

    public ObservableCollection<string> Profiles { get; } = [];

    public ProfileManagerViewModel(IServiceFactory svc, LogService log)
    {
        _svc = svc;
        _log = log;
        RefreshProfiles();
    }

    [RelayCommand]
    private void CreateProfile()
    {
        if (string.IsNullOrWhiteSpace(NewName) || string.IsNullOrWhiteSpace(NewRegistry)) return;
        var profile = new BackupProfile
        {
            Name = NewName,
            SourcePaths = NewSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Registry = NewRegistry
        };
        _svc.CreateProfileStore().Save(profile);
        _log.Log($"Profile '{NewName}' created");
        NewName = ""; NewSource = ""; NewRegistry = "";
        RefreshProfiles();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var store = _svc.CreateProfileStore();
        var path = store.GetProfilePath(SelectedProfile);
        if (File.Exists(path)) File.Delete(path);
        _log.Log($"Profile '{SelectedProfile}' deleted");
        RefreshProfiles();
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var name in _svc.CreateProfileStore().ListProfiles())
            Profiles.Add(name);
    }
}
