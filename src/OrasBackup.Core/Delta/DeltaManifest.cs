using System.Text.Json;

namespace OrasBackup.Core.Delta;

public sealed class DeltaManifest
{
    public string BackupId { get; set; } = Guid.NewGuid().ToString();
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? BasedOn { get; set; }
    public List<FileSnapshot> Files { get; set; } = [];
    public List<string> Deleted { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, JsonCtx.Default.DeltaManifest);

    public static DeltaManifest Deserialize(byte[] data) =>
        JsonSerializer.Deserialize(data, JsonCtx.Default.DeltaManifest)
        ?? throw new InvalidOperationException("Failed to deserialize manifest");
}

[System.Text.Json.Serialization.JsonSerializable(typeof(DeltaManifest))]
internal sealed partial class JsonCtx : System.Text.Json.Serialization.JsonSerializerContext;
