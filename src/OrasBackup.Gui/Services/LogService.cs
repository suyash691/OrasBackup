using System.Collections.ObjectModel;

namespace OrasBackup.Gui.Services;

public sealed class LogService
{
    public ObservableCollection<string> Entries { get; } = [];

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        Entries.Add(entry);
    }
}
