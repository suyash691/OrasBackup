namespace OrasBackup.Core.Backup;

/// <summary>
/// Persists BackupIndex to local disk between backup runs, enabling chunk-level deduplication.
/// Index is stored as-is (file paths are not sensitive — the actual file content is encrypted in the registry).
/// </summary>
public sealed class BackupIndexCache
{
    private readonly string _cacheDir;

    public BackupIndexCache(string? cacheDir = null) =>
        _cacheDir = cacheDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "state");

    public void Save(string profileName, BackupIndex index)
    {
        Directory.CreateDirectory(_cacheDir);
        File.WriteAllBytes(GetPath(profileName), index.Serialize());
    }

    public BackupIndex? Load(string profileName)
    {
        var path = GetPath(profileName);
        if (!File.Exists(path)) return null;
        try { return BackupIndex.Deserialize(File.ReadAllBytes(path)); }
        catch { return null; } // corrupt cache — treat as fresh
    }

    public void Delete(string profileName)
    {
        var path = GetPath(profileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private string GetPath(string profileName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(profileName, @"^[\w\-]+$"))
            throw new ArgumentException($"Invalid profile name: {profileName}");
        return Path.Combine(_cacheDir, $"{profileName}.index.json");
    }
}
