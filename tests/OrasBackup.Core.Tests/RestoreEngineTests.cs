using System.Formats.Tar;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Core.Tests;

public class RestoreEngineTests : IDisposable
{
    private readonly string _restoreDir = Path.Combine(Path.GetTempPath(), $"orasrestore-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly RestoreEngine _sut;
    private readonly byte[] _key = new byte[32];

    public RestoreEngineTests()
    {
        _encryptor.Decrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _sut = new RestoreEngine(_encryptor, _oras, NullLogger<RestoreEngine>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_restoreDir, true); } catch { }
    }

    [Fact]
    public async Task Restore_SingleLayer_ExtractsFiles()
    {
        var manifest = new DeltaManifest
        {
            BackupId = "abc123",
            Files = [new FileSnapshot("hello.txt", "hash", 5, DateTime.UtcNow)]
        };
        var tarData = CreateTar(("hello.txt", "hello"));

        SetupOras("abc123", manifest, tarData);

        await _sut.RestoreAsync(new RestoreOptions("registry.example.com/test/repo", "abc123", _restoreDir, _key, false));

        Assert.True(File.Exists(Path.Combine(_restoreDir, "hello.txt")));
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_restoreDir, "hello.txt")));
    }

    [Fact]
    public async Task Restore_ChainedLayers_AppliesInOrder()
    {
        var base_ = new DeltaManifest
        {
            BackupId = "base",
            Files = [new FileSnapshot("a.txt", "h1", 2, DateTime.UtcNow)]
        };
        var incr = new DeltaManifest
        {
            BackupId = "incr",
            BasedOn = "base",
            Files = [
                new FileSnapshot("a.txt", "h2", 7, DateTime.UtcNow),
                new FileSnapshot("b.txt", "h3", 3, DateTime.UtcNow)
            ]
        };

        SetupOras("base", base_, CreateTar(("a.txt", "v1")));
        SetupOras("incr", incr, CreateTar(("a.txt", "updated"), ("b.txt", "new")));

        await _sut.RestoreAsync(new RestoreOptions("registry.example.com/test/repo", "incr", _restoreDir, _key, false));

        Assert.Equal("updated", File.ReadAllText(Path.Combine(_restoreDir, "a.txt")));
        Assert.Equal("new", File.ReadAllText(Path.Combine(_restoreDir, "b.txt")));
    }

    [Fact]
    public async Task Restore_AppliesDeletions()
    {
        var base_ = new DeltaManifest
        {
            BackupId = "base",
            Files = [new FileSnapshot("keep.txt", "h1", 4, DateTime.UtcNow),
                     new FileSnapshot("remove.txt", "h2", 6, DateTime.UtcNow)]
        };
        var incr = new DeltaManifest
        {
            BackupId = "incr",
            BasedOn = "base",
            Deleted = ["remove.txt"],
            Files = [new FileSnapshot("keep.txt", "h1", 4, DateTime.UtcNow)]
        };

        SetupOras("base", base_, CreateTar(("keep.txt", "keep"), ("remove.txt", "gone")));
        SetupOras("incr", incr, null);

        await _sut.RestoreAsync(new RestoreOptions("registry.example.com/test/repo", "incr", _restoreDir, _key, false));

        Assert.True(File.Exists(Path.Combine(_restoreDir, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(_restoreDir, "remove.txt")));
    }

    [Fact]
    public async Task Restore_UsesLatestTag_WhenNoBackupIdSpecified()
    {
        var manifest = new DeltaManifest
        {
            BackupId = "latest",
            Files = [new FileSnapshot("f.txt", "h", 1, DateTime.UtcNow)]
        };
        SetupOras("latest", manifest, CreateTar(("f.txt", "data")));
        _oras.ListTagsAsync("registry.example.com/test/repo", Arg.Any<CancellationToken>())
            .Returns(new[] { "old", "latest" });

        await _sut.RestoreAsync(new RestoreOptions("registry.example.com/test/repo", null, _restoreDir, _key, false));

        Assert.True(File.Exists(Path.Combine(_restoreDir, "f.txt")));
    }

    private void SetupOras(string backupId, DeltaManifest manifest, byte[]? tarData)
    {
        var reference = $"registry.example.com/test/repo:{backupId}";
        var manifestBytes = manifest.Serialize();
        var layers = new List<OrasManifestEntry>
        {
            new("application/vnd.orasbackup.manifest+json", $"sha256:manifest-{backupId}", manifestBytes.Length)
        };
        if (tarData is not null)
            layers.Add(new("application/vnd.orasbackup.layer.v1.tar+encrypted", $"sha256:data-{backupId}", tarData.Length));

        _oras.DiscoverAsync(reference, Arg.Any<CancellationToken>()).Returns(layers);
        _oras.PullLayerAsync(reference, $"sha256:manifest-{backupId}", Arg.Any<CancellationToken>()).Returns(manifestBytes);
        if (tarData is not null)
            _oras.PullLayerAsync(reference, $"sha256:data-{backupId}", Arg.Any<CancellationToken>()).Returns(tarData);
    }

    private static byte[] CreateTar(params (string Name, string Content)[] files)
    {
        using var ms = new MemoryStream();
        using (var tar = new TarWriter(ms, leaveOpen: true))
        {
            foreach (var (name, content) in files)
            {
                var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
                {
                    DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content))
                };
                tar.WriteEntry(entry);
            }
        }
        return ms.ToArray();
    }
}
