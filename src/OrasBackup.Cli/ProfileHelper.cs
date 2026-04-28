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
    private static readonly JsonSerializerOptions WriteOpts = new(ProfileJsonCtx.Default.Options) { WriteIndented = true };

    public FileProfileStore(string? profileDir = null) =>
        _profileDir = profileDir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "profiles");

    public string GetProfilePath(string name)
    {
        ValidateName(name);
        return Path.Combine(_profileDir, $"{name}.json");
    }

    public BackupProfile Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile '{name}' not found at {path}");
        var json = File.ReadAllText(path);
        var profile = JsonSerializer.Deserialize(json, ProfileJsonCtx.Default.BackupProfile)
            ?? throw new InvalidOperationException("Failed to deserialize profile");
        profile.Encryption.ProfileName = name;
        return profile;
    }

    public void Save(BackupProfile profile)
    {
        Directory.CreateDirectory(_profileDir);
        var path = GetProfilePath(profile.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, WriteOpts));
    }

    public IEnumerable<string> ListProfiles()
    {
        if (!Directory.Exists(_profileDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(_profileDir, "*.json"))
            yield return Path.GetFileNameWithoutExtension(file);
    }

    private static void ValidateName(string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[\w\-]+$"))
            throw new ArgumentException($"Invalid profile name: {name}. Use only alphanumeric, dash, underscore.");
    }
}
