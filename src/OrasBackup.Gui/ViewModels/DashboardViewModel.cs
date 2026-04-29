using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrasBackup.Cli;
using OrasBackup.Core.Delta;
using OrasBackup.Gui.Services;

namespace OrasBackup.Gui.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IServiceFactory _svc;
    private readonly LogService _log;

    [ObservableProperty] private string _selectedProfile = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private string _lastBackupInfo = "No backups yet";
    [ObservableProperty] private bool _isRunning;

    public DashboardViewModel(IServiceFactory svc, LogService log)
    {
        _svc = svc;
        _log = log;
    }

    [RelayCommand]
    private async Task RunBackupAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile)) return;
        IsRunning = true;
        Status = "Backing up...";
        try
        {
            var store = _svc.CreateProfileStore();
            var profile = store.Load(SelectedProfile);
            var key = _svc.ResolveKey(Password, null, profile.Encryption);
            var cache = _svc.CreateManifestCache();
            var previous = cache.Load(SelectedProfile);
            var engine = _svc.CreateBackupEngine();

            var result = await engine.RunBackupAsync(profile, key, previous);
            if (result.Success)
            {
                cache.Save(SelectedProfile, engine.LastManifest!);
                LastBackupInfo = $"Backup {result.BackupId}: +{result.FilesAdded} ~{result.FilesModified} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)";
                Status = "Backup complete";
                _log.Log(LastBackupInfo);

                var retention = _svc.CreateRetentionEnforcer();
                await retention.EnforceAsync(profile.Registry, profile.Retention);
            }
            else
            {
                Status = $"Failed: {result.Error}";
                _log.Log($"Backup failed: {result.Error}");
            }
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _log.Log($"Error: {ex.Message}");
        }
        finally { IsRunning = false; }
    }
}
