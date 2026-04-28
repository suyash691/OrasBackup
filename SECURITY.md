# Security Policy

## Security/Bugfix Versions

Security and bug fixes are generally provided only for the last minor version.
Fixes are released either as part of the next minor version or as an on-demand patch version.

Security fixes are given priority and might be enough to cause a new version to be released.

## Reporting a Vulnerability

Responsible disclosure of security vulnerabilities is encouraged.
If you find something suspicious, we encourage and appreciate your report!

### Ways to report

In order for the vulnerability reports to reach maintainers as soon as possible, the preferred way is to use the "Report a vulnerability" button under the "Security" tab of the associated GitHub project.
This creates a private communication channel between the reporter and the maintainers.

## Known Limitations

- **Per-chunk authentication, not per-backup:** AES-256-GCM authenticates each encryption chunk individually. There is no MAC or signature over the entire backup index. A registry-level attacker with write access could theoretically swap chunk references between backups. This is acceptable for the target use case (personal backups to trusted registries).
- **Passwords in managed memory:** Encryption passwords are held in .NET managed strings which cannot be zeroed. Use `--key-file` for higher security.
- **Docker env var visibility:** `ORASBACKUP_PASSWORD` is visible in `/proc/1/environ` inside the container. Use `--key-file` mounted as a Docker secret for production deployments.
