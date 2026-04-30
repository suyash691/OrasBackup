using Microsoft.Extensions.Logging;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Backup;

namespace OrasBackup.Cli;

public interface IServiceFactory
{
    IOrasClient CreateOrasClient();
    IEncryptor CreateEncryptor();
    IBackupEngine CreateBackupEngine();
    IRestoreEngine CreateRestoreEngine();
    IChunkEngine CreateChunkEngine();
    IBackupIndexCache CreateBackupIndexCache();
    IProfileStore CreateProfileStore();
    byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config);
    ILogger<T> CreateLogger<T>();
}

public class DefaultServiceFactory : IServiceFactory
{
    private readonly Lazy<ILoggerFactory> _loggerFactory = new(() => LoggerFactory.Create(b => b.AddConsole()));
    private readonly Lazy<HttpClient> _httpClient;
    private readonly Lazy<IOrasClient> _orasClient;
    private readonly Lazy<IEncryptor> _encryptor = new(() => new AesEncryptor());

    public DefaultServiceFactory()
    {
        _httpClient = new Lazy<HttpClient>(() =>
        {
            var registryBase = Environment.GetEnvironmentVariable("ORAS_REGISTRY_URL")
                ?? Environment.GetEnvironmentVariable("ORAS_REGISTRY");
            var insecure = Environment.GetEnvironmentVariable("ORAS_INSECURE") == "true";
            var handler = insecure
                ? new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true }
                : new HttpClientHandler();
            var http = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(30) };
            if (!string.IsNullOrEmpty(registryBase))
            {
                var scheme = insecure || registryBase.Contains("localhost") || registryBase.Contains("127.0.0.1")
                    ? "http" : "https";
                http.BaseAddress = new Uri($"{scheme}://{registryBase}");
            }
            return http;
        });
        _orasClient = new Lazy<IOrasClient>(() =>
            new HttpOrasClient(_httpClient.Value, _loggerFactory.Value.CreateLogger<HttpOrasClient>()));
    }

    public virtual IOrasClient CreateOrasClient() => _orasClient.Value;
    public virtual IEncryptor CreateEncryptor() => _encryptor.Value;
    public virtual IBackupIndexCache CreateBackupIndexCache() => new BackupIndexCache();
    public virtual IProfileStore CreateProfileStore() => new FileProfileStore();

    public virtual IChunkEngine CreateChunkEngine() =>
        new ChunkEngine(CreateOrasClient(), CreateEncryptor(), _loggerFactory.Value.CreateLogger<ChunkEngine>());

    public virtual IBackupEngine CreateBackupEngine() =>
        new BackupEngine(new DeltaTracker(), CreateChunkEngine(), CreateOrasClient(),
            CreateEncryptor(), _loggerFactory.Value.CreateLogger<BackupEngine>());

    public virtual IRestoreEngine CreateRestoreEngine() =>
        new RestoreEngine(CreateOrasClient(), CreateEncryptor(), _loggerFactory.Value.CreateLogger<RestoreEngine>());

    public virtual byte[] ResolveKey(string? password, string? keyFile, Core.Config.EncryptionConfig config) =>
        KeyHelper.Resolve(password, keyFile, config);

    public ILogger<T> CreateLogger<T>() => _loggerFactory.Value.CreateLogger<T>();
}
