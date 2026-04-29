using System.Net;
using System.Text;

namespace OrasBackup.Core.Scheduling;

/// <summary>
/// Minimal HTTP health endpoint for Docker/NAS monitoring.
/// Responds to GET /healthz with 200 OK and last backup status.
/// </summary>
public sealed class HealthServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private string _status = "starting";
    private DateTime _lastBackupUtc = DateTime.MinValue;

    public HealthServer(int port = 8080)
    {
        _listener.Prefixes.Add($"http://+:{port}/");
    }

    public void UpdateStatus(bool success, string backupId)
    {
        _lastBackupUtc = DateTime.UtcNow;
        _status = success ? $"ok (last: {backupId} at {_lastBackupUtc:u})" : $"failed (last attempt: {_lastBackupUtc:u})";
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
                var response = ctx.Response;
                var body = Encoding.UTF8.GetBytes($"{{\"status\":\"{_status}\",\"lastBackup\":\"{_lastBackupUtc:u}\"}}");
                response.StatusCode = 200;
                response.ContentType = "application/json";
                response.ContentLength64 = body.Length;
                await response.OutputStream.WriteAsync(body);
                response.Close();
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
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
