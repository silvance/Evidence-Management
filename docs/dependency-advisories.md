# Dependency Advisories

The build audits every dependency, including transitives, at every severity
(`NuGetAudit` / `NuGetAuditMode=all` / `NuGetAuditLevel=low` in `Directory.Build.props`).

**NU1903 is reported as a warning, not an error, and that is a deliberate choice.** One current
advisory has no patched release on the .NET 8 LTS band at all, so treating advisories as errors
would mean either an unbuildable repository or a blanket `NoWarn` that would hide *future*
advisories too. Instead, every open advisory is assessed here.

Targeted per-advisory suppression (`NuGetAuditSuppress`) requires the **.NET 9 SDK**. When the
toolchain moves, adopt it and restore NU1903 to error severity — then this file becomes a record
of suppressions rather than a substitute for them.

## Process

1. **Any new NU1903 in a build must be assessed and recorded here before it is merged.**
2. Review this file before every deployment.
3. Re-check for patched releases whenever dependencies are updated.
4. This file is a *risk register*, not an *exemption*. An advisory recorded here is still open.

## Open advisories

### GHSA-2p3q-h3hg-jcqq and GHSA-8prm-248r-h957 — `Microsoft.AspNetCore.Authentication.Negotiate`

| | |
|---|---|
| **Severity** | High |
| **Affected** | All `8.0.x` releases available at time of writing (checked through 8.0.25) |
| **Fixed in** | No patched release exists on the .NET 8 band |
| **Reachable from EMC?** | **Requires assessment — see below** |
| **Status** | **OPEN — action required before deployment** |

**Why the package is present.** Windows Authentication (Negotiate/Kerberos) is the authentication
mechanism (`docs/architecture.md` §8, IAM-003). EMC deliberately stores no passwords, so this is
not a dependency that can simply be dropped.

**Assessment.** These advisories concern the Negotiate handler. EMC uses the handler only to
establish the Windows identity; **roles and all authorization decisions are resolved
server-side from EMC's own database** (IAM-002, `EvidenceAuthorizationService`), and EMC does not
use the handler's LDAP-based role retrieval. That narrows the exposure but **does not by itself
clear it.**

**Required before deployment — this is a decision for the organization, not for the application:**

1. Read both advisories against the deployed configuration and confirm the affected code path is
   not reachable in EMC's usage.
2. Prefer moving to a .NET release where the package is patched, if the target environment allows
   it. Retargeting is a small change; the dependency direction and code do not depend on the
   framework version.
3. If neither is possible, obtain a documented risk acceptance from the organization's security
   authority and record the reference here.

Because EMC's authentication surface is exactly one mechanism, this advisory should be closed out
deliberately rather than carried indefinitely. **Do not deploy to an Army environment without
completing step 1.**

## Closed

### GHSA-2m69-gcr7-jv3q — `SQLitePCLRaw.lib.e_sqlite3`

| | |
|---|---|
| **Severity** | High |
| **Resolution** | Closed twice over |

1. **Removed from production entirely.** SQLite was pulled into `Emc.Infrastructure` only by a
   provider-detection helper (`Database.IsSqlite()`). That check now reads
   `Database.ProviderName` instead, which needs no package reference, so **no production assembly
   references SQLite at all**. SQL Server is the only production provider.
2. **Updated where it remains.** The test projects still use SQLite in-memory for relational
   integration tests, pinned to `SQLitePCLRaw.bundle_e_sqlite3` 3.0.0, which carries the fix.

Test-only dependencies are not shipped, but they still run on developer machines and build
agents, so they are updated rather than excused.
