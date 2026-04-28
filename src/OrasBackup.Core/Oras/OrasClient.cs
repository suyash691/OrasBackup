using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OrasBackup.Core.Oras;

/// <summary>
/// ORAS client that shells out to the `oras` CLI binary.
/// Requires `oras` to be on PATH and authenticated with the target OCI registry.
/// </summary>
public sealed class OrasClient : IOrasClient
{
    private readonly ILogger<OrasClient> _logger;
    private readonly string _orasBinary;
    private readonly bool _plainHttp;

    public OrasClient(ILogger<OrasClient> logger, string orasBinary = "oras", bool plainHttp = false)
    {
        _logger = logger;
        _orasBinary = orasBinary;
        _plainHttp = plainHttp;
    }

    private string HttpFlag => _plainHttp ? " --plain-http" : "";

    public async Task PushAsync(string reference, IReadOnlyList<OrasLayer> layers, CancellationToken ct = default)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"orasbackup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var fileArgs = new List<string>();
            for (var i = 0; i < layers.Count; i++)
            {
                var fileName = $"layer-{i}.bin";
                var filePath = Path.Combine(tempDir, fileName);
                await File.WriteAllBytesAsync(filePath, layers[i].Data, ct);
                fileArgs.Add($"{filePath}:{layers[i].MediaType}");
            }

            await RunOrasAsync($"push --disable-path-validation{HttpFlag} {reference} {string.Join(' ', fileArgs)}", ct);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* best effort */ }
        }
    }

    public async Task<byte[]> PullLayerAsync(string reference, string digest, CancellationToken ct = default)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"orasbackup-{Guid.NewGuid():N}.bin");
        try
        {
            // Extract repo from reference (strip tag)
            var repo = reference.Contains(':') ? reference[..reference.LastIndexOf(':')] : reference;
            await RunOrasAsync($"blob fetch{HttpFlag} --output {tempFile} {repo}@{digest}", ct);
            return await File.ReadAllBytesAsync(tempFile, ct);
        }
        finally
        {
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }

    public async Task<IReadOnlyList<OrasManifestEntry>> FetchManifestLayersAsync(string reference, CancellationToken ct = default)
    {
        var json = await RunOrasAsync($"manifest fetch{HttpFlag} {reference}", ct);
        using var doc = JsonDocument.Parse(json);
        var entries = new List<OrasManifestEntry>();
        if (doc.RootElement.TryGetProperty("layers", out var layers))
        {
            foreach (var l in layers.EnumerateArray())
            {
                entries.Add(new OrasManifestEntry(
                    l.GetProperty("mediaType").GetString() ?? "",
                    l.GetProperty("digest").GetString() ?? "",
                    l.GetProperty("size").GetInt64()));
            }
        }
        return entries;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct = default)
    {
        var output = await RunOrasAsync($"repo tags{HttpFlag} {repository}", ct);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<string> RunOrasAsync(string arguments, CancellationToken ct)
    {
        _logger.LogDebug("Running: {Binary} {Args}", _orasBinary, arguments);
        var psi = new ProcessStartInfo(_orasBinary, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start oras");
        var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = await proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);

        if (proc.ExitCode != 0)
        {
            _logger.LogError("oras failed (exit {Code}): {Stderr}", proc.ExitCode, stderr);
            throw new InvalidOperationException($"oras exited with code {proc.ExitCode}: {stderr}");
        }

        return stdout;
    }
}
