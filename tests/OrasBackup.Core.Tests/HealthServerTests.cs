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
}
