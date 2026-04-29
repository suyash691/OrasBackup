using System.CommandLine;
using OrasBackup.Cli;
using Xunit;

namespace OrasBackup.Core.Tests;

public class AppCommandsTests
{
    private readonly RootCommand _root = AppCommands.Build();

    [Fact]
    public void Build_HasAllSubcommands()
    {
        var names = _root.Subcommands.Select(c => c.Name).ToList();
        Assert.Contains("init", names);
        Assert.Contains("backup", names);
        Assert.Contains("restore", names);
        Assert.Contains("list", names);
        Assert.Contains("daemon", names);
        Assert.Contains("compact", names);
        Assert.Equal(6, names.Count);
    }

    [Fact]
    public void Init_RequiresNameSourceRegistry()
    {
        var init = _root.Subcommands.Single(c => c.Name == "init");
        var opts = init.Options.Select(o => o.Name).ToList();
        Assert.Contains("--name", opts);
        Assert.Contains("--source", opts);
        Assert.Contains("--registry", opts);
    }

    [Fact]
    public void Backup_RequiresProfile()
    {
        var backup = _root.Subcommands.Single(c => c.Name == "backup");
        var profileOpt = backup.Options.Single(o => o.Name == "--profile");
        Assert.True(profileOpt.Required);
    }

    [Fact]
    public void Restore_RequiresProfileAndTarget()
    {
        var restore = _root.Subcommands.Single(c => c.Name == "restore");
        var required = restore.Options.Where(o => o.Required).Select(o => o.Name).ToList();
        Assert.Contains("--profile", required);
        Assert.Contains("--target", required);
    }

    [Fact]
    public void Restore_BackupIdIsOptional()
    {
        var restore = _root.Subcommands.Single(c => c.Name == "restore");
        var opt = restore.Options.SingleOrDefault(o => o.Name == "--backup-id");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void Daemon_HasIntervalOption()
    {
        var daemon = _root.Subcommands.Single(c => c.Name == "daemon");
        var opt = daemon.Options.SingleOrDefault(o => o.Name == "--interval");
        Assert.NotNull(opt);
    }

    [Fact]
    public void List_ProfileIsOptional()
    {
        var list = _root.Subcommands.Single(c => c.Name == "list");
        var opt = list.Options.SingleOrDefault(o => o.Name == "--profile");
        Assert.NotNull(opt);
        Assert.False(opt!.Required);
    }

    [Fact]
    public void Parse_BackupMissingProfile_HasError()
    {
        var result = _root.Parse("backup");
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void Parse_BackupWithProfile_NoError()
    {
        var result = _root.Parse("backup --profile myprof --password secret");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Build_AcceptsCustomFactory()
    {
        var root = AppCommands.Build(new DefaultServiceFactory());
        Assert.Equal(6, root.Subcommands.Count);
    }
}
