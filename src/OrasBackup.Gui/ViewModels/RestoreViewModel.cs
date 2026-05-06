using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrasBackup.Cli;
using OrasBackup.Gui.Services;

namespace OrasBackup.Gui.ViewModels;

public partial class RestoreViewModel : ObservableObject
{
    private readonly IServiceFactory _svc;
    private readonly LogService _log;

    [ObservableProperty] private string _selectedProfile = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _targetDir = "";
    [ObservableProperty] private string? _selectedBackupId;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private bool _isRunning;

    public ObservableCollection<string> BackupIds { get; } = [];
    public ObservableCollection<string> Profiles { get; }

    public RestoreViewModel(IServiceFactory svc, LogService log, ObservableCollection<string>? profiles = null)
    {
        _svc = svc;
        _log = log;
        Profiles = profiles ?? [];
    }

    [RelayCommand]
    private async Task LoadBackupsAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile)) return;
        try
        {
            var profile = _svc.CreateProfileStore().Load(SelectedProfile);
            var tags = await _svc.CreateOrasClient(profile.AuthToken).ListTagsAsync(profile.Registry);
            BackupIds.Clear();
            BackupIds.Add("(latest)");
            foreach (var tag in tags.Where(t => t != "latest" && !t.StartsWith("chunk-")).OrderDescending())
                BackupIds.Add(tag);
        }
        catch (Exception ex) { _log.Log($"Failed to list backups: {ex.Message}"); }
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RestoreAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile) || string.IsNullOrWhiteSpace(TargetDir)) return;
        IsRunning = true;
        Status = "Restoring...";
        try
        {
            var profile = _svc.CreateProfileStore().Load(SelectedProfile);
            var key = _svc.ResolveKey(Password, null, profile.Encryption);
            var backupId = SelectedBackupId == "(latest)" ? null : SelectedBackupId;
            await _svc.CreateRestoreEngine(profile.AuthToken).RestoreAsync(
                profile.Registry, backupId, TargetDir, key, profile.Encryption.Enabled, ct);
            Status = $"Restored to {TargetDir}";
            _log.Log($"Restore complete to {TargetDir}");
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            _log.Log("Restore cancelled");
        }
        catch (Exception ex)
        {
            Status = $"Failed: {ex.Message}";
            _log.Log($"Restore failed: {ex.Message}");
        }
        finally { IsRunning = false; Password = ""; }
    }
}
