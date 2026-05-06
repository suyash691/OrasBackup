using System.CommandLine;
using Microsoft.Extensions.Logging;
using OrasBackup.Core.Scheduling;
using OrasBackup.Core.Backup;

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
            var cache = svc.CreateBackupIndexCache();
            var previous = cache.Load(profileName);
            var engine = svc.CreateBackupEngine(p.AuthToken);
            var result = await engine.RunBackupAsync(p, key, previous, ct);
            if (result.Success)
            {
                cache.Save(profileName, engine.LastIndex!);

                // Simplified retention: delete oldest tags by count (each backup is self-contained)
                await EnforceRetentionAsync(svc, p.Registry, p.Retention.MaxBackups, key, p.Encryption.Enabled, ct);

                Console.WriteLine($"Backup {result.BackupId} complete: +{result.FilesAdded} -{result.FilesDeleted} ({result.Duration.TotalSeconds:F1}s)");
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
            var backupId = parseResult.GetValue(backupIdOpt);
            var target = parseResult.GetValue(targetOpt)!;
            await svc.CreateRestoreEngine(p.AuthToken).RestoreAsync(p.Registry, backupId, target, key, p.Encryption.Enabled, ct);
            Console.WriteLine($"Restore complete to {target}");
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
            var tags = await svc.CreateOrasClient(p.AuthToken).ListTagsAsync(p.Registry, ct);
            Console.WriteLine($"Backups for '{profile}':");
            foreach (var tag in tags)
                Console.WriteLine($"  {tag}");
        });
        return cmd;
    }

    private static Command BuildDaemon(IServiceFactory svc)
    {
        var intervalOpt = new Option<int>("--interval") { DefaultValueFactory = _ => 60 };
        var healthPortOpt = new Option<int>("--health-port") { DefaultValueFactory = _ => 8080, Description = "Health endpoint port" };
        var cmd = new Command("daemon", "Run periodic backups in the foreground") { ProfileOpt, intervalOpt, healthPortOpt, PasswordOpt, KeyFileOpt };
        cmd.SetAction(async (parseResult, ct) =>
        {
            var profileName = parseResult.GetValue(ProfileOpt)!;
            var p = svc.CreateProfileStore().Load(profileName);
            p.Schedule.IntervalMinutes = parseResult.GetValue(intervalOpt);
            var healthPort = parseResult.GetValue(healthPortOpt);
            var key = svc.ResolveKey(parseResult.GetValue(PasswordOpt), parseResult.GetValue(KeyFileOpt), p.Encryption);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

            using var health = new HealthServer(healthPort);
            try { health.Start(); Console.WriteLine($"Health endpoint: http://0.0.0.0:{healthPort}/healthz"); }
            catch (Exception ex) { Console.WriteLine($"Health endpoint unavailable: {ex.Message}"); }

            var engine = svc.CreateBackupEngine(p.AuthToken);
            var cache = svc.CreateBackupIndexCache();
            using var scheduler = new BackupScheduler(engine, svc.CreateLogger<BackupScheduler>(), health, cache,
                async (profile, key, ct) => await EnforceRetentionAsync(svc, profile.Registry,
                    profile.Retention.MaxBackups, key, profile.Encryption.Enabled, ct));

            Console.WriteLine($"Daemon started: backing up '{profileName}' every {p.Schedule.IntervalMinutes}m. Press Ctrl+C to stop.");
            try { await scheduler.RunAsync(p, key, cts.Token); }
            catch (OperationCanceledException) { Console.WriteLine("Daemon stopped."); }
        });
        return cmd;
    }

    /// <summary>
    /// Simplified retention: delete oldest backup tags by count, then GC orphaned chunk images.
    /// </summary>
    public static async Task EnforceRetentionAsync(IServiceFactory svc, string registry, int maxBackups,
        byte[]? encryptionKey, bool encrypted, CancellationToken ct, string? authToken = null)
    {
        var oras = svc.CreateOrasClient(authToken);
        var allTags = await oras.ListTagsAsync(registry, ct);
        var backupTags = allTags.Where(t => t != "latest" && !t.StartsWith("chunk-")).OrderBy(t => t).ToList();

        if (backupTags.Count <= maxBackups) return;

        var toDelete = backupTags.Take(backupTags.Count - maxBackups).ToList();
        foreach (var tag in toDelete)
            await oras.DeleteTagAsync(registry, tag, ct);

        // Re-list tags after deletion to pick up any backups pushed concurrently
        var freshTags = await oras.ListTagsAsync(registry, ct);
        var remainingTags = freshTags.Where(t => t != "latest" && !t.StartsWith("chunk-")).ToList();
        var referencedChunks = new HashSet<string>();
        var encryptor = encrypted ? svc.CreateEncryptor() : null;

        foreach (var tag in remainingTags)
        {
            try
            {
                var layers = await oras.FetchManifestLayersAsync($"{registry}:{tag}", ct);
                var indexLayer = layers.FirstOrDefault(l => l.MediaType.Contains("index"));
                if (indexLayer == null) continue;
                var data = await oras.PullLayerAsync($"{registry}:{tag}", indexLayer.Digest, ct);
                if (encrypted && encryptor != null && encryptionKey != null)
                    data = encryptor.Decrypt(data, encryptionKey);
                var index = BackupIndex.Deserialize(data);
                foreach (var c in index.Chunks) referencedChunks.Add(c.Tag);
            }
            catch { /* can't read index — skip this tag but continue GC */ continue; }
        }

        // Delete unreferenced chunk tags
        var chunkTags = freshTags.Where(t => t.StartsWith("chunk-")).ToList();
        foreach (var chunk in chunkTags.Where(c => !referencedChunks.Contains(c)))
            await oras.DeleteTagAsync(registry, chunk, ct);
    }
}
