# OrasBackup

[![CI](https://github.com/suyash691/OrasBackup/actions/workflows/ci.yml/badge.svg)](https://github.com/suyash691/OrasBackup/actions/workflows/ci.yml)
[![Integration Tests](https://github.com/suyash691/OrasBackup/actions/workflows/integration.yml/badge.svg)](https://github.com/suyash691/OrasBackup/actions/workflows/integration.yml)
[![codecov](https://codecov.io/gh/suyash691/OrasBackup/branch/main/graph/badge.svg?token=ZNH0JE6OPS)](https://codecov.io/gh/suyash691/OrasBackup)

Encrypted incremental backups to any OCI registry. No cloud vendor lock-in — works with GHCR, Docker Hub, self-hosted registries, whatever speaks OCI.

## What it does

- Graph-based chunked backups (files grouped by directory, each chunk is an OCI image)
- Per-file AES-256-GCM encryption with streaming 64MB chunks (constant memory regardless of file size)
- Unchanged directories skip entirely (content-hash comparison)
- Unchanged files within changed directories deduplicated by registry
- No delta chains, no compaction needed — each backup is self-contained
- Runs as CLI, daemon, Docker container, or GUI

## How it works

```
Your files:
  /data/documents/  → chunk image (50 files, 120MB)
  /data/photos/     → chunk image (200 files, 180MB)
  /data/videos/     → chunk image part-0 (2 files, 250MB)
                    → chunk image part-1 (1 file, 200MB)

Registry:
  ghcr.io/you/backups:20260429-140000-abc123   ← root index
    └── references chunk images by tag
  ghcr.io/you/backups:chunk-a1b2c3d4e5f6...    ← documents chunk
    ├── layer 0: chunk manifest (file list)
    ├── layer 1: encrypted file1.txt
    └── layer 2: encrypted file2.pdf
```

Next backup: only chunks with changed files get re-pushed. Unchanged chunks are referenced by their existing tag.

## Quick start

```bash
orasbackup init --name mybackup --source /path/to/data --registry ghcr.io/you/backups
orasbackup backup --profile mybackup --password hunter2
orasbackup restore --profile mybackup --target /tmp/restore --password hunter2
orasbackup daemon --profile mybackup --interval 60 --password hunter2
```

## Docker

```bash
cp .env.example .env  # edit with your registry and password
docker compose up -d
```

Everything configurable via env vars. See `.env.example`.

## Build

```bash
dotnet build
dotnet test
dotnet run --project src/OrasBackup.Cli -- --help
dotnet run --project src/OrasBackup.Gui
```

Requires .NET 10.

## Upgrade

**Docker/NAS:**
```bash
docker compose pull
docker compose up -d
```

**CLI binary:** Download the latest release from [GitHub Releases](../../releases), replace the old binary. No state migration needed — profiles, caches, and backup data are forward-compatible within the same major version.

**Pin a version:** Use a specific tag instead of `latest`:
```yaml
image: ghcr.io/suyash691/orasbackup:v1.5.0
```

## Known limitations

- **Glob patterns:** Supports `**`, `*`, `*.ext`, `prefix*`. Mid-segment wildcards like `test*.log` are not supported.
- **Docker passwords:** `ORASBACKUP_PASSWORD` is visible in `/proc/1/environ` inside the container. Use `--key-file` for higher security.
- **Large files:** Encrypted files are written to a temp file on `/scratch` before push. Ensure scratch volume has enough space for your largest single file.
