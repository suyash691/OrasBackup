using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrasBackup.Core.Scheduling;

public sealed class HealthServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly ILogger<HealthServer> _logger;
    private readonly object _lock = new();
    private volatile string _status = "starting";
    private DateTime _lastBackupUtc = DateTime.MinValue;

    public HealthServer(int port = 8080, ILogger<HealthServer>? logger = null)
    {
        _logger = logger ?? NullLogger<HealthServer>.Instance;
        _listener.Prefixes.Add($"http://*:{port}/");
    }

    public void UpdateStatus(bool success, string backupId)
    {
        lock (_lock)
        {
            _lastBackupUtc = DateTime.UtcNow;
            _status = success ? $"ok (last: {backupId} at {_lastBackupUtc:u})" : $"failed (last attempt: {_lastBackupUtc:u})";
        }
    }

    public string GetStatus()
    {
        lock (_lock)
        {
            return JsonSerializer.Serialize(new { status = _status, lastBackup = _lastBackupUtc.ToString("u") });
        }
    }

    public void Start()
    {
        _listener.Start();
        _ = ListenAsync();
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var ctx = await _listener.GetContextAsync();
                var isHealth = ctx.Request.Url?.AbsolutePath == "/healthz";
                var body = isHealth ? Encoding.UTF8.GetBytes(GetStatus()) : "{\"error\":\"not found\"}"u8.ToArray();
                ctx.Response.StatusCode = isHealth ? 200 : 404;
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = body.Length;
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            catch { _logger.LogWarning("Unexpected error in health server listen loop"); }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Stop();
        _listener.Close();
        _cts.Dispose();
    }
}
