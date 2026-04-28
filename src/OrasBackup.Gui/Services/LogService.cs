using System.Collections.ObjectModel;

namespace OrasBackup.Gui.Services;

public sealed class LogService
{
    public ObservableCollection<string> Entries { get; } = [];

    public void Log(string message)
    {
        var entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
        try
        {
            if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess())
                Entries.Add(entry);
            else
                Avalonia.Threading.Dispatcher.UIThread.Post(() => Entries.Add(entry));
        }
        catch (InvalidOperationException)
        {
            // No dispatcher available (unit tests without Avalonia) — add directly
            Entries.Add(entry);
        }
    }
}
