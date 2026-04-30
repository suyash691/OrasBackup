using System.Collections.ObjectModel;

namespace OrasBackup.Gui.Services;

public sealed class LogService
{
    private readonly Action<Action>? _dispatcher;

    public ObservableCollection<string> Entries { get; } = [];

    /// <param name="dispatcher">
    /// UI thread dispatcher. Pass null for direct execution (tests, CLI).
    /// In production, pass: action => Avalonia.Threading.Dispatcher.UIThread.Post(action)
    /// </param>
    public LogService(Action<Action>? dispatcher = null) => _dispatcher = dispatcher;

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (_dispatcher != null)
            _dispatcher(() => Entries.Add(entry));
        else
            Entries.Add(entry);
    }
}
