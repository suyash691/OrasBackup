using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;

namespace OrasBackup.Integration.Tests;

/// <summary>
/// Shared fixture providing engines, temp dirs, and cleanup for integration tests.
/// Cleans up local temp dirs and registry artifacts on dispose.
/// </summary>
public abstract class IntegrationFixture : IDisposable
{
    protected readonly string SourceDir;
    protected readonly string RestoreDir;
    protected readonly string Registry;
    protected readonly byte[] Key;
    protected readonly BackupEngine BackupEngine;
    protected readonly RestoreEngine RestoreEngine;
    protected readonly CompactionEngine CompactionEngine;
    protected readonly DeltaTracker Tracker = new();
    private readonly AesEncryptor _encryptor = new(pbkdf2Iterations: 1000);
    private readonly List<string> _pushedTags = [];
    private readonly string _registryHost;

    protected IntegrationFixture()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        SourceDir = Path.Combine(Path.GetTempPath(), $"oras-int-src-{id}");
        RestoreDir = Path.Combine(Path.GetTempPath(), $"oras-int-dst-{id}");
        Directory.CreateDirectory(SourceDir);

        _registryHost = Environment.GetEnvironmentVariable("ORAS_REGISTRY") ?? "localhost:5000";
        Registry = $"{_registryHost}/test/backup-{id}";

        var salt = _encryptor.GenerateSalt();
        Key = _encryptor.DeriveKey("test-password", salt);

        var lf = NullLoggerFactory.Instance;
        var oras = new OrasClient(lf.CreateLogger<OrasClient>());
        BackupEngine = new BackupEngine(Tracker, _encryptor, oras, lf.CreateLogger<BackupEngine>());
        RestoreEngine = new RestoreEngine(_encryptor, oras, lf.CreateLogger<RestoreEngine>());
        CompactionEngine = new CompactionEngine(RestoreEngine, BackupEngine, lf.CreateLogger<CompactionEngine>());
    }

    protected static void EnsureRegistry()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ORAS_REGISTRY")))
            Assert.Fail("ORAS_REGISTRY not set — run via integration test workflow");
    }

    protected BackupProfile MakeProfile(bool encrypt = false) => new()
    {
        Name = "int-test",
        SourcePaths = [SourceDir],
        Registry = Registry,
        Encryption = new EncryptionConfig { Enabled = encrypt }
    };

    protected void TrackBackup(string backupId) => _pushedTags.Add(backupId);

    protected void WriteFile(string relative, string content)
    {
        var path = Path.Combine(SourceDir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    protected void DeleteFile(string relative)
    {
        var path = Path.Combine(SourceDir, relative.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(path)) File.Delete(path);
    }

    protected string ReadRestored(string relative) =>
        File.ReadAllText(Path.Combine(RestoreDir, relative.Replace('/', Path.DirectorySeparatorChar)));

    protected bool RestoredExists(string relative) =>
        File.Exists(Path.Combine(RestoreDir, relative.Replace('/', Path.DirectorySeparatorChar)));

    protected void CleanRestoreDir()
    {
        if (Directory.Exists(RestoreDir)) Directory.Delete(RestoreDir, true);
        Directory.CreateDirectory(RestoreDir);
    }

    public void Dispose()
    {
        // Clean local temp dirs
        try { Directory.Delete(SourceDir, true); } catch { }
        try { Directory.Delete(RestoreDir, true); } catch { }

        // Clean registry artifacts
        foreach (var tag in _pushedTags)
        {
            try
            {
                var psi = new ProcessStartInfo("oras", $"manifest delete {Registry}:{tag} --force")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
            }
            catch { /* best effort */ }
        }

        GC.SuppressFinalize(this);
    }
}
