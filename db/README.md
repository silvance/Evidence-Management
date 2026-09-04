# Database

**SQL Server.** Schema is defined by EF Core code-first migrations in
`src/Emc.Infrastructure/Migrations`, which are source-controlled and reproducible (AUD-012).

## Applying the schema

**The application never migrates on startup.** Silent schema change on an accountability system
is unacceptable, and the application's runtime login is deliberately not granted the rights to do
it (`docs/architecture.md` §11).

Apply migrations as a separate deployment step, with a separate higher-privilege login:

```
dotnet ef database update --project src/Emc.Infrastructure --startup-project src/Emc.Web
```

Or review and run the generated script:

```
dotnet ef migrations script --idempotent \
    --project src/Emc.Infrastructure --startup-project src/Emc.Infrastructure \
    --output db/schema-v1.sql
```

`db/schema-v1.sql` is a committed copy for review. **Regenerate it whenever a migration is
added**, so a reviewer can see the schema change without running the tooling.

## Seeding a new installation

Administration screens for users, roles, storage locations and system configuration are designed
but not built in the first vertical slice, so `db/seed-initial.sql` is the supported way to bring
a new installation up. **Review and edit the values marked `<-- EDIT` before running it.**

```
sqlcmd -S <server> -d Emc -i db/seed-initial.sql
```

It creates system configuration, the six roles, one evidence room, one user keyed to an Active
Directory object SID, a role assignment, a written custodian appointment, and a few storage
locations. It creates no accountability data.

Two values in it are decisions, not defaults:

- **`AccreditedClassificationLevel`** — see open decision **DEC-06**. An aggregation of CI
  evidence descriptions may itself be classified, which changes the accreditation and the hosting
  enclave. Settle this with the security manager before the system holds real data.
- **`AuthoritativeMode` / `NumberingMode`** — leave both at `0`. Setting either to `1` makes EMC a
  stand-alone automated evidence ledger, which AR 195-5 para 2-5c requires **Army G-2X** to
  approve for a CI organization beforehand.

## What the schema enforces

Integrity lives in the database, not only in the UI (engineering principle 7):

| Constraint | Purpose |
|---|---|
| Unique index over `(EvidenceRoomId, CalendarYear, Sequence)` on `OfficialDocumentNumberAssignments` | AR 195-5 2-4c / 2-7g — the canonical document number is unique per **evidence room, per calendar year**, across **all** assignment history. **Unfiltered**: once recorded, a number is consumed for good, superseded or not (VCH-011, invariant I-04). The written text (`001-26` or a local `26-01`) is presentation; identity is canonical (VCH-023) |
| `UX_ItemEvents_ItemSequence` | Event sequence numbers unique per item — also what makes a removed row detectable as a gap during chain verification (invariant I-07) |
| `UX_CustodianAppointments_OneOpenPerType` | AR 195-5 1-4g(1) — one open primary and one open alternate appointment per evidence room (invariant I-06) |
| `IX_EvidenceItems_VoucherId_ItemNumber` | Item numbers unique within a voucher (invariant I-01) |
| `TR_ItemEvents_AppendOnly_Update` / `_Delete` | Append-only accountability history. **Unconditional**: every `UPDATE` and `DELETE` is rejected, on every column including table-per-hierarchy subtype columns. Corrections are new rows that reference backward (`CorrectsEventId`); nothing is ever updated |
| `TR_AuditEvents_AppendOnly_Update` / `_Delete` | Append-only security audit |
| `TR_DocumentNumbers_AppendOnly_Update` / `_Delete` | AR 195-5 2-7g — a prior document number is superseded by a new assignment that names it (`SupersedesAssignmentId`), never rewritten |
| `TR_VoucherReviewActions_AppendOnly_Update` / `_Delete` | AR 195-5 2-3g — the record of the custodian's pre-acceptance review is kept as it happened |
| Unique index over `(EvidenceRoomId, Date)` on `TemporaryIdentifierCounters` | Collision-safe temporary-identifier allocation (VCH-024); the counter row carries a concurrency stamp |
| `EvidenceRoomNumberingPolicies` | Effective-dated per-room document-number layout (VCH-023). The regulation's layout applies when a room has none |
| `SourceDocuments` + `TR_SourceDocuments_AppendOnly_*` | Immutable companion copies of scanned documents (DOC-002). Bytes live in the filesystem store outside the web root; the row holds the generated key and the SHA-256 recorded at receipt (AUD-022). No page count and no render state: both are derived from the render runs |
| `DocumentRenderJobs` | Work records: one row per request to render a document, leased by the worker under the concurrency stamp exactly like `OcrJobs` (DOC-014). Mutable by design; no trigger |
| `DocumentRenderRuns` / `DocumentRenderPages` + `TR_DocumentRenderRuns_AppendOnly_*`, `TR_DocumentRenderPages_AppendOnly_*` | Immutable render attempts and the page images a successful attempt produced (DOC-015). A failed attempt stays; the newest successful run is the current page set; `OcrJobs.RenderRunId` names the one run an OCR job reads |
| `OcrJobs` | Work records for local OCR: leased by a worker id under the concurrency stamp, retried on transient failure, settled by the lease holder (OCR-011). Mutable by design; carries a failure category, never text |
| `OcrRuns` / `OcrRunPages` / `ExtractedFields` / `FieldVerifications` + `TR_OcrRuns_AppendOnly_*`, `TR_OcrRunPages_AppendOnly_*`, `TR_ExtractedFields_AppendOnly_*`, `TR_FieldVerifications_AppendOnly_*` | What an engine read, where, how confidently, and what a person decided about it (OCR-004, OCR-012 .. OCR-014). Immutable: a re-run is a new run and a second look is a second verification row |
| `ReconciliationFindings` + `TR_ReconciliationFindings_AppendOnly_*` | A person's decision about one difference between a verified scan and the companion record, with both values as they were (REC-004). Append-only; a later decision is a later row |
| `TemporaryReleases` / `TemporaryReleaseItems` | One temporary release of evidence (AR 195-5 2-7a, 2-7b): category (USACIL / ADJUDICATION), the two custody parties, when it left, purpose, the suspense folder, the five 2-7b paper attestations; per item, the custody event that took it out and the one that brought it back. `UX_TemporaryReleaseItems_OneOpenPerItem` keeps an item on at most one open release |
| `TemporaryReleaseEvents` + `TR_TemporaryReleaseEvents_AppendOnly_*` | What happened to the release, in order (released, item returned, accounted for without return, note, closed). Immutable |
| `SuspenseContacts` + `TR_SuspenseContacts_AppendOnly_*` | Each contact the custodian made with the holder (2-7a): the record that "reasonable and adequate contact" was kept (SUSP-005). Immutable |
| `PhysicalFileContainers`, `PhysicalVoucherDocuments`, `PhysicalVoucherDocumentEvents` + `TR_PhysicalVoucherDocumentEvents_AppendOnly_*` | The PAPER DA Form 4137: files, suspense folders, where the original is, inactive date, confirmed destruction (FIL-001..FIL-009) |
| `VoucherFormRevisions`, `VoucherFormRevisionLines` + triggers | What the form contained at each submission (VCH-025) |

## Least privilege

Grant the application's runtime login `db_datareader`, `db_datawriter` and `EXECUTE` — **not**
`db_owner` or `db_ddladmin`. The running application must not be able to drop the append-only
triggers it depends on (SEC-009).

The triggers are not absolute: a principal holding `ALTER` can drop them. That is why events are
also hash-chained per item (AUD-008), so out-of-band modification stays **detectable by any
reader** even if the triggers are removed.

## Tests

Everyday integration tests run against **SQLite in-memory**, not SQL Server, so they need no
database server. SQLite is a test-only dependency — no production assembly references it. **The
append-only triggers are SQL Server-only and SQLite does not exercise them**; those tests cover
the domain and `SaveChanges` layers of the same guard, and verify that hash-chain and snapshot
verification detect raw-SQL modification and deletion.

The **SQL Server release-validation lane** (`tests/Emc.Application.Tests/SqlServer`, opt-in via
`EMC_SQLSERVER_TEST_CONNECTION`) applies the committed migrations to an empty database on an
approved local instance and proves every trigger, the unique and filtered indexes, concurrency
conflicts and `datetimeoffset` round-trips as deployed. It runs offline. It is a release gate. See
`docs/air-gapped-build-and-maintenance.md`.
