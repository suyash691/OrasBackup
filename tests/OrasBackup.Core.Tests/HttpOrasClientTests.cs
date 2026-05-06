using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class HttpOrasClientTests
{
    private readonly MockHttpHandler _handler = new();
    private readonly HttpOrasClient _sut;

    public HttpOrasClientTests()
    {
        var http = new HttpClient(_handler) { BaseAddress = new Uri("http://localhost:5000") };
        _sut = new HttpOrasClient(http, NullLogger<HttpOrasClient>.Instance);
    }

    [Fact]
    public async Task PushAsync_UploadsBlobs_ThenManifest()
    {
        // HEAD blob check → 404 (not exists)
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        // POST initiate upload → 202 with Location
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { Location = new Uri("/v2/repo/blobs/uploads/uuid1?state=x", UriKind.Relative) } });
        // PUT complete upload → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        // HEAD config blob → 404
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        // POST config upload → 202
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted) { Headers = { Location = new Uri("/v2/repo/blobs/uploads/uuid2?state=y", UriKind.Relative) } });
        // PUT config → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));
        // PUT manifest → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));

        var layers = new List<OrasLayer> { new("application/octet-stream", "hello"u8.ToArray()) };
        await _sut.PushAsync("repo:tag1", layers);

        // Verify manifest PUT was the last request
        var lastReq = _handler.Requests.Last();
        Assert.Equal(HttpMethod.Put, lastReq.Method);
        Assert.Contains("/manifests/tag1", lastReq.RequestUri!.ToString());
    }

    [Fact]
    public async Task PushAsync_SkipsExistingBlob()
    {
        // HEAD blob → 200 (already exists)
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        // HEAD config → 200
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK));
        // PUT manifest → 201
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));

        await _sut.PushAsync("repo:tag1", [new("application/octet-stream", "data"u8.ToArray())]);

        // Should NOT have POST/PUT for blob upload — only HEAD + manifest PUT
        Assert.DoesNotContain(_handler.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FetchManifestLayersAsync_ParsesLayers()
    {
        var manifest = """{"layers":[{"mediaType":"app/tar","digest":"sha256:abc","size":100}]}""";
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });

        var layers = await _sut.FetchManifestLayersAsync("repo:tag1");

        Assert.Single(layers);
        Assert.Equal("app/tar", layers[0].MediaType);
        Assert.Equal("sha256:abc", layers[0].Digest);
        Assert.Equal(100, layers[0].Size);
    }

    [Fact]
    public async Task PullLayerAsync_ReturnsBlobData()
    {
        var data = "blob content"u8.ToArray();
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });

        var result = await _sut.PullLayerAsync("repo:tag1", "sha256:abc");

        Assert.Equal(data, result);
    }

    [Fact]
    public async Task ListTagsAsync_ParsesTags()
    {
        var json = """{"tags":["v1","v2","latest"]}""";
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });

        var tags = await _sut.ListTagsAsync("repo");

        Assert.Equal(3, tags.Count);
        Assert.Contains("latest", tags);
    }

    [Fact]
    public async Task DeleteTagAsync_HeadsThenDeletes()
    {
        var headResp = new HttpResponseMessage(HttpStatusCode.OK);
        headResp.Headers.Add("Docker-Content-Digest", "sha256:manifestdigest");
        _handler.Enqueue(headResp);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Accepted));

        await _sut.DeleteTagAsync("repo", "v1");

        Assert.Equal(HttpMethod.Head, _handler.Requests[0].Method);
        Assert.Equal(HttpMethod.Delete, _handler.Requests[1].Method);
        Assert.Contains("sha256:manifestdigest", _handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task GetDigestAsync_ReturnsDigest()
    {
        var resp = new HttpResponseMessage(HttpStatusCode.OK);
        resp.Headers.Add("Docker-Content-Digest", "sha256:abc123");
        _handler.Enqueue(resp);

        var digest = await _sut.GetDigestAsync("repo", "v1");
        Assert.Equal("sha256:abc123", digest);
    }

    [Fact]
    public async Task GetDigestAsync_ReturnsNull_WhenNotFound()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await _sut.GetDigestAsync("repo", "missing"));
    }

    [Fact]
    public async Task TagAsync_FetchesThenPuts()
    {
        var manifest = """{"schemaVersion":2}""";
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(manifest) });
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Created));

        await _sut.TagAsync("repo", "v1", "latest");

        Assert.Equal(HttpMethod.Get, _handler.Requests[0].Method);
        Assert.Contains("/manifests/v1", _handler.Requests[0].RequestUri!.ToString());
        Assert.Equal(HttpMethod.Put, _handler.Requests[1].Method);
        Assert.Contains("/manifests/latest", _handler.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task SendWithRetry_RetriesOn503()
    {
        // 503 → 503 → 200
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tags":[]}""") });

        var tags = await _sut.ListTagsAsync("repo");
        Assert.Empty(tags);
        Assert.Equal(3, _handler.Requests.Count);
    }

    [Fact]
    public async Task SendWithRetry_DoesNotRetryOn404()
    {
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.NotFound));
        Assert.Null(await _sut.GetDigestAsync("repo", "missing"));
        Assert.Single(_handler.Requests);
    }

    [Fact]
    public async Task ListTagsAsync_FollowsPagination()
    {
        var page1 = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tags":["a","b"]}""") };
        page1.Headers.Add("Link", "</v2/repo/tags/list?n=2&last=b>; rel=\"next\"");
        _handler.Enqueue(page1);
        _handler.Enqueue(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"tags":["c"]}""") });

        var tags = await _sut.ListTagsAsync("repo");
        Assert.Equal(3, tags.Count);
        Assert.Equal(["a", "b", "c"], tags);
    }

    [Theory]
    [InlineData("ghcr.io/user/repo:tag", "user/repo", "tag")]
    [InlineData("localhost:5000/myrepo:v1", "myrepo", "v1")]
    [InlineData("myrepo", "myrepo", "latest")]
    [InlineData("registry.io/org/repo@sha256:abc", "org/repo", "sha256:abc")]
    public void ParseReference_Variants(string reference, string expectedRepo, string expectedTag)
    {
        var (repo, tag) = _sut.ParseReference(reference);
        Assert.Equal(expectedRepo, repo);
        Assert.Equal(expectedTag, tag);
    }

    [Theory]
    [InlineData("ghcr.io/user/repo", "user/repo")]
    [InlineData("localhost:5000/myrepo", "myrepo")]
    [InlineData("plainrepo", "plainrepo")]
    public void StripHost_Variants(string input, string expected)
    {
        Assert.Equal(expected, _sut.StripHost(input));
    }
}

/// <summary>Simple mock HttpMessageHandler that returns queued responses.</summary>
internal class MockHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();
    public List<HttpRequestMessage> Requests { get; } = [];

    public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_responses.Count > 0
            ? _responses.Dequeue()
            : new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}

public class HttpOrasClientUrlTests
{
    [Theory]
    [InlineData("ghcr.io/suyash691/testbackup", "suyash691/testbackup")]
    [InlineData("https://ghcr.io/suyash691/testbackup", "suyash691/testbackup")]
    [InlineData("ghcr.io/org/team/repo", "org/team/repo")]
    [InlineData("localhost:5000/myrepo", "myrepo")]
    [InlineData("registry.example.com/foo/bar", "foo/bar")]
    public void StripHost_WithMatchingBaseAddress(string registry, string expectedRepo)
    {
        // Simulate what CreateHttpClient does: extract host, set BaseAddress
        var cleaned = registry.Replace("https://", "").Replace("http://", "");
        var firstSlash = cleaned.IndexOf('/');
        var host = firstSlash > 0 ? cleaned[..firstSlash] : cleaned;
        var scheme = host.Contains("localhost") ? "http" : "https";

        var http = new HttpClient { BaseAddress = new Uri($"{scheme}://{host}") };
        var sut = new HttpOrasClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpOrasClient>.Instance);

        var result = sut.StripHost(registry);
        Assert.Equal(expectedRepo, result);
    }

    [Theory]
    [InlineData("ghcr.io/suyash691/testbackup:mytag", "suyash691/testbackup", "mytag")]
    [InlineData("ghcr.io/org/repo:latest", "org/repo", "latest")]
    [InlineData("localhost:5000/myrepo:v1", "myrepo", "v1")]
    public void ParseReference_WithMatchingBaseAddress(string reference, string expectedRepo, string expectedTag)
    {
        var host = reference.Split('/')[0];
        if (host.Contains(':') && !host.Contains('.')) host = reference[..(reference.IndexOf('/'))]; // localhost:5000
        else { var s = reference.IndexOf('/'); host = reference[..s]; }
        var scheme = host.Contains("localhost") ? "http" : "https";

        var http = new HttpClient { BaseAddress = new Uri($"{scheme}://{host}") };
        var sut = new HttpOrasClient(http, Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpOrasClient>.Instance);

        var (repo, tag) = sut.ParseReference(reference);
        Assert.Equal(expectedRepo, repo);
        Assert.Equal(expectedTag, tag);
    }
}

public class HttpOrasClientAuthTests
{
    [Fact]
    public async Task TokenExchange_On401_FetchesTokenAndRetries()
    {
        var handler = new MockHttpHandler();

        // First request: 401 with WWW-Authenticate challenge
        var challenge = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.ParseAdd(
            "Bearer realm=\"https://auth.example.io/token\",service=\"registry.example.io\",scope=\"repository:user/repo:pull,push\"");
        handler.Enqueue(challenge);

        // Token endpoint: return a token
        var tokenResp = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"token\":\"test-access-token-123\"}")
        };
        handler.Enqueue(tokenResp);

        // Retry with token: success
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent("{\"tags\":[\"v1\"]}")
        });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://registry.example.io") };
        var sut = new HttpOrasClient(http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpOrasClient>.Instance,
            authToken: "my-pat");

        var tags = await sut.ListTagsAsync("user/repo");

        Assert.Single(tags);
        Assert.Equal("v1", tags[0]);
        // Should have made 3 requests: original 401, token fetch, retry with Bearer
        Assert.Equal(3, handler.Requests.Count);
        // Token fetch should use Basic auth
        Assert.Contains("Basic", handler.Requests[1].Headers.Authorization?.ToString() ?? "");
        // Retry should use Bearer token
        Assert.Equal("Bearer", handler.Requests[2].Headers.Authorization?.Scheme);
        Assert.Equal("test-access-token-123", handler.Requests[2].Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task CachedToken_UsedOnSubsequentRequests()
    {
        var handler = new MockHttpHandler();

        // First request: 401
        var challenge = new HttpResponseMessage(System.Net.HttpStatusCode.Unauthorized);
        challenge.Headers.WwwAuthenticate.ParseAdd(
            "Bearer realm=\"https://auth.example.io/token\",service=\"reg\",scope=\"repository:repo:pull\"");
        handler.Enqueue(challenge);
        // Token fetch
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"token\":\"cached-token\"}") });
        // Retry: success
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"tags\":[\"a\"]}") });
        // Second call: should use cached token directly (no 401)
        handler.Enqueue(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        { Content = new StringContent("{\"tags\":[\"b\"]}") });

        var http = new HttpClient(handler) { BaseAddress = new Uri("https://reg.io") };
        var sut = new HttpOrasClient(http,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpOrasClient>.Instance,
            authToken: "pat");

        await sut.ListTagsAsync("repo");
        var tags2 = await sut.ListTagsAsync("repo");

        Assert.Equal("b", tags2[0]);
        // 4th request should have Bearer token (cached, no new 401 exchange)
        Assert.Equal("Bearer", handler.Requests[3].Headers.Authorization?.Scheme);
        Assert.Equal("cached-token", handler.Requests[3].Headers.Authorization?.Parameter);
    }
}
