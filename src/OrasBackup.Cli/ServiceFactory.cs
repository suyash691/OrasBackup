using Microsoft.Extensions.Logging;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Backup;

namespace OrasBackup.Cli;

public interface IServiceFactory
{
    IOrasClient CreateOrasClient(string? registry = null, string? authToken = null);
    IEncryptor CreateEncryptor();
    IBackupEngine CreateBackupEngine(string? registry = null, string? authToken = null);
    IRestoreEngine CreateRestoreEngine(string? registry = null, string? authToken = null);
    IChunkEngine CreateChunkEngine(string? registry = null, string? authToken = null);
    IBackupIndexCache CreateBackupIndexCache();
    IProfileStore CreateProfileStore();
    byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config);
    ILogger<T> CreateLogger<T>();
}

public class DefaultServiceFactory : IServiceFactory
{
    private readonly Lazy<ILoggerFactory> _loggerFactory;
    private readonly Lazy<IEncryptor> _encryptor = new(() => new AesEncryptor());

    public DefaultServiceFactory(ILoggerFactory? loggerFactory = null)
    {
        _loggerFactory = new Lazy<ILoggerFactory>(() => loggerFactory ?? LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug)));
    }

    private HttpClient CreateHttpClient(string? registry = null)
    {
        var insecure = Environment.GetEnvironmentVariable("ORAS_INSECURE") == "true";
        var handler = insecure
            ? new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }
            : new HttpClientHandler();
        var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };

        // Determine base address: env var > registry host from profile
        var registryBase = Environment.GetEnvironmentVariable("ORAS_REGISTRY_URL")
            ?? Environment.GetEnvironmentVariable("ORAS_REGISTRY");

        if (string.IsNullOrEmpty(registryBase) && !string.IsNullOrEmpty(registry))
        {
            // Extract host from registry string (e.g. "ghcr.io/user/repo" → "ghcr.io")
            var cleaned = registry.Replace("https://", "").Replace("http://", "");
            var firstSlash = cleaned.IndexOf('/');
            registryBase = firstSlash > 0 ? cleaned[..firstSlash] : cleaned;
        }

        if (!string.IsNullOrEmpty(registryBase))
        {
            var scheme = insecure || registryBase.Contains("localhost") || registryBase.Contains("127.0.0.1")
                ? "http" : "https";
            http.BaseAddress = new Uri($"{scheme}://{registryBase}");
        }
        return http;
    }

    public virtual IOrasClient CreateOrasClient(string? registry = null, string? authToken = null) =>
        new HttpOrasClient(CreateHttpClient(registry), _loggerFactory.Value.CreateLogger<HttpOrasClient>(), authToken);
    public virtual IEncryptor CreateEncryptor() => _encryptor.Value;
    public virtual IBackupIndexCache CreateBackupIndexCache() => new BackupIndexCache();
    public virtual IProfileStore CreateProfileStore() => new FileProfileStore();

    public virtual IChunkEngine CreateChunkEngine(string? registry = null, string? authToken = null) =>
        new ChunkEngine(CreateOrasClient(registry, authToken), CreateEncryptor(), _loggerFactory.Value.CreateLogger<ChunkEngine>());

    public virtual IBackupEngine CreateBackupEngine(string? registry = null, string? authToken = null) =>
        new BackupEngine(new DeltaTracker(), CreateChunkEngine(registry, authToken), CreateOrasClient(registry, authToken),
            CreateEncryptor(), _loggerFactory.Value.CreateLogger<BackupEngine>());

    public virtual IRestoreEngine CreateRestoreEngine(string? registry = null, string? authToken = null) =>
        new RestoreEngine(CreateOrasClient(registry, authToken), CreateEncryptor(), _loggerFactory.Value.CreateLogger<RestoreEngine>());

    public virtual byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config) =>
        KeyHelper.Resolve(password, keyFile, config);

    public ILogger<T> CreateLogger<T>() => _loggerFactory.Value.CreateLogger<T>();
}
