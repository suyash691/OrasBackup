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

    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editSource = "";
    [ObservableProperty] private string _editRegistry = "";
    [ObservableProperty] private string _editAuthToken = "";
    [ObservableProperty] private bool _editEncryption = true;
    [ObservableProperty] private int _editMaxBackups = 50;
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private bool _isEditing;

    public ObservableCollection<string> Profiles { get; } = [];

    public ProfileManagerViewModel(IServiceFactory svc, LogService log)
    {
        _svc = svc;
        _log = log;
        RefreshProfiles();
    }

    partial void OnSelectedProfileChanged(string? value)
    {
        if (value == null) { IsEditing = false; return; }
        try
        {
            var profile = _svc.CreateProfileStore().Load(value);
            EditName = profile.Name;
            EditSource = string.Join(", ", profile.SourcePaths);
            EditRegistry = profile.Registry;
            EditAuthToken = profile.AuthToken ?? "";
            EditEncryption = profile.Encryption.Enabled;
            EditMaxBackups = profile.Retention.MaxBackups;
            IsEditing = true;
        }
        catch { IsEditing = false; }
    }

    [RelayCommand]
    private void CreateProfile()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditRegistry)) return;
        SaveCurrentProfile();
        _log.Log($"Profile '{EditName}' created");
        ClearForm();
        RefreshProfiles();
    }

    [RelayCommand]
    private void SaveProfile()
    {
        if (string.IsNullOrWhiteSpace(EditName) || string.IsNullOrWhiteSpace(EditRegistry)) return;
        SaveCurrentProfile();
        _log.Log($"Profile '{EditName}' saved");
        RefreshProfiles();
    }

    [RelayCommand]
    private void DeleteProfile()
    {
        if (SelectedProfile is null) return;
        var store = _svc.CreateProfileStore();
        var path = store.GetProfilePath(SelectedProfile);
        if (File.Exists(path))
        {
            _log.Log($"Deleting profile '{SelectedProfile}' at {path}");
            File.Delete(path);
        }
        var cache = _svc.CreateBackupIndexCache();
        try { cache.Delete(SelectedProfile); } catch { }
        _log.Log($"Profile '{SelectedProfile}' deleted");
        ClearForm();
        RefreshProfiles();
    }

    [RelayCommand]
    private void NewProfile()
    {
        SelectedProfile = null;
        ClearForm();
        IsEditing = false;
    }

    private void SaveCurrentProfile()
    {
        var profile = new BackupProfile
        {
            Name = EditName,
            SourcePaths = EditSource.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
            Registry = EditRegistry,
            AuthToken = string.IsNullOrWhiteSpace(EditAuthToken) ? null : EditAuthToken,
            Encryption = new EncryptionConfig { Enabled = EditEncryption },
            Retention = new RetentionConfig { MaxBackups = EditMaxBackups }
        };
        _svc.CreateProfileStore().Save(profile);
    }

    private void ClearForm()
    {
        EditName = ""; EditSource = ""; EditRegistry = ""; EditAuthToken = "";
        EditEncryption = true; EditMaxBackups = 50;
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        foreach (var name in _svc.CreateProfileStore().ListProfiles())
            Profiles.Add(name);
    }
}
