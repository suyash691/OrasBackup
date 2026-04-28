using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace OrasBackup.Core.Oras;

public sealed class HttpOrasClient : IOrasClient
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpOrasClient> _logger;

    public HttpOrasClient(HttpClient http, ILogger<HttpOrasClient> logger)
    {
        _http = http;
        _logger = logger;

        var pat = Environment.GetEnvironmentVariable("ORAS_PAT");
        var username = Environment.GetEnvironmentVariable("ORAS_USERNAME");
        var password = Environment.GetEnvironmentVariable("ORAS_PASSWORD");
        if (!string.IsNullOrEmpty(pat))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pat);
        else if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

    public async Task PushAsync(string reference, IReadOnlyList<OrasLayer> layers, CancellationToken ct = default)
    {
        var (repo, tag) = ParseReference(reference);

        var descriptors = new List<(string mediaType, string digest, long size)>();
        foreach (var layer in layers)
        {
            var digest = ComputeSha256(layer.Data);
            await UploadBlobAsync(repo, digest, layer.Data, ct);
            descriptors.Add((layer.MediaType, digest, layer.Data.Length));
        }

        await PushManifestInternalAsync(repo, tag, descriptors, ct);
    }

    public async Task PushManifestAsync(string reference, IReadOnlyList<OrasLayer> inMemoryLayers,
        IReadOnlyList<OrasLayerDescriptor> blobLayers, CancellationToken ct = default)
    {
        var (repo, tag) = ParseReference(reference);

        var descriptors = new List<(string mediaType, string digest, long size)>();

        // Upload in-memory layers
        foreach (var layer in inMemoryLayers)
        {
            var digest = ComputeSha256(layer.Data);
            await UploadBlobAsync(repo, digest, layer.Data, ct);
            descriptors.Add((layer.MediaType, digest, layer.Data.Length));
        }

        // Add pre-uploaded blob descriptors
        foreach (var blob in blobLayers)
            descriptors.Add((blob.MediaType, blob.Digest, blob.Size));

        await PushManifestInternalAsync(repo, tag, descriptors, ct);
    }

    public async Task<OrasLayerDescriptor> UploadBlobFromFileAsync(string repository, string filePath,
        string mediaType, CancellationToken ct = default)
    {
        var repo = StripHost(repository);

        // Compute digest from file stream
        string digest;
        long size;
        using (var hashStream = File.OpenRead(filePath))
        {
            digest = $"sha256:{Convert.ToHexString(await SHA256.HashDataAsync(hashStream, ct)).ToLowerInvariant()}";
            size = hashStream.Length;
        }

        // Check if blob already exists
        var headResp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"), ct);
        if (headResp.IsSuccessStatusCode)
            return new OrasLayerDescriptor(mediaType, digest, size);

        // Stream upload from file with retry (re-initiate session on each attempt
        // because registries may invalidate the upload URL after a failed PUT)
        for (var attempt = 0; ; attempt++)
        {
            var postResp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Post, $"/v2/{repo}/blobs/uploads/"), ct);
            postResp.EnsureSuccessStatusCode();
            var location = postResp.Headers.Location?.ToString()
                ?? throw new InvalidOperationException("No Location header from blob upload initiation");
            var separator = location.Contains('?') ? "&" : "?";
            var putUrl = $"{location}{separator}digest={Uri.EscapeDataString(digest)}";

            using var fileStream = File.OpenRead(filePath);
            var req = new HttpRequestMessage(HttpMethod.Put, putUrl) { Content = new StreamContent(fileStream) };
            req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            var putResp = await _http.SendAsync(req, ct);
            if (putResp.IsSuccessStatusCode || attempt >= 3) { putResp.EnsureSuccessStatusCode(); break; }
            var status = (int)putResp.StatusCode;
            if (status != 429 && status != 500 && status != 503) { putResp.EnsureSuccessStatusCode(); break; }
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _logger.LogWarning("Upload {Status} for {Digest}, re-initiating session in {Delay}s ({Attempt}/3)",
                status, digest, delay.TotalSeconds, attempt + 1);
            await Task.Delay(delay, ct);
        }

        return new OrasLayerDescriptor(mediaType, digest, size);
    }

    public async Task<byte[]> PullLayerAsync(string reference, string digest, CancellationToken ct = default)
    {
        var (repo, _) = ParseReference(reference);
        var resp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Get, $"/v2/{repo}/blobs/{digest}"), ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task PullLayerToFileAsync(string reference, string digest, string outputPath, CancellationToken ct = default)
    {
        var (repo, _) = ParseReference(reference);
        var resp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Get, $"/v2/{repo}/blobs/{digest}"), ct);
        resp.EnsureSuccessStatusCode();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = File.Create(outputPath);
        await resp.Content.CopyToAsync(output, ct);
    }

    public async Task<IReadOnlyList<OrasManifestEntry>> FetchManifestLayersAsync(string reference, CancellationToken ct = default)
    {
        var (repo, tag) = ParseReference(reference);
        var req = new HttpRequestMessage(HttpMethod.Get, $"/v2/{repo}/manifests/{tag}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        var resp = await SendWithRetryAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var entries = new List<OrasManifestEntry>();
        if (doc.RootElement.TryGetProperty("layers", out var layersEl))
            foreach (var l in layersEl.EnumerateArray())
                entries.Add(new OrasManifestEntry(
                    l.GetProperty("mediaType").GetString() ?? "",
                    l.GetProperty("digest").GetString() ?? "",
                    l.GetProperty("size").GetInt64()));
        return entries;
    }

    public async Task<IReadOnlyList<string>> ListTagsAsync(string repository, CancellationToken ct = default)
    {
        var repo = StripHost(repository);
        var allTags = new List<string>();
        var url = $"/v2/{repo}/tags/list";

        while (url != null)
        {
            var resp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Get, url), ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tags", out var tags))
                allTags.AddRange(tags.EnumerateArray().Select(t => t.GetString()!));

            // Follow Link header for pagination (OCI Distribution Spec)
            url = null;
            if (resp.Headers.TryGetValues("Link", out var links))
            {
                var link = links.FirstOrDefault();
                if (link != null)
                {
                    var match = System.Text.RegularExpressions.Regex.Match(link, @"<([^>]+)>");
                    if (match.Success) url = match.Groups[1].Value;
                }
            }
        }

        return allTags;
    }

    public async Task DeleteTagAsync(string repository, string tag, CancellationToken ct = default)
    {
        var repo = StripHost(repository);
        var headReq = new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/manifests/{tag}");
        headReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        var headResp = await SendWithRetryAsync(headReq, ct);
        headResp.EnsureSuccessStatusCode();

        var digest = headResp.Headers.GetValues("Docker-Content-Digest").FirstOrDefault()
            ?? throw new InvalidOperationException($"No digest header for {repository}:{tag}");

        var deleteResp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Delete, $"/v2/{repo}/manifests/{digest}"), ct);
        deleteResp.EnsureSuccessStatusCode();
    }

    public async Task<string?> GetDigestAsync(string repository, string tag, CancellationToken ct = default)
    {
        var repo = StripHost(repository);
        var req = new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/manifests/{tag}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        var resp = await SendWithRetryAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        return resp.Headers.GetValues("Docker-Content-Digest").FirstOrDefault();
    }

    public async Task TagAsync(string repository, string existingTag, string newTag, CancellationToken ct = default)
    {
        var repo = StripHost(repository);
        var getReq = new HttpRequestMessage(HttpMethod.Get, $"/v2/{repo}/manifests/{existingTag}");
        getReq.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.oci.image.manifest.v1+json"));
        var getResp = await SendWithRetryAsync(getReq, ct);
        getResp.EnsureSuccessStatusCode();
        var manifestBytes = await getResp.Content.ReadAsByteArrayAsync(ct);

        var putResp = await SendWithRetryAsync(MakePut($"/v2/{repo}/manifests/{newTag}", manifestBytes, "application/vnd.oci.image.manifest.v1+json"), ct);
        putResp.EnsureSuccessStatusCode();
    }

    // --- internal helpers ---

    private async Task PushManifestInternalAsync(string repo, string tag,
        List<(string mediaType, string digest, long size)> layerDescriptors, CancellationToken ct)
    {
        var config = "{}"u8.ToArray();
        var configDigest = ComputeSha256(config);
        await UploadBlobAsync(repo, configDigest, config, ct);

        var manifest = BuildManifest(configDigest, config.Length, layerDescriptors);
        var manifestBytes = Encoding.UTF8.GetBytes(manifest);

        var resp = await SendWithRetryAsync(MakePut($"/v2/{repo}/manifests/{tag}", manifestBytes, "application/vnd.oci.image.manifest.v1+json"), ct);
        resp.EnsureSuccessStatusCode();
        _logger.LogDebug("Pushed manifest {Repo}:{Tag}", repo, tag);
    }

    private async Task UploadBlobAsync(string repo, string digest, byte[] data, CancellationToken ct)
    {
        var headResp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Head, $"/v2/{repo}/blobs/{digest}"), ct);
        if (headResp.IsSuccessStatusCode) return;

        var postResp = await SendWithRetryAsync(new HttpRequestMessage(HttpMethod.Post, $"/v2/{repo}/blobs/uploads/"), ct);
        postResp.EnsureSuccessStatusCode();

        var location = postResp.Headers.Location?.ToString()
            ?? throw new InvalidOperationException("No Location header from blob upload initiation");

        var separator = location.Contains('?') ? "&" : "?";
        var putUrl = $"{location}{separator}digest={Uri.EscapeDataString(digest)}";
        var putResp = await SendWithRetryAsync(MakePut(putUrl, data, "application/octet-stream"), ct);
        putResp.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(HttpRequestMessage request, CancellationToken ct, int maxRetries = 3)
    {
        byte[]? contentBytes = null;
        MediaTypeHeaderValue? contentType = null;
        if (request.Content != null)
        {
            contentBytes = await request.Content.ReadAsByteArrayAsync(ct);
            contentType = request.Content.Headers.ContentType;
        }

        for (var attempt = 0; ; attempt++)
        {
            var req = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (contentBytes != null)
            {
                req.Content = new ByteArrayContent(contentBytes);
                if (contentType != null) req.Content.Headers.ContentType = contentType;
            }

            var resp = await _http.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode || attempt >= maxRetries) return resp;
            var status = (int)resp.StatusCode;
            if (status != 429 && status != 503 && status != 500) return resp;
            var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
            _logger.LogWarning("HTTP {Status} from {Url}, retrying in {Delay}s (attempt {Attempt}/{Max})",
                status, request.RequestUri, delay.TotalSeconds, attempt + 1, maxRetries);
            await Task.Delay(delay, ct);
        }
    }

    private static HttpRequestMessage MakePut(string url, byte[] data, string mediaType)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(data) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
        return req;
    }

    private static string BuildManifest(string configDigest, long configSize, List<(string mediaType, string digest, long size)> layers)
    {
        var layersJson = string.Join(",", layers.Select(l =>
            $$"""{"mediaType":"{{l.mediaType}}","digest":"{{l.digest}}","size":{{l.size}}}"""));
        return $$"""{"schemaVersion":2,"mediaType":"application/vnd.oci.image.manifest.v1+json","config":{"mediaType":"application/vnd.oci.image.config.v1+json","digest":"{{configDigest}}","size":{{configSize}}},"layers":[{{layersJson}}]}""";
    }

    private static string ComputeSha256(byte[] data) =>
        $"sha256:{Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant()}";

    internal string StripHost(string repo)
    {
        if (_http.BaseAddress is not null)
        {
            var hostPort = $"{_http.BaseAddress.Host}:{_http.BaseAddress.Port}/";
            if (repo.StartsWith(hostPort, StringComparison.OrdinalIgnoreCase)) return repo[hostPort.Length..];
            var hostSlash = $"{_http.BaseAddress.Host}/";
            if (repo.StartsWith(hostSlash, StringComparison.OrdinalIgnoreCase)) return repo[hostSlash.Length..];
        }
        var firstSlash = repo.IndexOf('/');
        if (firstSlash > 0 && (repo[..firstSlash].Contains('.') || repo[..firstSlash].Contains(':')))
            return repo[(firstSlash + 1)..];
        return repo;
    }

    internal (string repo, string tag) ParseReference(string reference)
    {
        if (reference.Contains('@'))
        {
            var atIdx = reference.IndexOf('@');
            return (StripHost(reference[..atIdx]), reference[(atIdx + 1)..]);
        }
        var lastSlash = reference.LastIndexOf('/');
        var colonSearch = lastSlash > 0 ? reference.IndexOf(':', lastSlash) : reference.LastIndexOf(':');
        if (colonSearch > 0)
            return (StripHost(reference[..colonSearch]), reference[(colonSearch + 1)..]);
        return (StripHost(reference), "latest");
    }
}
