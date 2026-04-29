using Microsoft.Extensions.Logging;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;

namespace OrasBackup.Cli;

public interface IServiceFactory
{
    IOrasClient CreateOrasClient();
    IEncryptor CreateEncryptor();
    BackupEngine CreateBackupEngine();
    RestoreEngine CreateRestoreEngine();
    CompactionEngine CreateCompactionEngine();
    RetentionEnforcer CreateRetentionEnforcer();
    ManifestCache CreateManifestCache();
    IProfileStore CreateProfileStore();
    byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config);
}

public class DefaultServiceFactory : IServiceFactory
{
    private ILoggerFactory Logs() => LoggerFactory.Create(b => b.AddConsole());

    public virtual IOrasClient CreateOrasClient() => new OrasClient(Logs().CreateLogger<OrasClient>());
    public virtual IEncryptor CreateEncryptor() => new AesEncryptor();
    public virtual ManifestCache CreateManifestCache() => new();
    public virtual IProfileStore CreateProfileStore() => new FileProfileStore();
    public virtual RetentionEnforcer CreateRetentionEnforcer() => new(CreateOrasClient());

    public virtual BackupEngine CreateBackupEngine() =>
        new(new DeltaTracker(), CreateEncryptor(), CreateOrasClient(), Logs().CreateLogger<BackupEngine>());

    public virtual RestoreEngine CreateRestoreEngine() =>
        new(CreateEncryptor(), CreateOrasClient(), Logs().CreateLogger<RestoreEngine>());

    public virtual CompactionEngine CreateCompactionEngine() =>
        new(CreateRestoreEngine(), CreateBackupEngine(), Logs().CreateLogger<CompactionEngine>());

    public virtual byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config) =>
        KeyHelper.Resolve(password, keyFile, config);
}
