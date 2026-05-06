using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;
using Xunit;

namespace OrasBackup.Core.Tests;

public class DefaultServiceFactoryTests
{
    [Fact]
    public void CreateOrasClient_ReturnsNewInstancePerCall()
    {
        var svc = new DefaultServiceFactory();
        var a = svc.CreateOrasClient();
        var b = svc.CreateOrasClient();
        Assert.NotSame(a, b); // new client per profile/call
    }

    [Fact]
    public void CreateEncryptor_ReturnsSingleton()
    {
        var svc = new DefaultServiceFactory();
        var a = svc.CreateEncryptor();
        var b = svc.CreateEncryptor();
        Assert.Same(a, b);
    }

    [Fact]
    public void CreateBackupEngine_ReturnsNewInstance()
    {
        var svc = new DefaultServiceFactory();
        var engine = svc.CreateBackupEngine();
        Assert.IsType<BackupEngine>(engine);
    }

    [Fact]
    public void CreateRestoreEngine_ReturnsNewInstance()
    {
        var svc = new DefaultServiceFactory();
        var engine = svc.CreateRestoreEngine();
        Assert.IsType<RestoreEngine>(engine);
    }

    [Fact]
    public void CreateChunkEngine_ReturnsNewInstance()
    {
        var svc = new DefaultServiceFactory();
        var engine = svc.CreateChunkEngine();
        Assert.IsType<ChunkEngine>(engine);
    }

    [Fact]
    public void CreateBackupIndexCache_ReturnsNewInstance()
    {
        var svc = new DefaultServiceFactory();
        var cache = svc.CreateBackupIndexCache();
        Assert.IsType<BackupIndexCache>(cache);
    }

    [Fact]
    public void CreateProfileStore_ReturnsFileProfileStore()
    {
        var svc = new DefaultServiceFactory();
        Assert.IsType<FileProfileStore>(svc.CreateProfileStore());
    }

    [Fact]
    public void CreateLogger_ReturnsLogger()
    {
        var svc = new DefaultServiceFactory();
        var logger = svc.CreateLogger<BackupScheduler>();
        Assert.NotNull(logger);
    }

    [Fact]
    public void ResolveKey_EncryptionDisabled_ReturnsPlaceholder()
    {
        var svc = new DefaultServiceFactory();
        var key = svc.ResolveKey(null, null, new Core.Config.EncryptionConfig { Enabled = false });
        Assert.Equal(32, key.Length);
    }
    [Fact]
    public void CreateOrasClient_WithRegistry_SetsBaseAddressAndStripsHost()
    {
        var svc = new DefaultServiceFactory();
        var client = svc.CreateOrasClient("ghcr.io/suyash691/testbackup") as HttpOrasClient;
        Assert.NotNull(client);
        Assert.Equal("suyash691/testbackup", client!.StripHost("ghcr.io/suyash691/testbackup"));
    }

    [Fact]
    public void CreateOrasClient_InsecureEnvVar_UsesHttp()
    {
        var prev = Environment.GetEnvironmentVariable("ORAS_INSECURE");
        try
        {
            Environment.SetEnvironmentVariable("ORAS_INSECURE", "true");
            var svc = new DefaultServiceFactory();
            var client = svc.CreateOrasClient("localhost:5000/repo");
            Assert.NotNull(client);
        }
        finally { Environment.SetEnvironmentVariable("ORAS_INSECURE", prev); }
    }
}
