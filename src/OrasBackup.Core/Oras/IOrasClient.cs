namespace OrasBackup.Core.Oras;

/// <summary>A layer with small in-memory data (manifests, indexes).</summary>
public sealed record OrasLayer(string MediaType, byte[] Data);

/// <summary>A layer descriptor for building manifests (already-uploaded blobs).</summary>
public sealed record OrasLayerDescriptor(string MediaType, string Digest, long Size);

public sealed record OrasManifestEntry(string MediaType, string Digest, long Size);

public interface IOrasClient
{
    /// <summary>Push an image with small in-memory layers (manifests, indexes).</summary>
    Task PushAsync(string reference, IReadOnlyList<OrasLayer> layers, CancellationToken ct = default);

    /// <summary>Push an image from a mix of in-memory layers and pre-uploaded blob descriptors.</summary>
    Task PushManifestAsync(string reference, IReadOnlyList<OrasLayer> inMemoryLayers,
        IReadOnlyList<OrasLayerDescriptor> blobLayers, CancellationToken ct = default);

    /// <summary>Upload a blob from a file. Returns digest. Memory = stream buffer only.</summary>
    Task<OrasLayerDescriptor> UploadBlobFromFileAsync(string repository, string filePath, string mediaType, CancellationToken ct = default);

    /// <summary>Pull a small layer (manifest, index) into memory.</summary>
    Task<byte[]> PullLayerAsync(string reference, string digest, CancellationToken ct = default);

    /// <summary>Pull a layer directly to a file. Memory = stream buffer only.</summary>
    Task PullLayerToFileAsync(string reference, string digest, string outputPath, CancellationToken ct = default);

    Task<IReadOnlyList<OrasManifestEntry>> FetchManifestLayersAsync(string reference, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct = default);
    Task DeleteTagAsync(string repository, string tag, CancellationToken ct = default);
    Task<string?> GetDigestAsync(string repository, string tag, CancellationToken ct = default);
    Task TagAsync(string repository, string existingTag, string newTag, CancellationToken ct = default);
}
