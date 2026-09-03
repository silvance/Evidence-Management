# Dependency Advisories

The build audits every dependency, including transitives, at every severity
(`NuGetAudit` / `NuGetAuditMode=all` / `NuGetAuditLevel=low` in `Directory.Build.props`).

**A finding fails the build.** NU1903 is at error severity.

## Current status

**No vulnerable packages, direct or transitive, in any project.**

Verify with:

```
dotnet build                                   # fails on any advisory
dotnet list <project> package --vulnerable --include-transitive
```

## Where the audit runs

The audit runs where there is data to audit against: the **connected staging environment**, at
dependency-bundle export (`scripts/staging/Export-DependencyBundle.ps1`), which **fails on any
finding** and writes `audit-report.txt` into the bundle. The **air-gapped build** restores only
the exact locked packages that audit covered and sets `EMC_OFFLINE=true`, which turns `NuGetAudit`
off for that build because a folder of packages is not an audit source. **An offline build never
claims to have audited.** Details: `docs/air-gapped-build-and-maintenance.md`.

## Process

1. **Any new NU1903 must be resolved, or assessed and recorded here, before it is merged.**
2. Review this file before every deployment.
3. Re-check for patched releases whenever dependencies are updated.
4. An advisory recorded here as accepted is still open. This is a risk register, not an exemption.

For each open advisory, record: the advisory, the affected package and version, **whether the
vulnerable code path is reachable from EMC**, the mitigation, and the deployment consequence.

## Resolved

### GHSA-2p3q-h3hg-jcqq and GHSA-8prm-248r-h957 — `Microsoft.AspNetCore.Authentication.Negotiate`

| | |
|---|---|
| **Severity** | High |
| **Was affected** | All `8.0.x` releases (checked through 8.0.25) — **no patched release existed on the .NET 8 band** |
| **Resolved by** | Retargeting to .NET 10 LTS; `Microsoft.AspNetCore.Authentication.Negotiate` **10.0.11** carries the fix |

This was the reason NU1903 was previously demoted to a warning: with no patched release available,
treating it as an error meant either an unbuildable repository or a blanket `NoWarn` that would
have hidden future advisories. The .NET 10 retarget removed the dilemma, and **error severity has
been restored**.

Windows Authentication is EMC's only authentication mechanism (IAM-003), so this dependency cannot
simply be dropped — which is why it was worth resolving properly rather than accepting.

### GHSA-2m69-gcr7-jv3q — `SQLitePCLRaw.lib.e_sqlite3`

| | |
|---|---|
| **Severity** | High |
| **Resolved by** | Removed from production entirely, and updated where it remains |

1. SQLite was pulled into `Emc.Infrastructure` only by a provider-detection helper
   (`Database.IsSqlite()`). That check now reads `Database.ProviderName`, which needs no package
   reference, so **no production assembly references SQLite at all**.
2. The test projects still use SQLite in-memory for relational integration tests, pinned to
   `SQLitePCLRaw.bundle_e_sqlite3` 3.0.0.

### `System.Net.Http` 4.3.0 and `System.Text.RegularExpressions` 4.3.0

Stale transitives of the .NET 8 test tooling, previously pinned to patched versions. **.NET 10
provides both in-box and prunes the references entirely** (NU1510), so the pins were removed.

## Framework support

| | |
|---|---|
| **Target** | .NET 10 LTS |
| **Support ends** | November 2028 |
| **Previous target** | .NET 8 LTS, support ended 10 November 2026 |

Retargeting was a security action, not a modernisation exercise: it resolved a high-severity
advisory that had no fix on the previous band, and moved the application off a runtime that is now
out of support.
