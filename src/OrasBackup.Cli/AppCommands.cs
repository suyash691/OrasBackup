using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Backup;
using OrasBackup.Core.Delta;
using OrasBackup.Core.Scheduling;

namespace OrasBackup.Cli;

public static class AppCommands
{
    public static readonly Option<string> ProfileOpt = new("--profile") { Required = true, Description = "Profile name" };
    public static readonly Option<string?> PasswordOpt = new("--password") { Description = "Encryption password" };
    public static readonly Option<string?> KeyFileOpt = new("--key-file") { Description = "Path to 32-byte key file" };

    public static RootCommand Build(IServiceFactory? factory = null)
    {
        var svc = factory ?? new DefaultServiceFactory();
        var root = new RootCommand("OrasBackup — encrypted incremental backups to OCI registries");
        root.Subcommands.Add(BuildInit(svc));
        root.Subcommands.Add(BuildBackup(svc));
        root.Subcommands.Add(BuildRestore(svc));
        root.Subcommands.Add(BuildList(svc));
        root.Subcommands.Add(BuildDaemon(svc));
        root.Subcommands.Add(BuildCompact(svc));
        return root;
    }

    private static Command BuildInit(IServiceFactory svc)
    {
        var nameOpt = new Option<string>("--name") { Required = true };
        var sourceOpt = new Option<string[]>("--source") { Required = true, AllowMultipleArgumentsPerToken = true };
        var registryOpt = new Option<string>("--registry") { Required = true };
        var cmd = new Command("init", "Create a new backup profile") { nameOpt, sourceOpt, registryOpt };
        cmd.SetAction(parseResult =>
        {
            var store = svc.CreateProfileStore();
            var profile = new Core.Config.BackupProfile
            {
                Name = parseResult.GetValue(nameOpt)!,
                SourcePaths = parseResult.GetValue(sourceOpt)!.ToList(),
                Registry = parseResult.GetValue(registryOpt)!
            };
            store.Save(profile);
            Console.WriteLine($"Profile '{profile.Name}' created at {store.GetProfilePath(profile.Name)}");
        });
        return cmd;
    }

    private static Command BuildBackup(IServiceFactory svc)
    {
        var cmd = new Command("backup", "Run a backup") { ProfileOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profileName = parseResult.GetValue(ProfileOpt)!;
            var store = svc.CreateProfileStore();
            var p = store.Load(profileName);
            var key = svc.ResolveKey(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var cache = svc.CreateManifestCache();
            var previous = cache.Load(profileName);
            var engine = svc.CreateBackupEngine();
            var result = await engine.RunBackupAsync(p, key, previous, ct);
            if (result.Success)
            {
                cache.Save(profileName, engine.LastManifest!);

                var retention = svc.CreateRetentionEnforcer();
                await retention.EnforceAsync(p.Registry, p.Retention, ct);

                var chainLen = ChainCounter.Count(engine.LastManifest!, _ => previous);
                if (retention.ShouldCompact(chainLen, p.Retention.CompactAfter))
                {
                    Console.WriteLine("Chain length exceeded threshold, compacting...");
                    var compacted = await svc.CreateCompactionEngine().CompactAsync(p, key, ct);
                    if (compacted.Success)
                    {
                        cache.Save(profileName, new DeltaManifest { BackupId = compacted.BackupId, Files = engine.LastManifest!.Files });
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

    private static Command BuildRestore(IServiceFactory svc)
    {
        var targetOpt = new Option<string>("--target") { Required = true };
        var backupIdOpt = new Option<string?>("--backup-id");
        var cmd = new Command("restore", "Restore from a backup") { ProfileOpt, targetOpt, backupIdOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var p = svc.CreateProfileStore().Load(parseResult.GetValue(ProfileOpt)!);
            var key = svc.ResolveKey(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var opts = new RestoreOptions(p.Registry, parseResult.GetValue(backupIdOpt), parseResult.GetValue(targetOpt)!, key, p.Encryption.Enabled);
            await svc.CreateRestoreEngine().RestoreAsync(opts, ct);
            Console.WriteLine($"Restore complete to {opts.TargetDir}");
        });
        return cmd;
    }

    private static Command BuildList(IServiceFactory svc)
    {
        var listProfileOpt = new Option<string?>("--profile");
        var cmd = new Command("list", "List backups or profiles") { listProfileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profile = parseResult.GetValue(listProfileOpt);
            if (string.IsNullOrEmpty(profile))
            {
                Console.WriteLine("Profiles:");
                foreach (var name in svc.CreateProfileStore().ListProfiles())
                    Console.WriteLine($"  {name}");
                return;
            }
            var p = svc.CreateProfileStore().Load(profile);
            var tags = await svc.CreateOrasClient().ListTagsAsync(p.Registry, ct);
            Console.WriteLine($"Backups for '{profile}':");
            foreach (var tag in tags)
                Console.WriteLine($"  {tag}");
        });
        return cmd;
    }

    private static Command BuildDaemon(IServiceFactory svc)
    {
        var intervalOpt = new Option<int>("--interval") { DefaultValueFactory = _ => 60 };
        var cmd = new Command("daemon", "Run periodic backups in the foreground") { ProfileOpt, intervalOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profileName = parseResult.GetValue(ProfileOpt)!;
            var p = svc.CreateProfileStore().Load(profileName);
            p.Schedule.IntervalMinutes = parseResult.GetValue(intervalOpt);
            var key = svc.ResolveKey(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            using var health = new HealthServer();
            try { health.Start(); Console.WriteLine("Health endpoint: http://0.0.0.0:8080/healthz"); }
            catch { Console.WriteLine("Health endpoint unavailable"); }

            var engine = svc.CreateBackupEngine();
            var lf = LoggerFactory.Create(b => b.AddConsole());
            var retention = svc.CreateRetentionEnforcer();
            using var scheduler = new BackupScheduler(engine, lf.CreateLogger<BackupScheduler>(), health, retention);

            Console.WriteLine($"Daemon started: backing up '{profileName}' every {p.Schedule.IntervalMinutes}m. Press Ctrl+C to stop.");
            try { await scheduler.RunAsync(p, key, cts.Token); }
            catch (OperationCanceledException) { Console.WriteLine("Daemon stopped."); }
        });
        return cmd;
    }

    private static Command BuildCompact(IServiceFactory svc)
    {
        var cmd = new Command("compact", "Compact the delta chain into a single full backup") { ProfileOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var p = svc.CreateProfileStore().Load(parseResult.GetValue(ProfileOpt)!);
            var key = svc.ResolveKey(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);
            var result = await svc.CreateCompactionEngine().CompactAsync(p, key, ct);
            if (result.Success)
                Console.WriteLine($"Compaction complete: new full backup {result.BackupId} ({result.FilesAdded} files, {result.Duration.TotalSeconds:F1}s)");
            else
                Console.Error.WriteLine($"Compaction failed: {result.Error}");
        });
        return cmd;
    }
}
