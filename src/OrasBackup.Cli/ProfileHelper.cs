using System.Text.Json;
using OrasBackup.Core.Config;

namespace OrasBackup.Cli;

internal static class ProfileHelper
{
    private static readonly string ProfileDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".orasbackup", "profiles");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string GetProfilePath(string name) => Path.Combine(ProfileDir, $"{name}.json");

    public static BackupProfile Load(string name)
    {
        var path = GetProfilePath(name);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Profile '{name}' not found at {path}");
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BackupProfile>(json, JsonOpts)
            ?? throw new InvalidOperationException("Failed to deserialize profile");
    }

    public static void Save(BackupProfile profile)
    {
        Directory.CreateDirectory(ProfileDir);
        var path = GetProfilePath(profile.Name);
        File.WriteAllText(path, JsonSerializer.Serialize(profile, JsonOpts));
    }

    public static IEnumerable<string> ListProfiles()
    {
        if (!Directory.Exists(ProfileDir)) yield break;
        foreach (var file in Directory.EnumerateFiles(ProfileDir, "*.json"))
            yield return Path.GetFileNameWithoutExtension(file);
    }
}
