using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using NSubstitute;
using OrasBackup.Cli;
using OrasBackup.Core.Config;
using OrasBackup.Gui.Services;
using OrasBackup.Gui.ViewModels;
using OrasBackup.Gui.Views;
using Xunit;

namespace OrasBackup.Gui.Tests;

public class HeadlessUiTests
{
    [AvaloniaFact]
    public void MainWindow_HasFourTabs()
    {
        var window = new MainWindow();
        window.Show();

        var tabControl = window.FindDescendantOfType<TabControl>();
        Assert.NotNull(tabControl);
        Assert.Equal(4, tabControl!.Items.Count);
    }

    [AvaloniaFact]
    public void MainWindow_HasCorrectTitle()
    {
        var window = new MainWindow();
        window.Show();
        Assert.Equal("OrasBackup", window.Title);
    }

    [AvaloniaFact]
    public void MainWindow_DataContextIsMainViewModel()
    {
        var window = new MainWindow();
        window.Show();
        Assert.IsType<MainViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public void MainViewModel_HasAllSubViewModels()
    {
        var vm = new MainViewModel();
        Assert.NotNull(vm.Profiles);
        Assert.NotNull(vm.Dashboard);
        Assert.NotNull(vm.Restore);
        Assert.NotNull(vm.Log);
    }

    // --- Per-view tests: instantiate each view directly ---

    [AvaloniaFact]
    public void ProfileManagerView_HasCreateAndDeleteButtons()
    {
        var svc = Substitute.For<IServiceFactory>();
        svc.CreateProfileStore().Returns(Substitute.For<IProfileStore>());
        svc.CreateProfileStore().ListProfiles().Returns(Array.Empty<string>());
        var view = new ProfileManagerView { DataContext = new ProfileManagerViewModel(svc, new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("Create", buttons);
        Assert.Contains("Delete", buttons);
    }

    [AvaloniaFact]
    public void DashboardView_HasRunBackupButton()
    {
        var svc = Substitute.For<IServiceFactory>();
        var view = new DashboardView { DataContext = new DashboardViewModel(svc, new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("Run Backup", buttons);
    }

    [AvaloniaFact]
    public void RestoreView_HasLoadAndRestoreButtons()
    {
        var svc = Substitute.For<IServiceFactory>();
        var view = new RestoreView { DataContext = new RestoreViewModel(svc, new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("Load Backups", buttons);
        Assert.Contains("Restore", buttons);
    }

    [AvaloniaFact]
    public void LogView_HasClearButton()
    {
        var view = new LogView { DataContext = new LogViewModel(new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var buttons = window.GetVisualDescendants().OfType<Button>().Select(b => b.Content?.ToString()).ToList();
        Assert.Contains("Clear", buttons);
    }

    [AvaloniaFact]
    public void ProfileManagerView_HasThreeTextBoxes()
    {
        var svc = Substitute.For<IServiceFactory>();
        svc.CreateProfileStore().Returns(Substitute.For<IProfileStore>());
        svc.CreateProfileStore().ListProfiles().Returns(Array.Empty<string>());
        var view = new ProfileManagerView { DataContext = new ProfileManagerViewModel(svc, new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var textBoxes = window.GetVisualDescendants().OfType<TextBox>().ToList();
        Assert.True(textBoxes.Count >= 3);
    }

    [AvaloniaFact]
    public void DashboardView_HasPasswordField()
    {
        var svc = Substitute.For<IServiceFactory>();
        var view = new DashboardView { DataContext = new DashboardViewModel(svc, new LogService()) };
        var window = new Window { Content = view };
        window.Show();

        var passwordBoxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.PasswordChar != default).ToList();
        Assert.Single(passwordBoxes);
    }
}
