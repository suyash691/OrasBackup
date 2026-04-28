using System.Net;
using OrasBackup.Core.Oras;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace OrasBackup.Core.Tests;

public class HttpOrasClientStreamingTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"oras-stream-{Guid.NewGuid():N}");
    private readonly MockHttpHandler _handler = new();
    private readonly HttpOrasClient _sut;

    public HttpOrasClientStreamingTests()
    {
        Directory.CreateDirectory(_tempDir);
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost:5000") };
        _sut = new HttpOrasClient(http, NullLogger<HttpOrasClient>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_tempDir, true); } catch { } }

    [Fact]
    public async Task UploadBlobFromFileAsync_SkipsExistingBlob()
    {
        var file = Path.Combine(_tempDir, "data.bin");
        File.WriteAllText(file, "hello");

        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK)); // HEAD → exists

        var result = await _sut.UploadBlobFromFileAsync("repo", file, "application/octet-stream");

        Assert.StartsWith("sha256:", result.Digest);
        Assert.Equal(5, result.Size);
        Assert.Single(_handler.Requests); // only HEAD, no upload
    }

    [Fact]
    public async Task UploadBlobFromFileAsync_UploadsNewBlob()
    {
        var file = Path.Combine(_tempDir, "new.bin");
        File.WriteAllText(file, "new data");

        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound)); // HEAD → not found
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted) // POST → initiate
            { Headers = { Location = new Uri("/v2/repo/blobs/uploads/uuid?state=x", UriKind.Relative) } });
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created)); // PUT → complete

        var result = await _sut.UploadBlobFromFileAsync("repo", file, "app/test");

        Assert.Equal("app/test", result.MediaType);
        Assert.Equal(3, _handler.Requests.Count); // HEAD + POST + PUT
        Assert.Equal(HttpMethod.Put, _handler.Requests[2].Method);
    }

    [Fact]
    public async Task PullLayerToFileAsync_WritesToDisk()
    {
        var outputPath = Path.Combine(_tempDir, "output.bin");
        var data = "file content"u8.ToArray();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

        await _sut.PullLayerToFileAsync("repo:tag", "sha256:abc", outputPath);

        Assert.True(File.Exists(outputPath));
        Assert.Equal("file content", File.ReadAllText(outputPath));
    }

    [Fact]
    public async Task PullLayerToFileAsync_CreatesDirectories()
    {
        var outputPath = Path.Combine(_tempDir, "sub", "dir", "output.bin");
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("x"u8.ToArray()) });

        await _sut.PullLayerToFileAsync("repo:tag", "sha256:abc", outputPath);

        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task PushManifestAsync_CombinesInMemoryAndBlobLayers()
    {
        // HEAD config → 404, POST → 202, PUT config → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Headers = { Location = new Uri("/v2/repo/blobs/uploads/u1?s=x", UriKind.Relative) } });
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        // HEAD in-memory layer → 404, POST → 202, PUT → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted)
            { Headers = { Location = new Uri("/v2/repo/blobs/uploads/u2?s=x", UriKind.Relative) } });
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        // PUT manifest → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));

        var inMemory = new List<OrasLayer> { new("app/manifest", "manifest data"u8.ToArray()) };
        var blobs = new List<OrasLayerDescriptor> { new("app/file", "sha256:preupload", 1000) };

        await _sut.PushManifestAsync("repo:tag", inMemory, blobs);

        // Last request should be manifest PUT containing both layers
        var lastReq = _handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, lastReq.Method);
        Assert.Contains("/manifests/tag", lastReq.RequestUri!.ToString());
    }
}
