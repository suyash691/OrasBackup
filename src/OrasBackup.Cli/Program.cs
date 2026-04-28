using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;

var profileOpt = new Option<string>("--profile", "Profile name") { IsRequired = true };
var passwordOpt = new Option<string?>("--password", "Encryption password");
var keyFileOpt = new Option<string?>("--key-file", "Path to 32-byte key file");

var root = new RootCommand("OrasBackup — encrypted incremental backups to OCI registries");

// --- init ---
var initCmd = new Command("init", "Create a new backup profile");
var nameOpt = new Option<string>("--name", "Profile name") { IsRequired = true };
var sourceOpt = new Option<string[]>("--source", "Source directories") { IsRequired = true, AllowMultipleArgumentsPerToken = true };
var registryOpt = new Option<string>("--registry", "OCI registry reference") { IsRequired = true };
initCmd.AddOption(nameOpt);
initCmd.AddOption(sourceOpt);
initCmd.AddOption(registryOpt);
initCmd.SetHandler((name, sources, registry) =>
{
    var profile = new BackupProfile
    {
        Name = name,
        SourcePaths = sources.ToList(),
        Registry = registry
    };
    ProfileHelper.Save(profile);
    Console.WriteLine($"Profile '{name}' created at {ProfileHelper.GetProfilePath(name)}");
}, nameOpt, sourceOpt, registryOpt);

// --- backup ---
var backupCmd = new Command("backup", "Run a backup");
backupCmd.AddOption(profileOpt);
backupCmd.AddOption(passwordOpt);
backupCmd.AddOption(keyFileOpt);
backupCmd.SetHandler(async (profile, password, keyFile) =>
{
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(password, keyFile, p.Encryption);
    var engine = BuildBackupEngine();
    var result = await engine.RunBackupAsync(p, key, null);
    if (result.Success)
        Console.WriteLine($"Backup {result.BackupId} complete: +{result.FilesAdded} ~{result.FilesModified} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)");
    else
        Console.Error.WriteLine($"Backup failed: {result.Error}");
}, profileOpt, passwordOpt, keyFileOpt);

// --- restore ---
var restoreCmd = new Command("restore", "Restore from a backup");
var targetOpt = new Option<string>("--target", "Target directory for restore") { IsRequired = true };
var backupIdOpt = new Option<string?>("--backup-id", "Specific backup ID (default: latest)");
restoreCmd.AddOption(profileOpt);
restoreCmd.AddOption(targetOpt);
restoreCmd.AddOption(backupIdOpt);
restoreCmd.AddOption(passwordOpt);
restoreCmd.AddOption(keyFileOpt);
restoreCmd.SetHandler(async (profile, target, backupId, password, keyFile) =>
{
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(password, keyFile, p.Encryption);
    var restoreEngine = BuildRestoreEngine();
    var opts = new RestoreOptions(p.Registry, backupId, target, key, p.Encryption.Enabled);
    await restoreEngine.RestoreAsync(opts);
    Console.WriteLine($"Restore complete to {target}");
}, profileOpt, targetOpt, backupIdOpt, passwordOpt, keyFileOpt);

// --- list ---
var listCmd = new Command("list", "List backups or profiles");
var listProfileOpt = new Option<string?>("--profile", "Profile name (omit to list all profiles)");
listCmd.AddOption(listProfileOpt);
listCmd.SetHandler(async (profile) =>
{
    if (string.IsNullOrEmpty(profile))
    {
        Console.WriteLine("Profiles:");
        foreach (var name in ProfileHelper.ListProfiles())
            Console.WriteLine($"  {name}");
        return;
    }
    var p = ProfileHelper.Load(profile);
    var oras = BuildOrasClient();
    var tags = await oras.ListTagsAsync(p.Registry);
    Console.WriteLine($"Backups for '{profile}':");
    foreach (var tag in tags)
        Console.WriteLine($"  {tag}");
}, listProfileOpt);

// --- daemon ---
var daemonCmd = new Command("daemon", "Run periodic backups in the foreground");
var intervalOpt = new Option<int>("--interval", () => 60, "Backup interval in minutes");
daemonCmd.AddOption(profileOpt);
daemonCmd.AddOption(intervalOpt);
daemonCmd.AddOption(passwordOpt);
daemonCmd.AddOption(keyFileOpt);
daemonCmd.SetHandler(async (profile, interval, password, keyFile) =>
{
    var p = ProfileHelper.Load(profile);
    p.Schedule.IntervalMinutes = interval;
    var key = KeyHelper.Resolve(password, keyFile, p.Encryption);

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

    var engine = BuildBackupEngine();
    using var scheduler = new BackupScheduler(engine, LoggerFactory.Create(b => b.AddConsole()).CreateLogger<BackupScheduler>());

    Console.WriteLine($"Daemon started: backing up '{profile}' every {interval}m. Press Ctrl+C to stop.");
    try { await scheduler.RunAsync(p, key, cts.Token); }
    catch (OperationCanceledException) { Console.WriteLine("Daemon stopped."); }
}, profileOpt, intervalOpt, passwordOpt, keyFileOpt);

root.AddCommand(initCmd);
root.AddCommand(backupCmd);
root.AddCommand(restoreCmd);
root.AddCommand(listCmd);
root.AddCommand(daemonCmd);

// --- compact ---
var compactCmd = new Command("compact", "Compact the delta chain into a single full backup");
compactCmd.AddOption(profileOpt);
compactCmd.AddOption(passwordOpt);
compactCmd.AddOption(keyFileOpt);
compactCmd.SetHandler(async (profile, password, keyFile) =>
{
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(password, keyFile, p.Encryption);
    var engine = BuildCompactionEngine();
    var result = await engine.CompactAsync(p, key);
    if (result.Success)
        Console.WriteLine($"Compaction complete: new full backup {result.BackupId} ({result.FilesAdded} files, {result.Duration.TotalSeconds:F1}s)");
    else
        Console.Error.WriteLine($"Compaction failed: {result.Error}");
}, profileOpt, passwordOpt, keyFileOpt);
root.AddCommand(compactCmd);

return await root.InvokeAsync(args);

// --- factory helpers ---
static ILoggerFactory Logs() => LoggerFactory.Create(b => b.AddConsole());

static BackupEngine BuildBackupEngine()
{
    var lf = Logs();
    return new BackupEngine(new DeltaTracker(), new AesEncryptor(), BuildOrasClient(), lf.CreateLogger<BackupEngine>());
}

static RestoreEngine BuildRestoreEngine()
{
    var lf = Logs();
    return new RestoreEngine(new AesEncryptor(), BuildOrasClient(), lf.CreateLogger<RestoreEngine>());
}

static OrasClient BuildOrasClient() => new(Logs().CreateLogger<OrasClient>());

static CompactionEngine BuildCompactionEngine()
{
    var lf = Logs();
    return new CompactionEngine(BuildRestoreEngine(), BuildBackupEngine(), lf.CreateLogger<CompactionEngine>());
}
