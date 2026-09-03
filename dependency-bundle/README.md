# Dependency bundle (generated — not committed)

This folder is populated by `scripts/staging/Export-DependencyBundle.ps1` in the **connected
staging** environment and transferred to the **air-gapped** environment by the organization's
approved media-transfer process. Nothing in it is source-controlled except this file.

Expected layout after export:

```
dependency-bundle/
  packages/            every .nupkg the lock files name (direct and transitive)
  prerequisites/       .NET SDK installer/archive, ASP.NET Core Hosting Bundle, as supplied
  manifest.json        machine-readable manifest: name, version, file, SHA-256, origin, date,
                       licence, classification (runtime/build/test), audit status
  MANIFEST.sha256      plain `sha256sum -c` list of every file above
  audit-report.txt     output of the NuGet vulnerability audit run at export time
  lockfiles/           copies of the packages.lock.json files the bundle was built from
```

**Verify before use:** `scripts/airgap/Verify-DependencyBundle.ps1` (or
`scripts/airgap/verify-dependency-bundle.sh`). The offline restore script refuses to run until
verification passes. See `docs/air-gapped-build-and-maintenance.md`.
