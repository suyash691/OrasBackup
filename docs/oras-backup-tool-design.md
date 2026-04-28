# ORAS Backup Tool — Design Document

**Author:** suyash691
**Date:** 2026-04-28
**Status:** Draft

---

## 1. Problem Statement

We need a cross-platform backup tool that:
- Stores backups as OCI artifacts in any OCI-compliant container registry via ORAS
- Supports both CLI and GUI interfaces
- Encrypts backups at rest and decrypts on restore
- Performs delta-based (incremental) backups — only new/changed files are pushed, deleted files are tracked
- Runs as a lightweight background daemon for periodic scheduled backups

---

## 2. High-Level Architecture

```
┌─────────────────────────────────────────────────────────┐
│                    Presentation Layer                    │
│  ┌──────────────┐  ┌──────────────┐  ┌───────────────┐ │
│  │  CLI (Verb)   │  │  CLI Daemon  │  │ GUI (Avalonia)│ │
│  │ backup/restore│  │  (periodic)  │  │               │ │
│  └──────┬───────┘  └──────┬───────┘  └───────┬───────┘ │
│         │                 │                   │         │
├─────────┴─────────────────┴───────────────────┴─────────┤
│                      Core Library                       │
│  ┌─────────────┐ ┌──────────────┐ ┌───────────┐        │
│  │BackupEngine │ │ DeltaTracker │ │ Encryption│        │
│  └──────┬──────┘ └──────┬───────┘ └─────┬─────┘        │
│         │               │               │              │
│  ┌──────┴───────────────┴───────────────┴──────┐       │
│  │           OrasClient (Push/Pull)            │       │
│  └─────────────────┬───────────────────────────┘       │
├────────────────────┴────────────────────────────────────┤
│              OCI-Compliant Container Registry            │
│             (OCI Artifacts via ORAS protocol)           │
└─────────────────────────────────────────────────────────┘
```

---

## 3. Solution Structure

```
OrasBackup/
├── OrasBackup.sln
├── src/
│   ├── OrasBackup.Core/            # Shared library — all business logic
│   │   ├── Backup/
│   │   │   ├── BackupEngine.cs
│   │   │   ├── RestoreEngine.cs
│   │   │   └── BackupManifest.cs
│   │   ├── Delta/
│   │   │   ├── DeltaTracker.cs
│   │   │   ├── FileSnapshot.cs
│   │   │   └── DeltaManifest.cs
│   │   ├── Crypto/
│   │   │   ├── AesEncryptor.cs
│   │   │   └── IEncryptor.cs
│   │   ├── Oras/
│   │   │   ├── OrasClient.cs
│   │   │   └── OrasConfig.cs
│   │   ├── Scheduling/
│   │   │   └── BackupScheduler.cs
│   │   └── Config/
│   │       └── BackupProfile.cs
│   ├── OrasBackup.Cli/             # CLI + daemon mode
│   │   └── Program.cs
│   └── OrasBackup.Gui/             # Avalonia UI
│       ├── App.axaml
│       └── MainWindow.axaml
└── tests/
    ├── OrasBackup.Core.Tests/
    └── OrasBackup.Cli.Tests/
```

---

## 4. Component Design

### 4.1 Delta Tracking

Delta tracking avoids re-uploading the entire backup set each run. A local manifest (JSON) records the state of the last successful backup.

**FileSnapshot:**
```json
{
  "relativePath": "docs/readme.md",
  "sha256": "abc123...",
  "sizeBytes": 4096,
  "lastModifiedUtc": "2026-04-28T12:00:00Z"
}
```

**DeltaManifest** (stored locally and pushed as a layer):
```json
{
  "backupId": "guid",
  "timestamp": "2026-04-28T18:00:00Z",
  "basedOn": "previous-backup-guid or null",
  "files": [ "...FileSnapshot entries..." ],
  "deleted": [ "old/removed-file.txt" ]
}
```

**Delta resolution algorithm:**
1. Scan source directory, compute SHA-256 for each file.
2. Load previous DeltaManifest (from local cache or pulled from registry).
3. Compare:
   - **Added/Modified:** hash differs or file is new → include in this backup's layers.
   - **Deleted:** file in previous manifest but not on disk → record in `deleted` list.
   - **Unchanged:** hash matches → skip (referenced from previous manifest).
4. Push only changed layers + new manifest.

### 4.2 OCI Artifact Layout

Each backup is an OCI artifact pushed via ORAS. The tag scheme encodes the backup chain.

```
<registry>/<repository>/<profile-name>:<backup-id>
```

**Manifest layers:**

| Layer mediaType | Content |
|---|---|
| `application/vnd.orasbackup.manifest+json` | DeltaManifest (encrypted) |
| `application/vnd.orasbackup.layer.v1.tar+encrypted` | Tar of changed files (encrypted) |

A full restore walks the chain: pull the latest manifest, resolve `basedOn` references back to the initial full backup, then overlay layers in order.

### 4.3 Encryption

All data layers and manifests are encrypted before push and decrypted after pull.

- **Algorithm:** AES-256-GCM
- **Key derivation:** PBKDF2 (password-based) or direct key file
- **Per-layer IV:** Each layer gets a unique 12-byte nonce, prepended to the ciphertext
- **Format:** `[12-byte nonce][ciphertext][16-byte GCM tag]`

```csharp
public interface IEncryptor
{
    byte[] Encrypt(byte[] plaintext, byte[] key);
    byte[] Decrypt(byte[] ciphertext, byte[] key);
    byte[] DeriveKey(string password, byte[] salt);
}
```

Key is never stored in the registry. The user provides it via `--key-file` or `--password` (CLI) or a prompt (GUI). Without the key, backup data is unreadable.

### 4.4 Backup Engine

```csharp
public class BackupEngine
{
    Task<BackupResult> RunBackupAsync(BackupProfile profile, CancellationToken ct);
}
```

**Flow:**
1. Load profile (source paths, registry target, schedule, encryption config).
2. `DeltaTracker.ComputeDelta()` → list of added/modified/deleted files.
3. Tar the changed files into a byte stream.
4. `IEncryptor.Encrypt()` the tar stream and the manifest.
5. `OrasClient.PushAsync()` — push layers to the OCI registry.
6. Save DeltaManifest locally for next run.

### 4.5 Restore Engine

```csharp
public class RestoreEngine
{
    Task RestoreAsync(RestoreOptions options, CancellationToken ct);
}
```

**Flow:**
1. `OrasClient.PullManifestAsync()` — pull the target backup's DeltaManifest.
2. Walk the `basedOn` chain to collect all manifests in order.
3. For each manifest (oldest → newest):
   - Pull and decrypt the data layer.
   - Extract files to the restore target directory.
4. Apply deletions from each manifest's `deleted` list in order.
5. Final state = point-in-time snapshot of the original source.

### 4.6 ORAS Client

Wraps the `oras` CLI or uses the OCI distribution spec HTTP API directly.

**Option A — Shell out to `oras` CLI:**
- Simpler, leverages existing registry auth (`oras login` / credential helpers).
- Downside: runtime dependency on `oras` binary.

**Option B — Native HTTP client against OCI Distribution API:**
- No external dependency. Uses `HttpClient` to push/pull blobs and manifests.
- More work but fully self-contained.

**Recommendation:** Start with Option A for speed, abstract behind `IOrasClient` so we can swap to Option B later.

```csharp
public interface IOrasClient
{
    Task PushAsync(string reference, IReadOnlyList<OrasLayer> layers, CancellationToken ct);
    Task<Stream> PullLayerAsync(string reference, string digest, CancellationToken ct);
    Task<OrasManifest> PullManifestAsync(string reference, CancellationToken ct);
}
```

### 4.7 CLI Design

Uses `System.CommandLine` for verb-based CLI.

```
orasbackup backup   --profile <name> [--password <pw> | --key-file <path>]
orasbackup restore  --profile <name> --target <dir> [--backup-id <id>] [--password | --key-file]
orasbackup daemon   --profile <name> [--interval 1h] [--password | --key-file]
orasbackup list     --profile <name>
orasbackup init     --name <profile-name> --source <dir> --registry <registry-ref>
```

**Daemon mode (`daemon`):**
- Runs in foreground (suitable for systemd/launchd/Windows Task Scheduler).
- Uses a `Timer`-based scheduler. On each tick: run `BackupEngine.RunBackupAsync()`.
- Logs to stdout/file. Exits cleanly on SIGTERM/Ctrl+C.

### 4.8 GUI Design

**Framework:** Avalonia UI (cross-platform: Windows, macOS, Linux).

**Screens:**
1. **Profile Manager** — create/edit backup profiles (source dir, registry, schedule, encryption).
2. **Backup Dashboard** — shows last backup status, next scheduled run, file counts.
3. **Restore Wizard** — pick profile → pick backup point → pick target dir → restore.
4. **Log Viewer** — scrollable log of backup/restore operations.

The GUI calls the same `OrasBackup.Core` library. No business logic in the GUI layer.

---

## 5. Configuration / Backup Profile

Stored as JSON in `~/.orasbackup/profiles/<name>.json`:

```json
{
  "name": "my-project",
  "sourcePaths": ["/home/user/projects/myapp"],
  "excludePatterns": ["**/node_modules", "**/.git", "**/bin", "**/obj"],
  "registry": "registry.example.com/myuser/backups/my-project",
  "schedule": {
    "enabled": true,
    "intervalMinutes": 60
  },
  "encryption": {
    "enabled": true,
    "keyDerivation": "pbkdf2",
    "pbkdf2Iterations": 600000
  },
  "retention": {
    "maxBackups": 50,
    "compactAfter": 10
  }
}
```

---

## 6. Delta Chain Compaction

Over time, the delta chain grows long, making restores slower (must replay N layers). Compaction merges a chain into a single full snapshot.

**Trigger:** After `retention.compactAfter` incremental backups, or on manual `orasbackup compact --profile <name>`.

**Process:**
1. Restore the full chain into a temp directory.
2. Create a new full backup (no `basedOn`).
3. Push it with a new backup ID.
4. Update the latest tag to point to the compacted backup.
5. Old layers can be garbage-collected (or left for history).

---

## 7. Authentication

Registry authentication options (in priority order):
1. `ORAS_AUTH_TOKEN` or registry-specific environment variable.
2. Docker credential store (`~/.docker/config.json`).
3. Interactive login via `orasbackup login` (wraps `oras login`).

The `OrasClient` reads credentials from these sources. No credentials are stored by the tool itself.

---

## 8. Cross-Platform Considerations

| Concern | Approach |
|---|---|
| Runtime | .NET 8+ (single-file publish, AOT where possible) |
| GUI | Avalonia UI (renders natively on Win/macOS/Linux) |
| File paths | Use `Path.Combine`, normalize separators in manifests to `/` |
| Permissions | Store POSIX permissions in FileSnapshot on Linux/macOS, skip on Windows |
| Daemon | Foreground process; user wraps with systemd / launchd / Windows Task Scheduler |
| Line endings | Binary tar — no conversion needed |

---

## 9. Testing Strategy

### Unit Tests (OrasBackup.Core.Tests)

| Area | What's tested |
|---|---|
| `DeltaTracker` | Detects added, modified, deleted, unchanged files correctly |
| `AesEncryptor` | Round-trip encrypt/decrypt, wrong-key rejection, unique IVs |
| `BackupManifest` | Serialization/deserialization, chain walking |
| `BackupEngine` | Orchestration with mocked `IOrasClient` and `IEncryptor` |
| `RestoreEngine` | Multi-layer overlay, deletion application, ordering |
| `BackupScheduler` | Timer fires at correct intervals, cancellation works |

### Integration Tests

- Push/pull to a local OCI registry (e.g., `zot` or `registry:2` in Docker).
- Full backup → incremental → incremental → restore → verify file equality.
- Compaction → restore → verify.

### CLI Tests (OrasBackup.Cli.Tests)

- Argument parsing for each verb.
- Daemon graceful shutdown on cancellation token.

---

## 10. Dependencies

| Package | Purpose |
|---|---|
| `System.CommandLine` | CLI argument parsing |
| `Avalonia` | Cross-platform GUI |
| `System.Security.Cryptography` | AES-256-GCM (built-in, no external dep) |
| `System.Formats.Tar` | Tar archive creation/extraction (.NET 7+) |
| `System.Text.Json` | Manifest serialization |
| `Microsoft.Extensions.Logging` | Structured logging |
| `Microsoft.Extensions.Hosting` | Background service hosting for daemon mode |

No external crypto libraries needed — .NET's `AesGcm` class covers AES-256-GCM natively.

---

## 11. Security Considerations

- **Encryption keys never leave the client.** They are not stored in the registry or in profiles.
- **PBKDF2 with 600k iterations** for password-based key derivation (OWASP 2024 recommendation).
- **Unique 12-byte nonce per layer** — GCM nonce reuse would be catastrophic; we generate via `RandomNumberGenerator`.
- **Manifest is encrypted too** — file names and structure are not visible in the registry without the key.
- **Registry credentials** — the tool relies on the registry's own auth mechanism. Ensure tokens have minimal required scope (push/pull).
- **No secrets in logs** — passwords/keys are never logged.

---

## 12. Docker / NAS Deployment

The CLI daemon mode is a natural fit for running as a Docker container on a NAS (Synology, Unraid, TrueNAS, etc.).

### Dockerfile

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0-alpine AS base

FROM mcr.microsoft.com/dotnet/sdk:8.0-alpine AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/OrasBackup.Cli -c Release -o /app --self-contained false

FROM base
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "OrasBackup.Cli.dll", "daemon"]
```

### Volume Mounts

| Mount | Purpose |
|---|---|
| `/data` | Source directory to back up (read-only is fine) |
| `/config` | Profiles + local delta manifests (persistent) |

### Environment Variables

| Variable | Purpose |
|---|---|
| `ORAS_AUTH_TOKEN` | Registry authentication token |
| `ORASBACKUP_PASSWORD` | Encryption password (alternative to key file) |
| `ORASBACKUP_PROFILE` | Profile name to run (default: `default`) |
| `ORASBACKUP_INTERVAL` | Backup interval (default: `1h`) |

### Example `docker-compose.yml`

```yaml
services:
  orasbackup:
    image: orasbackup:latest
    container_name: orasbackup
    restart: unless-stopped
    environment:
      - ORAS_AUTH_TOKEN=${ORAS_AUTH_TOKEN}
      - ORASBACKUP_PASSWORD=${BACKUP_PASSWORD}
      - ORASBACKUP_PROFILE=nas-docs
      - ORASBACKUP_INTERVAL=6h
    volumes:
      - /volume1/documents:/data:ro
      - orasbackup-config:/config

volumes:
  orasbackup-config:
```

### Multi-Architecture

Publish for `linux/amd64` and `linux/arm64` to cover x86 NAS boxes (Synology, Unraid) and ARM ones (some QNAP, Raspberry Pi setups):

```bash
docker buildx build --platform linux/amd64,linux/arm64 -t orasbackup:latest --push .
```

### NAS-Specific Notes

- **Synology:** Deploy via Container Manager (Docker Compose UI). Mount shared folders as `/data`.
- **Unraid:** Add as a Community Applications template or manual Docker container.
- **TrueNAS:** Deploy as a custom app or via Docker Compose in a sandbox.
- **Health check:** The daemon exposes a simple HTTP health endpoint on port 8080 (`/healthz`) so Docker/NAS UIs can monitor it.
- **Logging:** Stdout logging works with `docker logs`. Optionally mount a `/logs` volume for persistent log files.

---

## 13. Open Questions

1. **Should we support multiple registries per profile?** (e.g., push to a primary + secondary registry for redundancy)
2. **Compression before encryption?** Tar + zstd before AES would reduce layer sizes, but leaks information about plaintext entropy. Acceptable tradeoff?
3. **Conflict resolution** — if two daemon instances back up the same profile concurrently, how do we handle manifest conflicts? (Likely: advisory lock file + tag-based optimistic concurrency.)
4. **Large file chunking** — should files over a threshold (e.g., 100MB) be split into separate layers for better dedup?

---

## 14. Milestones

| Phase | Scope | Target |
|---|---|---|
| **M1** | Core library: delta tracking, encryption, ORAS push/pull (shelling out). Unit tests. | 2 weeks |
| **M2** | CLI: all verbs, daemon mode with scheduler. CLI tests. | 1 week |
| **M3** | Integration tests with local OCI registry. Compaction. | 1 week |
| **M4** | Avalonia GUI: profile manager, backup dashboard, restore wizard. | 2 weeks |
| **M5** | Native OCI HTTP client (replace shell-out). Polish, docs. | 2 weeks |
