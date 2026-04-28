namespace OrasBackup.Core.Config;

public sealed class BackupProfile
{
    public string Name { get; set; } = "default";
    public List<string> SourcePaths { get; set; } = [];
    public List<string> ExcludePatterns { get; set; } = ["**/.git", "**/node_modules", "**/bin", "**/obj"];
    public string Registry { get; set; } = "";
    public ScheduleConfig Schedule { get; set; } = new();
    public EncryptionConfig Encryption { get; set; } = new();
    public RetentionConfig Retention { get; set; } = new();
}

public sealed class ScheduleConfig
{
    public bool Enabled { get; set; }
    public int IntervalMinutes { get; set; } = 60;
}

public sealed class EncryptionConfig
{
    public bool Enabled { get; set; } = true;
    public int Pbkdf2Iterations { get; set; } = 600_000;
    [System.Text.Json.Serialization.JsonIgnore]
    public string? ProfileName { get; set; }
}

public sealed class RetentionConfig
{
    public int MaxBackups { get; set; } = 50;
}

[System.Text.Json.Serialization.JsonSerializable(typeof(BackupProfile))]
public sealed partial class ProfileJsonCtx : System.Text.Json.Serialization.JsonSerializerContext;
