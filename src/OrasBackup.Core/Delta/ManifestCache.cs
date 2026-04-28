using System.Text.Json;

namespace OrasBackup.Core.Delta;

/// <summary>
/// Persists DeltaManifest to local disk between backup runs, enabling incremental backups.
/// </summary>
public sealed class ManifestCache
{
    private readonly string _cacheDir;

    public ManifestCache(string? cacheDir = null) =>
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "state");

    public void Save(string profileName, DeltaManifest manifest)
    {
        Directory.CreateDirectory(_cacheDir);
        var path = GetPath(profileName);
        File.WriteAllBytes(path, manifest.Serialize());
    }

    public DeltaManifest? Load(string profileName)
    {
        var path = GetPath(profileName);
        if (!File.Exists(path)) return null;
        return DeltaManifest.Deserialize(File.ReadAllBytes(path));
    }

    private string GetPath(string profileName) =>
        Path.Combine(_cacheDir, $"{profileName}.manifest.json");
}
