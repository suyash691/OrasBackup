using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Config;
using OrasBackup.Core.Crypto;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Oras;
using OrasBackup.Core.Scheduling;

namespace OrasBackup.Cli;

public static class AppCommands
{
    public static readonly Option<string> ProfileOpt = new("--profile") { Required = true, Description = "Profile name" };
    public static readonly Option<string?> PasswordOpt = new("--password") { Description = "Encryption password" };
    public static readonly Option<string?> KeyFileOpt = new("--key-file") { Description = "Path to 32-byte key file" };

    public static RootCommand Build()
    {
        var root = new RootCommand("OrasBackup — encrypted incremental backups to OCI registries");
        root.Subcommands.Add(BuildInit());
        root.Subcommands.Add(BuildBackup());
        root.Subcommands.Add(BuildRestore());
        root.Subcommands.Add(BuildList());
        root.Subcommands.Add(BuildDaemon());
        root.Subcommands.Add(BuildCompact());
        return root;
    }

    private static Command BuildInit()
    {
        var nameOpt = new Option<string>("--name") { Required = true, Description = "Profile name" };
        var sourceOpt = new Option<string[]>("--source") { Required = true, Description = "Source directories", AllowMultipleArgumentsPerToken = true };
        var registryOpt = new Option<string>("--registry") { Required = true, Description = "OCI registry reference" };
        var cmd = new Command("init", "Create a new backup profile") { nameOpt, sourceOpt, registryOpt };
        cmd.SetAction(parseResult =>
        {
            var name = parseResult.GetValue(nameOpt)!;
            var sources = parseResult.GetValue(sourceOpt)!;
            var registry = parseResult.GetValue(registryOpt)!;
            ProfileHelper.Save(new BackupProfile { Name = name, SourcePaths = sources.ToList(), Registry = registry });
            Console.WriteLine($"Profile '{name}' created at {ProfileHelper.GetProfilePath(name)}");
        });
        return cmd;
    }

    private static Command BuildBackup()
    {
        var cmd = new Command("backup", "Run a backup") { ProfileOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(ProfileOpt)!;
            var p = ProfileHelper.Load(profile);
            var key = KeyHelper.Resolve(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var cache = new ManifestCache();
            var previous = cache.Load(profile);
            var engine = CreateBackupEngine();
            var result = await engine.RunBackupAsync(p, key, previous, ct);
            if (result.Success)
            {
                cache.Save(profile, engine.LastManifest!);

                var retention = new RetentionEnforcer(CreateOrasClient());
                await retention.EnforceAsync(p.Registry, p.Retention, ct);

                var chainLen = 0;
                var m = engine.LastManifest;
                while (m?.BasedOn != null) { chainLen++; m = previous?.BackupId == m.BasedOn ? previous : null; }
                if (retention.ShouldCompact(chainLen, p.Retention.CompactAfter))
                {
                    Console.WriteLine("Chain length exceeded threshold, compacting...");
                    var compacted = await CreateCompactionEngine().CompactAsync(p, key, ct);
                    if (compacted.Success)
                    {
                        cache.Save(profile, new DeltaManifest { BackupId = compacted.BackupId, Files = engine.LastManifest!.Files });
                        Console.WriteLine($"Auto-compacted to {compacted.BackupId}");
                    }
                }

                Console.WriteLine($"Backup {result.BackupId} complete: +{result.FilesAdded} ~{result.FilesModified} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)");
            }
            else
                Console.Error.WriteLine($"Backup failed: {result.Error}");
        });
        return cmd;
    }

    private static Command BuildRestore()
    {
        var targetOpt = new Option<string>("--target") { Required = true, Description = "Target directory for restore" };
        var backupIdOpt = new Option<string?>("--backup-id") { Description = "Specific backup ID (default: latest)" };
        var cmd = new Command("restore", "Restore from a backup") { ProfileOpt, targetOpt, backupIdOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(ProfileOpt)!;
            var p = ProfileHelper.Load(profile);
            var key = KeyHelper.Resolve(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var opts = new RestoreOptions(p.Registry, parseResult.GetValue(backupIdOpt), parseResult.GetValue(targetOpt)!, key, p.Encryption.Enabled);
            await CreateRestoreEngine().RestoreAsync(opts, ct);
            Console.WriteLine($"Restore complete to {opts.TargetDir}");
        });
        return cmd;
    }

    private static Command BuildList()
    {
        var listProfileOpt = new Option<string?>("--profile") { Description = "Profile name (omit to list all profiles)" };
        var cmd = new Command("list", "List backups or profiles") { listProfileOpt };
        cmd.SetAction(async (parseResult, ct) =>
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
            var tags = await CreateOrasClient().ListTagsAsync(p.Registry, ct);
            Console.WriteLine($"Backups for '{profile}':");
            foreach (var tag in tags)
                Console.WriteLine($"  {tag}");
        });
        return cmd;
    }

    private static Command BuildDaemon()
    {
        var intervalOpt = new Option<int>("--interval") { Description = "Backup interval in minutes", DefaultValueFactory = _ => 60 };
        var cmd = new Command("daemon", "Run periodic backups in the foreground") { ProfileOpt, intervalOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(ProfileOpt)!;
            var p = ProfileHelper.Load(profile);
            p.Schedule.IntervalMinutes = parseResult.GetValue(intervalOpt);
            var key = KeyHelper.Resolve(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            using var health = new HealthServer();
            try { health.Start(); Console.WriteLine("Health endpoint: http://0.0.0.0:8080/healthz"); }
            catch { Console.WriteLine("Health endpoint unavailable (port 8080 in use or no permission)"); }

            var engine = CreateBackupEngine();
            using var scheduler = new BackupScheduler(engine, LoggerFactory.Create(b => b.AddConsole()).CreateLogger<BackupScheduler>(), health);

            Console.WriteLine($"Daemon started: backing up '{profile}' every {p.Schedule.IntervalMinutes}m. Press Ctrl+C to stop.");
            try { await scheduler.RunAsync(p, key, cts.Token); }
            catch (OperationCanceledException) { Console.WriteLine("Daemon stopped."); }
        });
        return cmd;
    }

    private static Command BuildCompact()
    {
        var cmd = new Command("compact", "Compact the delta chain into a single full backup") { ProfileOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(ProfileOpt)!;
            var p = ProfileHelper.Load(profile);
            var key = KeyHelper.Resolve(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var result = await CreateCompactionEngine().CompactAsync(p, key, ct);
            if (result.Success)
                Console.WriteLine($"Compaction complete: new full backup {result.BackupId} ({result.FilesAdded} files, {result.Duration.TotalSeconds:F1}s)");
            else
                Console.Error.WriteLine($"Compaction failed: {result.Error}");
        });
        return cmd;
    }

    // --- factory helpers (internal for testing) ---
    internal static ILoggerFactory Logs() => LoggerFactory.Create(b => b.AddConsole());
    internal static BackupEngine CreateBackupEngine() => new(new DeltaTracker(), new AesEncryptor(), CreateOrasClient(), Logs().CreateLogger<BackupEngine>());
    internal static RestoreEngine CreateRestoreEngine() => new(new AesEncryptor(), CreateOrasClient(), Logs().CreateLogger<RestoreEngine>());
    internal static OrasClient CreateOrasClient() => new(Logs().CreateLogger<OrasClient>());
    internal static CompactionEngine CreateCompactionEngine() => new(CreateRestoreEngine(), CreateBackupEngine(), Logs().CreateLogger<CompactionEngine>());
}
