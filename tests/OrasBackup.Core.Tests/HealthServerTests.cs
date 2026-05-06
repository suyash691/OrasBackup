using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class HealthServerTests
{
    [Fact]
    public void GetStatus_ReturnsValidJson()
    {
        using var server = new HealthServer(0);
        var json = server.GetStatus();
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"lastBackup\"", json);
    }

    [Fact]
    public void UpdateStatus_Success_ReflectsInGetStatus()
    {
        using var server = new HealthServer(0);
        server.UpdateStatus(true, "backup-123");
        var json = server.GetStatus();
        Assert.Contains("ok", json);
        Assert.Contains("backup-123", json);
    }

    [Fact]
    public void UpdateStatus_Failure_ReflectsInGetStatus()
    {
        using var server = new HealthServer(0);
        server.UpdateStatus(false, "backup-456");
        Assert.Contains("failed", server.GetStatus());
    }

    [Fact]
    public void GetStatus_SpecialChars_ValidJson()
    {
        using var server = new HealthServer(0);
        server.UpdateStatus(true, "backup\"with\\quotes");
        var doc = System.Text.Json.JsonDocument.Parse(server.GetStatus());
        Assert.NotNull(doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task ListenAsync_RespondsToHealthz()
    {
        // Use a random high port to avoid conflicts
        var port = 18000 + Random.Shared.Next(1000);
        using var server = new HealthServer(port);
        server.Start();
        server.UpdateStatus(true, "test-backup");

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://localhost:{port}/healthz");
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("ok", body);
        Assert.Contains("test-backup", body);
    }

    [Fact]
    public async Task ListenAsync_Returns404ForOtherPaths()
    {
        var port = 18000 + Random.Shared.Next(1000);
        using var server = new HealthServer(port);
        server.Start();

        using var http = new HttpClient();
        var resp = await http.GetAsync($"http://localhost:{port}/other");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public void Dispose_StopsListening()
    {
        var port = 18000 + Random.Shared.Next(1000);
        var server = new HealthServer(port);
        server.Start();
        server.Dispose(); // should not throw
    }

    [Fact]
    public async Task ListenAsync_DisposeDuringRequest_DoesNotThrow()
    {
        var port = 18000 + Random.Shared.Next(1000);
        var server = new HealthServer(port);
        server.Start();

        // Make a request and immediately dispose — exercises the shutdown exception paths
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        var task = http.GetAsync($"http://localhost:{port}/healthz");
        await Task.Delay(50);
        server.Dispose();

        // Either completes or throws — neither should crash the server
        try { await task; } catch { }
    }
}
