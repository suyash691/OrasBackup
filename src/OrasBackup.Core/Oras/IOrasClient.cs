namespace OrasBackup.Core.Oras;

public sealed record OrasLayer(string MediaType, byte[] Data, string? Digest = null);

public sealed record OrasManifestEntry(string MediaType, string Digest, long Size);

public interface IOrasClient
{
    Task PushAsync(string reference, IReadOnlyList<OrasLayer> layers, CancellationToken ct = default);
    Task<byte[]> PullLayerAsync(string reference, string digest, CancellationToken ct = default);
    Task<IReadOnlyList<OrasManifestEntry>> FetchManifestLayersAsync(string reference, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct = default);
}
