using System.Formats.Tar;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Backup;

public sealed record RestoreOptions(string Registry, string? BackupId, string TargetDir, byte[] EncryptionKey, bool EncryptionEnabled);

public sealed class RestoreEngine
{
    private readonly IEncryptor _encryptor;
    private readonly IOrasClient _oras;
    private readonly ILogger<RestoreEngine> _logger;

    public RestoreEngine(IEncryptor encryptor, IOrasClient oras, ILogger<RestoreEngine> logger)
    {
        _encryptor = encryptor;
        _oras = oras;
        _logger = logger;
    }

    public async Task RestoreAsync(RestoreOptions options, CancellationToken ct = default)
    {
        // 1. Resolve the backup ID (latest tag if not specified)
        var backupId = options.BackupId;
        if (string.IsNullOrEmpty(backupId))
        {
            var tags = await _oras.ListTagsAsync(options.Registry, ct);
            backupId = tags.LastOrDefault()
                ?? throw new InvalidOperationException("No backups found in registry");
        }

        // 2. Walk the chain and collect manifests in order (oldest first)
        var chain = new List<(DeltaManifest Manifest, string Reference)>();
        var currentId = backupId;

        while (currentId is not null)
        {
            var reference = $"{options.Registry}:{currentId}";
            var manifest = await PullManifestAsync(reference, options, ct);
            chain.Add((manifest, reference));
            currentId = manifest.BasedOn;
        }

        chain.Reverse(); // oldest first

        // 3. Apply each layer in order
        Directory.CreateDirectory(options.TargetDir);

        foreach (var (manifest, reference) in chain)
        {
            _logger.LogInformation("Applying backup layer {Id} ({Files} files, {Deleted} deletions)",
                manifest.BackupId, manifest.Files.Count, manifest.Deleted.Count);

            // Pull and extract data layer
            await ExtractLayerAsync(reference, options, ct);

            // Apply deletions
            foreach (var deleted in manifest.Deleted)
            {
                var path = Path.Combine(options.TargetDir, deleted.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    File.Delete(path);
                    _logger.LogDebug("Deleted: {Path}", deleted);
                }
            }
        }

        _logger.LogInformation("Restore complete to {Dir}", options.TargetDir);
    }

    private async Task<DeltaManifest> PullManifestAsync(string reference, RestoreOptions options, CancellationToken ct)
    {
        var layers = await _oras.DiscoverAsync(reference, ct);
        var manifestLayer = layers.FirstOrDefault(l => l.MediaType.Contains("manifest+json"))
            ?? throw new InvalidOperationException($"No manifest layer found in {reference}");

        var data = await _oras.PullLayerAsync(reference, manifestLayer.Digest, ct);
        var decrypted = options.EncryptionEnabled ? _encryptor.Decrypt(data, options.EncryptionKey) : data;
        return DeltaManifest.Deserialize(decrypted);
    }

    private async Task ExtractLayerAsync(string reference, RestoreOptions options, CancellationToken ct)
    {
        var layers = await _oras.DiscoverAsync(reference, ct);
        var dataLayer = layers.FirstOrDefault(l => l.MediaType.Contains("layer.v1.tar"));
        if (dataLayer is null) return; // No data layer (e.g., delete-only manifest)

        var data = await _oras.PullLayerAsync(reference, dataLayer.Digest, ct);
        var decrypted = options.EncryptionEnabled ? _encryptor.Decrypt(data, options.EncryptionKey) : data;

        using var ms = new MemoryStream(decrypted);
        await TarFile.ExtractToDirectoryAsync(ms, options.TargetDir, overwriteFiles: true, ct);
    }
}
