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
| `UX_DocumentNumber_PerRoomPerYear` | AR 195-5 2-4c / 2-7g — the document-number series is unique per **evidence room, per calendar year**, among non-superseded assignments. Filtered so a number superseded under 2-7g does not block the series (invariant I-04) |
| `UX_DocumentNumber_OneCurrentPerVoucher` | At most one current document number per voucher (invariant I-05) |
| `UX_ItemEvents_ItemSequence` | Event sequence numbers unique per item — also what makes a removed row detectable as a gap during chain verification (invariant I-07) |
| `UX_CustodianAppointments_OneOpenPerType` | AR 195-5 1-4g(1) — one open primary and one open alternate appointment per evidence room (invariant I-06) |
| `IX_EvidenceItems_VoucherId_ItemNumber` | Item numbers unique within a voucher (invariant I-01) |
| `TR_ItemEvents_AppendOnly_Update` / `_Delete` | Append-only accountability history. Permits only `SupersededByEventId`, null → value, once |
| `TR_AuditEvents_AppendOnly_Update` / `_Delete` | Append-only security audit |
| `TR_DocumentNumbers_AppendOnly_Update` / `_Delete` | AR 195-5 2-7g — a prior document number is superseded, never rewritten |

## Least privilege

Grant the application's runtime login `db_datareader`, `db_datawriter` and `EXECUTE` — **not**
`db_owner` or `db_ddladmin`. The running application must not be able to drop the append-only
triggers it depends on (SEC-009).

The triggers are not absolute: a principal holding `ALTER` can drop them. That is why events are
also hash-chained per item (AUD-008), so out-of-band modification stays **detectable by any
reader** even if the triggers are removed.

## Tests

Integration tests run against **SQLite in-memory**, not SQL Server, so they need no database
server. SQLite is a test-only dependency — no production assembly references it. The append-only
triggers are SQL Server-only; the tests cover the domain and `SaveChanges` layers of the same
guard, and separately verify that hash-chain verification detects raw-SQL modification and
deletion.
