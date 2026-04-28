using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Oras;
using Xunit;

namespace OrasBackup.Core.Tests;

public class RestoreEngineTests : IDisposable
{
    private readonly string _targetDir = Path.Combine(Path.GetTempPath(), $"restore-test-{Guid.NewGuid():N}");
    private readonly IOrasClient _oras = Substitute.For<IOrasClient>();
    private readonly IEncryptor _encryptor = Substitute.For<IEncryptor>();
    private readonly RestoreEngine _sut;

    public RestoreEngineTests()
    {
        _encryptor.Decrypt(Arg.Any<byte[]>(), Arg.Any<byte[]>()).Returns(ci => ci.ArgAt<byte[]>(0));
        _sut = new RestoreEngine(_oras, _encryptor, NullLogger<RestoreEngine>.Instance);
    }

    public void Dispose() { try { Directory.Delete(_targetDir, true); } catch { } }

    private static string Hash(byte[] data) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();

    private void SetupIndex(BackupIndex index)
    {
        var indexBytes = index.Serialize();
        _oras.FetchManifestLayersAsync(Arg.Is<string>(s => !s.Contains("chunk-")), Arg.Any<CancellationToken>())
            .Returns([new OrasManifestEntry("application/vnd.orasbackup.index.v2+json", "sha256:idx", indexBytes.Length)]);
        _oras.PullLayerAsync(Arg.Is<string>(s => !s.Contains("chunk-")), "sha256:idx", Arg.Any<CancellationToken>())
            .Returns(indexBytes);
    }

    private void SetupChunk(string tag, ChunkManifest manifest, Dictionary<int, byte[]> fileLayers)
    {
        var manifestBytes = manifest.Serialize();
        var layers = new List<OrasManifestEntry>
        {
            new("application/vnd.orasbackup.chunk.manifest+json", "sha256:cm", manifestBytes.Length)
        };
        foreach (var (idx, data) in fileLayers)
            layers.Add(new OrasManifestEntry("application/vnd.orasbackup.file.v2", $"sha256:f{idx}", data.Length));

        _oras.FetchManifestLayersAsync(Arg.Is<string>(s => s.Contains(tag)), Arg.Any<CancellationToken>())
            .Returns(layers.AsReadOnly());
        // Manifest pulled in-memory (small)
        _oras.PullLayerAsync(Arg.Is<string>(s => s.Contains(tag)), "sha256:cm", Arg.Any<CancellationToken>())
            .Returns(manifestBytes);
        // File layers pulled to disk via PullLayerToFileAsync
        foreach (var (idx, data) in fileLayers)
        {
            var capturedData = data;
            _oras.PullLayerToFileAsync(Arg.Is<string>(s => s.Contains(tag)), $"sha256:f{idx}",
                Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(ci => { File.WriteAllBytes(ci.ArgAt<string>(2), capturedData); return Task.CompletedTask; });
        }
    }

    [Fact]
    public async Task Restore_ExtractsFilesToTargetDir()
    {
        var data = "world"u8.ToArray();
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "hello.txt", LayerIndex = 1, Sha256 = Hash(data) }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = data });

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false);

        Assert.Equal("world", File.ReadAllText(Path.Combine(_targetDir, "hello.txt")));
    }

    [Fact]
    public async Task Restore_NullBackupId_UsesLatest()
    {
        var data = "data"u8.ToArray();
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "f.txt", LayerIndex = 1, Sha256 = Hash(data) }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = data });

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false);

        await _oras.Received().FetchManifestLayersAsync("reg/repo:latest", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_SpecificBackupId()
    {
        var data = "data"u8.ToArray();
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "f.txt", LayerIndex = 1, Sha256 = Hash(data) }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = data });

        await _sut.RestoreAsync("reg/repo", "20260101-120000-abc123", _targetDir, new byte[32], false);

        await _oras.Received().FetchManifestLayersAsync("reg/repo:20260101-120000-abc123", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Restore_CreatesSubdirectories()
    {
        var data = "nested"u8.ToArray();
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "a/b/c.txt", LayerIndex = 1, Sha256 = Hash(data) }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "a/b" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = data });

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false);

        Assert.Equal("nested", File.ReadAllText(Path.Combine(_targetDir, "a", "b", "c.txt")));
    }

    [Fact]
    public async Task Restore_DeletesFilesFromIndex()
    {
        var data = "fresh"u8.ToArray();
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "keep.txt", LayerIndex = 1, Sha256 = Hash(data) }] };
        var index = new BackupIndex
        {
            BackupId = "b1",
            Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }],
            DeletedFiles = ["old.txt"]
        };
        Directory.CreateDirectory(_targetDir);
        File.WriteAllText(Path.Combine(_targetDir, "old.txt"), "stale");
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = data });

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false);

        Assert.False(File.Exists(Path.Combine(_targetDir, "old.txt")));
        Assert.True(File.Exists(Path.Combine(_targetDir, "keep.txt")));
    }

    [Fact]
    public async Task Restore_DecryptsWhenEncrypted()
    {
        var decryptedData = System.Text.Encoding.UTF8.GetBytes("decrypted");
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "s.txt", LayerIndex = 1, Sha256 = Hash(decryptedData) }] };
        var index = new BackupIndex { BackupId = "b1", Encrypted = true, Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);

        // For encrypted restore: PullLayerToFileAsync writes encrypted data, then DecryptFile is called
        var manifestBytes = manifest.Serialize();
        _oras.FetchManifestLayersAsync(Arg.Is<string>(s => s.Contains("chunk-abc")), Arg.Any<CancellationToken>())
            .Returns(new List<OrasManifestEntry>
            {
                new("application/vnd.orasbackup.chunk.manifest+json", "sha256:cm", manifestBytes.Length),
                new("application/vnd.orasbackup.file.v2+encrypted", "sha256:f1", 10)
            }.AsReadOnly());
        _oras.PullLayerAsync(Arg.Is<string>(s => s.Contains("chunk-abc")), "sha256:cm", Arg.Any<CancellationToken>())
            .Returns(manifestBytes);
        _oras.PullLayerToFileAsync(Arg.Is<string>(s => s.Contains("chunk-abc")), "sha256:f1",
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { File.WriteAllBytes(ci.ArgAt<string>(2), "encrypted"u8.ToArray()); return Task.CompletedTask; });

        // DecryptFile: write decrypted content to .dec file
        _encryptor.DecryptFile(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var decPath = ci.ArgAt<string>(0) + ".dec";
                File.WriteAllText(decPath, "decrypted");
                return decPath;
            });

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], true);

        _encryptor.Received(1).DecryptFile(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
        Assert.Equal("decrypted", File.ReadAllText(Path.Combine(_targetDir, "s.txt")));
    }

    [Fact]
    public async Task Restore_MissingIndexLayer_Throws()
    {
        _oras.FetchManifestLayersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new List<OrasManifestEntry> { new("application/octet-stream", "sha256:x", 10) }.AsReadOnly());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false));
    }

    [Fact]
    public async Task Restore_Cancellation_Throws()
    {
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false, cts.Token));
    }

    [Fact]
    public async Task Restore_EmptyBackup_CreatesTargetDir()
    {
        var index = new BackupIndex { BackupId = "b1", Chunks = [] };
        SetupIndex(index);

        await _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false);

        Assert.True(Directory.Exists(_targetDir));
    }

    [Fact]
    public async Task Restore_PathTraversal_Throws()
    {
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "../../etc/passwd", LayerIndex = 1, Sha256 = "abc" }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = "evil"u8.ToArray() });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false));
    }

    [Fact]
    public async Task Restore_EmptySha256_Throws()
    {
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "f.txt", LayerIndex = 1, Sha256 = "" }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);
        SetupChunk("chunk-abc", manifest, new() { [1] = "data"u8.ToArray() });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false));
    }

    [Fact]
    public async Task Restore_LayerIndexOutOfRange_Throws()
    {
        var manifest = new ChunkManifest { Files = [new ChunkFile { RelativePath = "f.txt", LayerIndex = 99, Sha256 = "abc" }] };
        var index = new BackupIndex { BackupId = "b1", Chunks = [new ChunkRef { Tag = "chunk-abc", Path = "root" }] };
        SetupIndex(index);

        var manifestBytes = manifest.Serialize();
        _oras.FetchManifestLayersAsync(Arg.Is<string>(s => s.Contains("chunk-abc")), Arg.Any<CancellationToken>())
            .Returns(new List<OrasManifestEntry> { new("application/vnd.orasbackup.chunk.manifest+json", "sha256:cm", manifestBytes.Length) }.AsReadOnly());
        _oras.PullLayerAsync(Arg.Is<string>(s => s.Contains("chunk-abc")), "sha256:cm", Arg.Any<CancellationToken>())
            .Returns(manifestBytes);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _sut.RestoreAsync("reg/repo", null, _targetDir, new byte[32], false));
    }
}
