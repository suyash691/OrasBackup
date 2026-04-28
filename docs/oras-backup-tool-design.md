# OrasBackup — Design Document

**Author:** suyash691
**Date:** 2026-04-29
**Status:** Implemented

---

## Architecture: Graph-Based Chunked Backups

Each backup produces a root index image pointing to chunk images. Each chunk groups files by directory and stores them as individual encrypted OCI layers.

```
Root Index Image (ghcr.io/you/backups:20260429-140000-abc123)
├── ChunkRef → "documents" (chunk image)
│   ├── ChunkManifest (layer 0)
│   ├── encrypted file1.txt (layer 1)
│   └── encrypted file2.pdf (layer 2)
├── ChunkRef → "photos/2024" (chunk image)
│   ├── ChunkManifest (layer 0)
│   └── encrypted img1.jpg (layer 1)
└── ChunkRef → "videos/part-0" (chunk image, split from large dir)
    ├── ChunkManifest (layer 0)
    └── encrypted movie.mp4 (layer 1)
```

## Chunking Algorithm

1. Scan all source directories, compute SHA-256 per file
2. Group files by top-level directory
3. Directories > 256MB → split into `part-0`, `part-1`, etc.
4. Directories < 10MB → merge with neighbors
5. Each chunk becomes one OCI image

## Incremental Behavior

- Each chunk has a deterministic content hash (SHA-256 of all file hashes)
- On subsequent backups, compare chunk hash with previous BackupIndex
- Unchanged chunks → skip entirely (zero bandwidth)
- Changed chunks → re-push (registry deduplicates unchanged file blobs)

No delta chains. No compaction. Each backup is self-contained.

## Encryption

- Algorithm: AES-256-GCM
- Key derivation: PBKDF2 (600k iterations)
- Streaming: files encrypted in 64MB chunks, each with unique nonce + auth tag
- Memory: constant 64MB regardless of file size
- Wire format: `[4-byte chunk size][nonce|ciphertext|tag][nonce|ciphertext|tag]...`

## Retention

Simplified from v1 — no chain walking needed. Each backup is self-contained.
Delete old root index tags by count. Chunk images with no remaining references
are garbage-collected by the registry.

## Components

| Component | Purpose |
|---|---|
| `DirectoryChunker` | Groups files into chunks by directory tree |
| `ChunkEngine` | Encrypts per-file, pushes chunk as OCI image |
| `BackupEngine` | Orchestrates scan → chunk → push |
| `RestoreEngine` | Pulls index → pulls chunks → extracts files |
| `BackupIndexCache` | Persists BackupIndex between runs for incremental |
| `HttpOrasClient` | Native OCI HTTP client with retry (429/500/503) |
| `AesEncryptor` | Streaming chunk-based AES-256-GCM |
| `HealthServer` | HTTP /healthz for Docker monitoring |

## CLI Commands

```
orasbackup init     --name <n> --source <dir> --registry <ref>
orasbackup backup   --profile <n> [--password | --key-file]
orasbackup restore  --profile <n> --target <dir> [--backup-id] [--password | --key-file]
orasbackup list     [--profile <n>]
orasbackup daemon   --profile <n> [--interval 60] [--password | --key-file]
```

## Docker Deployment

- `TMPDIR=/scratch` for temp files during encryption
- `ORASBACKUP_PASSWORD` or `ORASBACKUP_KEY_FILE` for encryption
- `ORAS_PAT` or `ORAS_USERNAME`/`ORAS_PASSWORD` for registry auth
- Health endpoint: `http://container:8080/healthz`
