using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OrasBackup.Cli;
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

    public ObservableCollection<string> Profiles { get; }

    public DashboardViewModel(IServiceFactory svc, LogService log, ObservableCollection<string>? profiles = null)
    {
        _svc = svc;
        _log = log;
        Profiles = profiles ?? [];
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task RunBackupAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(SelectedProfile)) return;
        IsRunning = true;
        Status = "Backing up...";
        try
        {
            var store = _svc.CreateProfileStore();
            var profile = store.Load(SelectedProfile);
            var key = _svc.ResolveKey(Password, null, profile.Encryption);
            var cache = _svc.CreateBackupIndexCache();
            var previous = cache.Load(SelectedProfile);
            var engine = _svc.CreateBackupEngine(profile.AuthToken);

            var result = await engine.RunBackupAsync(profile, key, previous, ct);
            if (result.Success)
            {
                cache.Save(SelectedProfile, engine.LastIndex!);
                LastBackupInfo = $"Backup {result.BackupId}: +{result.FilesAdded} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)";
                Status = "Backup complete";
                _log.Log(LastBackupInfo);

                await AppCommands.EnforceRetentionAsync(_svc, profile.Registry, profile.Retention.MaxBackups, key, profile.Encryption.Enabled, ct, profile.AuthToken);
            }
            else
            {
                Status = $"Failed: {result.Error}";
                _log.Log($"Backup failed: {result.Error}");
            }
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled";
            _log.Log("Backup cancelled");
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            _log.Log($"Error: {ex.Message}");
        }
        finally { IsRunning = false; Password = ""; }
    }
}
