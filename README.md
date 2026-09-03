# Evidence Management Companion (EMC)

An internal Army Counterintelligence evidence-accountability **companion** application.

> **What this is.** An application that assists the AR 195-5 evidence process.
>
> **What it is not.** The authoritative evidence ledger, the authoritative DA Form 4137, or a
> substitute for the signatures AR 195-5 requires.

## The constraint that shapes the whole design

**AR 195-5 para 2-5c** distinguishes two kinds of software:

| | Approval required | EMC V1? |
|---|---|---|
| A **stand-alone** automated evidence ledger / accountability system | Yes — **Army G-2X** for CI organizations, prior to use | **No** |
| A system **"used in conjunction with or to enhance the requirements of this regulation"** | **No approval required** | **Yes** |

EMC V1 is the second. Three consequences are enforced structurally, not by convention:

1. **EMC does not assign the evidence document number.** Para 2-4c makes that the custodian's
   act, performed by order of precedence from the evidence ledger, which para 2-5a keeps a bound
   book absent approval. EMC issues `TMP-20260903-A014` until a custodian records `037-26` with
   an explicit attestation that the ledger assigned it.
2. **EMC does not produce signatures.** Paras 2-5b(2), 2-7b, 3-1b(2) and 2-8e(5) require
   handwritten signatures. EMC records *that a paper certification was executed*. That record is
   not a signature, and the UI never calls it one.
3. **EMC does not erase history.** Para 2-5b(5) requires an erroneous entry to be struck through
   with one line *"so it may still be read"* and initialed. EMC's equivalent is a correction that
   preserves the original.

The domain is designed so that an approved automated equivalent could be enabled by configuration
(`AuthoritativeMode`, `NumberingMode`) rather than by rewriting the model. **That path is designed,
not built or tested, in V1** — V1 does not assume approval and does not exercise it.

## Documentation

| Document | Contents |
|---|---|
| [`docs/regulatory-requirements.md`](docs/regulatory-requirements.md) | AR 195-5 extract with paragraph references, organized around the CI variant. **§12 lists claims EMC must *not* make** — several useful features are design decisions, not regulatory mandates |
| [`docs/architecture.md`](docs/architecture.md) | Stack, layering, the event model, three-layer append-only enforcement, the hash chain, authorization, security posture, deployment |
| [`docs/domain-model.md`](docs/domain-model.md) | Entities, four independent state axes, immutable vs mutable data, 41 numbered invariants |
| [`docs/requirements-traceability.md`](docs/requirements-traceability.md) | Requirement IDs → AR 195-5 paragraph → type → status → the tests that prove them. **A blank regulation column is meaningful**: it marks a design decision. Every cited test name is checked to exist |
| [`docs/open-policy-decisions.md`](docs/open-policy-decisions.md) | Decisions the organization must make, including genuine ambiguities in AR 195-5 as applied to CI; two are now closed with the reasoning kept |
| [`docs/dependency-advisories.md`](docs/dependency-advisories.md) | The dependency risk register: current audit status, resolved advisories, and the connected-staging / air-gapped audit split |
| [`docs/air-gapped-build-and-maintenance.md`](docs/air-gapped-build-and-maintenance.md) | **The build constraint.** Pinned SDK, committed lock files, offline NuGet configuration, the dependency bundle and its manifest, the offline release-validation lane |
| [`db/README.md`](db/README.md) | Schema, constraints, triggers, migrations, least-privilege database setup |

## Stack

.NET 10 LTS · ASP.NET Core Razor Pages · EF Core 10 · SQL Server · IIS · Windows Authentication.

No SPA framework, no microservices, no cloud dependency, and **no Internet dependency at all** —
not for operation, and not for building. EMC is built and maintained inside an air-gapped
environment from a verified dependency bundle (`docs/air-gapped-build-and-maintenance.md`). EMC
stores **no passwords and no password hashes**.

## Layout

```
src/Emc.Domain/          Entities, invariants, regulatory rules. No EF, no ASP.NET, no I/O.
src/Emc.Application/     Use cases, authorization policy, abstractions.
src/Emc.Infrastructure/  EF Core, migrations, append-only guard, SQL Server triggers.
src/Emc.Web/             Razor Pages, IIS host, composition root.
tests/Emc.Domain.Tests/  Domain rules. No database.
tests/Emc.Application.Tests/
                         Use cases and pages over SQLite in-memory; an opt-in SQL Server lane.
scripts/                 Connected-staging bundle export; air-gapped verify, restore, build, test.
```

## Build and test

Connected (development, staging):

```
dotnet restore --locked-mode
dotnet build
dotnet test
```

Air-gapped (the real thing): `scripts/airgap/Restore-Build-Test-Offline.ps1`, which verifies the
dependency bundle's hashes, restores from it alone in locked mode, builds and tests with no
network. See `docs/air-gapped-build-and-maintenance.md`.

**361 tests** (211 domain, 150 application, of which the 10 in the SQL Server lane are skipped unless opted in). Three lanes:

- **Domain** — pure rules, no database.
- **Application and pages, over SQLite in-memory** — real foreign keys, unique and filtered
  indexes and transactions, with no database server. The EF in-memory provider is deliberately
  **not** used: it enforces none of those, which for an accountability system is the worst kind of
  green. The web-host tests run the real application with a test identity and drive it through
  actual HTTP POSTs. **SQLite does not exercise the SQL Server trigger layer**; the tests cover the
  domain and `SaveChanges` layers of the same guard.
- **SQL Server release validation** — opt-in with `EMC_SQLSERVER_TEST_CONNECTION`, offline against
  an approved local instance. Applies the committed migrations from empty and proves the
  append-only triggers reject `UPDATE`/`DELETE` on every accountability table, subtype columns
  included; canonical document-number uniqueness; the appointment index; concurrency conflicts;
  `datetimeoffset` round-trips. **Skipped, visibly, when not opted in** — and not yet executed
  against a real instance from this repository's development environment, which has none.

## Running locally

Requires SQL Server. Set the `Emc` connection string in `src/Emc.Web/appsettings.json` (or user
secrets), then:

```
dotnet ef database update --project src/Emc.Infrastructure --startup-project src/Emc.Web
dotnet run --project src/Emc.Web
```

**The application never migrates on startup** (AUD-012) — silent schema change on an
accountability system is unacceptable, and the runtime login is not granted the rights to do it.

On first run the database has no configuration, evidence room or users; every page shows a notice
saying so until an administrator seeds them.

## Implemented in the first vertical slice

Create a case → create a draft DA Form 4137 → add and edit items while it is a draft → submit for
custodian intake → record the official document number assigned in the ledger → assign
evidence-room locations → view a complete chronological item history → correct an entry without
destroying the original. Every action is audit logged.

## Designed, specified, and deliberately not yet built

Scanned-form ingestion and local OCR, reconciliation, DA Form 4137 generation, disposition,
inspections and inventories, long-term retention, digital-forensic metadata, and the suspense
dashboard. All are specified in the traceability matrix and modelled in the domain document.

The slice exists to prove the event and correction model, because every one of those subsystems
is built on top of it.

## Before deployment

1. **Answer the open decisions** in `docs/open-policy-decisions.md`. **DEC-06 (accredited
   classification level) must be settled before EMC holds real data** — an aggregation of CI
   evidence descriptions may itself be classified, which changes the accreditation and the
   hosting enclave. **DEC-10** (the evidence room's document-number layout) decides what the
   custodian can transcribe.
2. **Run the SQL Server release-validation lane** against the enclave's SQL Server and keep the
   output. It is a release gate.
3. **Export and verify the dependency bundle** per `docs/air-gapped-build-and-maintenance.md`;
   the bundle's audit report is the vulnerability assessment of record.
4. **Apply least privilege** to the application's SQL login (`db/README.md`) so the running
   application cannot drop the append-only triggers it depends on.
5. **Configure each evidence room's time zone** with an id the IIS host resolves natively
   (`Eastern Standard Time`, not `America/New_York`): the build is invariant-globalization and
   does no Windows/IANA conversion.

## Public repository safety

This repository is developed publicly. **Never commit** a real DA Form 4137, real case control
numbers, real subjects, victims, witnesses or agent names tied to cases, real serial numbers or
IMEIs, real evidence descriptions, unit network details, server names or addresses, credentials,
connection strings with credentials, classified or CUI operational information, or real
forensic-image metadata. Every name, number, case and location in the tests and seeds is
fictitious and must stay unmistakably so.
