using System.Text.Json;
using OrasBackup.Core.Config;

namespace OrasBackup.Cli;

public interface IProfileStore
{
    BackupProfile Load(string name);
    void Save(BackupProfile profile);
    string GetProfilePath(string name);
    IEnumerable<string> ListProfiles();
}

public sealed class FileProfileStore : IProfileStore
{
    private readonly string _profileDir;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public FileProfileStore(string? profileDir = null) =>
        _profileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "profiles");

    public string GetProfilePath(string name) => Path.Combine(_profileDir, $"{name}.json");

    public BackupProfile Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile '{name}' not found at {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BackupProfile>(json, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize profile");
    }

    public void Save(BackupProfile profile)
    {
        Directory.CreateDirectory(_profileDir);
        var path = GetProfilePath(profile.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
    }

    public IEnumerable<string> ListProfiles()
    {
        if (!Directory.Exists(_profileDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(_profileDir, "*.json"))
            yield return Path.GetFileNameWithoutExtension(file);
    }
}

/// <summary>Kept for backward compatibility — delegates to FileProfileStore.</summary>
internal static class ProfileHelper
{
    private static readonly FileProfileStore Store = new();
    public static BackupProfile Load(string name) => Store.Load(name);
    public static void Save(BackupProfile profile) => Store.Save(profile);
    public static string GetProfilePath(string name) => Store.GetProfilePath(name);
    public static IEnumerable<string> ListProfiles() => Store.ListProfiles();
}
