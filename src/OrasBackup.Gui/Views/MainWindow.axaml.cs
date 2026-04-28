using Avalonia.Controls;
using OrasBackup.Gui.ViewModels;

namespace OrasBackup.Gui.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }
}
