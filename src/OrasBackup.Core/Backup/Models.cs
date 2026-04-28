using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrasBackup.Core.Backup;

/// <summary>Root index pushed as the backup's main OCI image. Points to chunk images.</summary>
public sealed class BackupIndex
{
    public string BackupId { get; set; } = "";
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public bool Encrypted { get; set; }
    public List<ChunkRef> Chunks { get; set; } = [];
    public List<string> DeletedFiles { get; set; } = [];
    public List<string> AllFiles { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, BackupJsonCtx.Default.BackupIndex);
    public static BackupIndex Deserialize(byte[] data) =>
        JsonSerializer.Deserialize(data, BackupJsonCtx.Default.BackupIndex)
        ?? throw new InvalidOperationException("Failed to deserialize BackupIndex");
}

/// <summary>Reference to a chunk image in the registry.</summary>
public sealed class ChunkRef
{
    public string Path { get; set; } = "";
    public string Tag { get; set; } = "";
    public string ContentHash { get; set; } = "";
    public int FileCount { get; set; }
    public long TotalBytes { get; set; }
}

/// <summary>Manifest stored as layer 0 of each chunk image. Lists files in the chunk.</summary>
public sealed class ChunkManifest
{
    public List<ChunkFile> Files { get; set; } = [];

    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, BackupJsonCtx.Default.ChunkManifest);
    public static ChunkManifest Deserialize(byte[] data) =>
        JsonSerializer.Deserialize(data, BackupJsonCtx.Default.ChunkManifest)
        ?? throw new InvalidOperationException("Failed to deserialize ChunkManifest");
}

/// <summary>A single file within a chunk.</summary>
public sealed class ChunkFile
{
    public string RelativePath { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public int UnixMode { get; set; }
    /// <summary>Index of the layer in the chunk image (layer 0 = ChunkManifest, layer 1+ = files).</summary>
    public int LayerIndex { get; set; }
}

[JsonSerializable(typeof(BackupIndex))]
[JsonSerializable(typeof(ChunkManifest))]
internal sealed partial class BackupJsonCtx : JsonSerializerContext;
