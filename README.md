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

The upgrade path to an approved automated equivalent is a configuration change plus a migration,
not a rewrite — but V1 does not assume approval.

## Documentation

| Document | Contents |
|---|---|
| [`docs/regulatory-requirements.md`](docs/regulatory-requirements.md) | AR 195-5 extract with paragraph references, organized around the CI variant. **§12 lists claims EMC must *not* make** — several useful features are design decisions, not regulatory mandates |
| [`docs/architecture.md`](docs/architecture.md) | Stack, layering, the event model, three-layer append-only enforcement, the hash chain, authorization, security posture, deployment |
| [`docs/domain-model.md`](docs/domain-model.md) | Entities, four independent state axes, immutable vs mutable data, 22 numbered invariants |
| [`docs/requirements-traceability.md`](docs/requirements-traceability.md) | ~120 requirement IDs → AR 195-5 paragraph → type → status → tests. **A blank regulation column is meaningful**: it marks a design decision |
| [`docs/open-policy-decisions.md`](docs/open-policy-decisions.md) | Nine decisions the organization must make, including two genuine ambiguities in AR 195-5 as applied to CI |
| [`docs/dependency-advisories.md`](docs/dependency-advisories.md) | Open dependency advisories and their assessments |
| [`db/README.md`](db/README.md) | Schema, constraints, migrations, least-privilege database setup |

## Stack

.NET 8 · ASP.NET Core Razor Pages · EF Core 8 · SQL Server · IIS · Windows Authentication.

No SPA framework, no microservices, no cloud dependency, no internet dependency for normal
operation. EMC stores **no passwords and no password hashes**.

## Layout

```
src/Emc.Domain/          Entities, invariants, regulatory rules. No EF, no ASP.NET, no I/O.
src/Emc.Application/     Use cases, authorization policy, abstractions.
src/Emc.Infrastructure/  EF Core, migrations, append-only guard, SQL Server triggers.
src/Emc.Web/             Razor Pages, IIS host, composition root.
tests/                   Domain rules, and integration tests over SQLite in-memory.
```

## Build and test

```
dotnet build
dotnet test
```

125 tests. The domain tests need nothing; the integration tests use SQLite in-memory — real
foreign keys, unique and filtered indexes and transactions, with no database server. The EF
in-memory provider is deliberately **not** used: it enforces none of those, which for an
accountability system is the worst kind of green.

The web-host tests run the real application with a test identity and drive it through actual
HTTP POSTs, so page handlers, model binding and anti-forgery are exercised rather than assumed.

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
   hosting enclave.
2. **Assess the open dependency advisory** in `docs/dependency-advisories.md`.
3. **Apply least privilege** to the application's SQL login (`db/README.md`) so the running
   application cannot drop the append-only triggers it depends on.
