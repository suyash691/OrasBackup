using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrasBackup.Cli;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;

var profileOpt = new Option<string>("--profile") { Required = true, Description = "Profile name" };
var passwordOpt = new Option<string?>("--password") { Description = "Encryption password" };
var keyFileOpt = new Option<string?>("--key-file") { Description = "Path to 32-byte key file" };

var root = new RootCommand("OrasBackup — encrypted incremental backups to OCI registries");

// --- init ---
var nameOpt = new Option<string>("--name") { Required = true, Description = "Profile name" };
var sourceOpt = new Option<string[]>("--source") { Required = true, Description = "Source directories", AllowMultipleArgumentsPerToken = true };
var registryOpt = new Option<string>("--registry") { Required = true, Description = "OCI registry reference" };
var initCmd = new Command("init", "Create a new backup profile") { nameOpt, sourceOpt, registryOpt };
initCmd.SetAction(parseResult =>
{
    var name = parseResult.GetValue(nameOpt)!;
    var sources = parseResult.GetValue(sourceOpt)!;
    var registry = parseResult.GetValue(registryOpt)!;
    var profile = new BackupProfile { Name = name, SourcePaths = sources.ToList(), Registry = registry };
    ProfileHelper.Save(profile);
    Console.WriteLine($"Profile '{name}' created at {ProfileHelper.GetProfilePath(name)}");
});

// --- backup ---
var backupCmd = new Command("backup", "Run a backup") { profileOpt, passwordOpt, keyFileOpt };
backupCmd.SetAction(async (parseResult, ct) =>
{
    var profile = parseResult.GetValue(profileOpt)!;
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(parseResult.GetValue(passwordOpt), parseResult.GetValue(keyFileOpt), p.Encryption);
    var cache = new ManifestCache();
    var previous = cache.Load(profile);
    var engine = BuildBackupEngine();
    var result = await engine.RunBackupAsync(p, key, previous, ct);
    if (result.Success)
    {
        cache.Save(profile, engine.LastManifest!);

        // Retention: prune old backups and auto-compact if chain is long
        var retention = new RetentionEnforcer(BuildOrasClient());
        await retention.EnforceAsync(p.Registry, p.Retention, ct);

        // Count chain length from manifest
        var chainLen = 0;
        var m = engine.LastManifest;
        while (m?.BasedOn != null) { chainLen++; m = previous?.BackupId == m.BasedOn ? previous : null; }
        if (retention.ShouldCompact(chainLen, p.Retention.CompactAfter))
        {
            Console.WriteLine("Chain length exceeded threshold, compacting...");
            var compacted = await BuildCompactionEngine().CompactAsync(p, key, ct);
            if (compacted.Success)
            {
                cache.Save(profile, new Delta.DeltaManifest { BackupId = compacted.BackupId, Files = engine.LastManifest!.Files });
                Console.WriteLine($"Auto-compacted to {compacted.BackupId}");
            }
        }

        Console.WriteLine($"Backup {result.BackupId} complete: +{result.FilesAdded} ~{result.FilesModified} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)");
    }
    else
        Console.Error.WriteLine($"Backup failed: {result.Error}");
});

// --- restore ---
var targetOpt = new Option<string>("--target") { Required = true, Description = "Target directory for restore" };
var backupIdOpt = new Option<string?>("--backup-id") { Description = "Specific backup ID (default: latest)" };
var restoreCmd = new Command("restore", "Restore from a backup") { profileOpt, targetOpt, backupIdOpt, passwordOpt, keyFileOpt };
restoreCmd.SetAction(async (parseResult, ct) =>
{
    var profile = parseResult.GetValue(profileOpt)!;
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(parseResult.GetValue(passwordOpt), parseResult.GetValue(keyFileOpt), p.Encryption);
    var opts = new RestoreOptions(p.Registry, parseResult.GetValue(backupIdOpt), parseResult.GetValue(targetOpt)!, key, p.Encryption.Enabled);
    await BuildRestoreEngine().RestoreAsync(opts, ct);
    Console.WriteLine($"Restore complete to {opts.TargetDir}");
});

// --- list ---
var listProfileOpt = new Option<string?>("--profile") { Description = "Profile name (omit to list all profiles)" };
var listCmd = new Command("list", "List backups or profiles") { listProfileOpt };
listCmd.SetAction(async (parseResult, ct) =>
{
    var profile = parseResult.GetValue(listProfileOpt);
    if (string.IsNullOrEmpty(profile))
    {
        Console.WriteLine("Profiles:");
        foreach (var name in ProfileHelper.ListProfiles())
            Console.WriteLine($"  {name}");
        return;
    }
    var p = ProfileHelper.Load(profile);
    var tags = await BuildOrasClient().ListTagsAsync(p.Registry, ct);
    Console.WriteLine($"Backups for '{profile}':");
    foreach (var tag in tags)
        Console.WriteLine($"  {tag}");
});

// --- daemon ---
var intervalOpt = new Option<int>("--interval") { Description = "Backup interval in minutes", DefaultValueFactory = _ => 60 };
var daemonCmd = new Command("daemon", "Run periodic backups in the foreground") { profileOpt, intervalOpt, passwordOpt, keyFileOpt };
daemonCmd.SetAction(async (parseResult, ct) =>
{
    var profile = parseResult.GetValue(profileOpt)!;
    var p = ProfileHelper.Load(profile);
    p.Schedule.IntervalMinutes = parseResult.GetValue(intervalOpt);
    var key = KeyHelper.Resolve(parseResult.GetValue(passwordOpt), parseResult.GetValue(keyFileOpt), p.Encryption);

    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    using var health = new HealthServer();
    try { health.Start(); Console.WriteLine("Health endpoint: http://0.0.0.0:8080/healthz"); }
    catch { Console.WriteLine("Health endpoint unavailable (port 8080 in use or no permission)"); }

    var engine = BuildBackupEngine();
    using var scheduler = new BackupScheduler(engine, LoggerFactory.Create(b => b.AddConsole()).CreateLogger<BackupScheduler>(), health);

    Console.WriteLine($"Daemon started: backing up '{profile}' every {p.Schedule.IntervalMinutes}m. Press Ctrl+C to stop.");
    try { await scheduler.RunAsync(p, key, cts.Token); }
    catch (OperationCanceledException) { Console.WriteLine("Daemon stopped."); }
});

// --- compact ---
var compactCmd = new Command("compact", "Compact the delta chain into a single full backup") { profileOpt, passwordOpt, keyFileOpt };
compactCmd.SetAction(async (parseResult, ct) =>
{
    var profile = parseResult.GetValue(profileOpt)!;
    var p = ProfileHelper.Load(profile);
    var key = KeyHelper.Resolve(parseResult.GetValue(passwordOpt), parseResult.GetValue(keyFileOpt), p.Encryption);
    var result = await BuildCompactionEngine().CompactAsync(p, key, ct);
    if (result.Success)
        Console.WriteLine($"Compaction complete: new full backup {result.BackupId} ({result.FilesAdded} files, {result.Duration.TotalSeconds:F1}s)");
    else
        Console.Error.WriteLine($"Compaction failed: {result.Error}");
});

root.Subcommands.Add(initCmd);
root.Subcommands.Add(backupCmd);
root.Subcommands.Add(restoreCmd);
root.Subcommands.Add(listCmd);
root.Subcommands.Add(daemonCmd);
root.Subcommands.Add(compactCmd);

return await root.Parse(args).InvokeAsync();

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
