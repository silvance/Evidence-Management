# EMC Architecture

**Evidence Management Companion (EMC)** — an internal Army Counterintelligence evidence
accountability companion application.

> **What this application is.** A companion that assists the AR 195-5 evidence process.
> **What it is not.** The authoritative evidence ledger, the authoritative DA Form 4137, or a
> replacement for required signatures. See `docs/regulatory-requirements.md` §2 (AR 195-5,
> para 2-5c).

---

## 1. The constraint that shapes everything

AR 195-5 **2-5c** distinguishes two kinds of software:

| | Approval required | EMC V1? |
|---|---|---|
| **Stand-alone automated evidence ledger/accountability system** | Yes — **Army G-2X** for CI organizations, prior to use | **No** |
| **Automated system used in conjunction with, or to enhance, the requirements of the regulation** | **No approval required** | **Yes** |

Everything below follows from choosing the second column. Three rules fall out, and they are
enforced structurally rather than by convention:

1. **EMC does not assign the evidence document number.** Para 2-4c ties assignment to order of
   precedence *in the evidence ledger*, which para 2-5a requires to be a bound book absent
   approval. EMC holds a **temporary identifier** until a custodian transcribes the number the
   ledger actually assigned. See §6.
2. **EMC does not produce signatures.** Paras 2-5b(2), 2-7b, 3-1b(2), 3-2g and 2-8e(5) all
   require handwritten signatures. EMC records **attestations** — assertions that a paper
   signature was executed — and labels them as such in the schema and the UI. See §7.
3. **EMC never presents itself as authoritative.** Every view of accountability data carries an
   explicit authoritative-record notice, driven from configuration so that it changes in exactly
   one place if approval is later granted.

**The upgrade path is deliberate, and it is a configuration change plus a migration, not a
rewrite.** `SystemConfiguration.AuthoritativeMode` has two values: `Companion` (V1) and
`AuthoritativeLedger` (post-approval). The numbering strategy, the authoritative-record banner
and the ledger-transcription prompts read from it. Nothing else in the domain changes, because
the domain already models the ledger's *semantics* (ordered assignment, strike-through
corrections, signed certifications) rather than the ledger's *paper*.

---

## 2. Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | **.NET 10 (LTS, supported to November 2028)** | Retargeted from .NET 8 as a security action (`docs/dependency-advisories.md`). `global.json` pins the **exact** SDK with roll-forward disabled: the air-gapped host builds with the SDK in the bundle or not at all. |
| Web | **ASP.NET Core Razor Pages** | Server-side rendering. Page-per-screen matches a forms-and-workflow application, and one developer can hold it in their head. MVC's controller/action indirection buys nothing here. |
| Data | **Entity Framework Core 10**, code-first, **SQL Server** | Migrations are source-controlled and reproducible (`Emc.Infrastructure/Migrations`); `db/schema-v1.sql` is the reviewable script. |
| Hosting | **IIS**, in-process, on-premises | No internet dependency for normal operation. |
| AuthN | **Windows Authentication (Negotiate/Kerberos)** | See §8. **EMC stores no passwords.** |
| Client JS | **None beyond the ASP.NET Core defaults** | No SPA framework. Progressive enhancement only. |
| Logging | **Microsoft.Extensions.Logging** to rolling files + Windows Event Log | Structured, local, no telemetry egress. |
| Tests | **xUnit**; SQLite in-memory for relational tests; an **opt-in SQL Server lane** | Real relational semantics (FKs, unique indexes, transactions) with no SQL Server dependency for everyday runs; the SQL Server lane (§12) proves triggers, filtered indexes and constraints as deployed. |
| Supply chain | **Air-gapped.** Pinned SDK, committed `packages.lock.json`, `NuGet.Offline.Config` with sources cleared, hashed dependency bundle | `docs/air-gapped-build-and-maintenance.md`. No run-time or build-time Internet dependency. |

**Explicitly excluded:** microservices, message brokers, Redis, containers as a *requirement*,
cloud services of any kind, client-side frameworks, GraphQL, and any component whose failure
mode a single maintainer cannot reason about at 0200.

---

## 3. Solution layout

```
Evidence-Management/
├── docs/
│   ├── regulatory-requirements.md     AR 195-5 extract with paragraph citations
│   ├── architecture.md                this document
│   ├── domain-model.md                entities, states, invariants, immutability
│   ├── requirements-traceability.md   EMC-/ITEM-/COC-/… requirement IDs → AR 195-5 → tests
│   └── open-policy-decisions.md       decisions the organization must make
├── db/
│   └── README.md                      how to produce a reviewable schema script
├── src/
│   ├── Emc.Domain/          Entities, enums, invariants. No EF, no ASP.NET, no I/O.
│   ├── Emc.Application/     Use-case services, authorization policy, abstractions.
│   ├── Emc.Infrastructure/  EF Core DbContext, configurations, migrations, clock, audit sink.
│   └── Emc.Web/             Razor Pages, IIS host, composition root.
└── tests/
    ├── Emc.Domain.Tests/          Pure domain-rule and invariant tests.
    └── Emc.Application.Tests/     Use-case tests over SQLite in-memory.
```

**Dependency direction is one-way and enforced by project references:**

```
Emc.Web ──► Emc.Application ──► Emc.Domain
   │                              ▲
   └──────► Emc.Infrastructure ───┘
```

`Emc.Domain` references nothing. That is what makes the regulatory rules testable without a
database, a web server, or a mocking framework — which is the point of the split. Four projects
is the boring .NET default; it is not layering for its own sake.

---

## 4. The event model

### 4.1 One table, several event types

Custody, location, seal, examination, status and administrative events are **separate C# types**
with distinct fields and distinct validation, mapped by EF Core **table-per-hierarchy** onto a
single `ItemEvents` table with a discriminator.

Why one table:

- **"Complete chronological item history" is a single indexed query**, not a five-way UNION over
  tables with different shapes. This is the headline feature of the first vertical slice.
- **The append-only guard, the correction mechanism and the hash chain are implemented once.**
  Five tables means five chances to forget one.
- Ordering across event kinds is trivially correct.

Why the types stay separate in C#: a `CustodyEvent` requires a releasing party, a receiving
party and a purpose; a `LocationEvent` requires a storage location. Collapsing them into one
class with nullable everything would move validation from the compiler into runtime checks.
TPH gives both properties.

The cost is nullable columns for subtype-specific fields. That is accepted and is a normal EF
Core TPH trade-off. Filtered unique indexes and `CHECK` constraints (per discriminator) recover
the integrity that nullability would otherwise lose.

### 4.2 Append-only, enforced in three places

Defence in depth, because a single guard is a single point of failure:

1. **Domain.** Event types have no public setters. Once constructed, an event's accountability
   fields cannot change. There is no code path that mutates them.
2. **Persistence.** `EmcDbContext.SaveChanges` rejects `EntityState.Modified` and
   `EntityState.Deleted` for **any** entity implementing `IAppendOnly`, unconditionally. This
   catches mistakes made through EF regardless of which service made them.
3. **Database.** SQL Server `INSTEAD OF UPDATE, DELETE` triggers on `ItemEvents` and
   `AuditEvents` raise an error, also unconditionally. This catches modifications made *outside*
   the application — including by a DBA using SSMS.

Layers 2 and 3 each once carried a narrow allowance, permitting an `UPDATE` that set only a
forward "superseded by" pointer. That allowance forced the trigger to *prove* every other column
was unchanged, and it compared only the columns common to all event types — so a
table-per-hierarchy subtype column (`StorageLocationPath`, `PurposeOfChangeOfCustody`, a seal
field) could be rewritten alongside a legitimate supersession and pass. Corrections now reference
backward (§4.4), which leaves no legitimate `UPDATE` to allow and no column comparison to get
wrong.

Layer 3 is shipped as a dedicated migration and is SQL Server-only; SQLite test runs exercise
layers 1 and 2, and there are explicit tests for each.

### 4.3 Tamper evidence: a per-item hash chain

**[CONTROL] — not required by AR 195-5.**

Triggers stop casual modification. They do not stop someone with `ALTER TABLE`. Because the
application administrator must not be able to rewrite evidence history — and because in a
small-team on-premises deployment the application administrator and the database administrator
are frequently the same person — EMC chains its events:

```
EventHash = SHA-256( canonical(event fields) || PreviousEventHash )
```

The chain is **per `EvidenceItem`**, sequenced by `SequenceNumber` (a per-item monotonic
integer, not a global one — so concurrent work on different items never contends).

- Any altered, inserted or removed row breaks the chain from that point forward.
- Verification is a read-only pass with no privileged access: **Integrity → Verify item chain**.
- The canonical serialization is versioned (`HashSchemaVersion`) so the algorithm can evolve
  without invalidating history.

**State its strength accurately.** The chain is **unkeyed**: it is computed from the data and
stored beside it, so a knowledgeable database administrator who modifies a row *and recomputes the
chain from that point forward* would not be detected by it. Describe it as:

> A tamper- and corruption-detection mechanism. It detects modification, deletion and insertion of
> events **when the stored chain has not also been deliberately recomputed**.

It is genuinely strong against accidental corruption, application bugs, out-of-band edits that
don't know about the chain, and casual database manipulation — which covers the realistic cases.
It is **not** equivalent to a digital signature, an external immutable ledger, or a keyed MAC whose
key lives outside the database.

A future control could sign periodic integrity checkpoints with a key held outside SQL Server,
which would close the recompute gap. That is deliberately not built now: it adds PKI and key
custody, and the honest description above is enough for V1 provided nobody overstates it.

### 4.3a The item's summary is verified against its history

`EvidenceItem` carries three derived fields — `AccountabilityStatus`, `LastEventSequenceNumber`,
`LastEventHash` — because reading them is cheaper than replaying the history on every list. They
are summaries, and a summary modified out of band drifts from its source: a single
`UPDATE EvidenceItems SET AccountabilityStatus = 7` would show an item as disposed while its
history says it is in the evidence room, and the hash chain would not notice, because the chain
protects the events and not the summary.

`SnapshotVerifier` checks the three against the events on every history view and in the
room-wide report, and the result is kept **distinct** from chain verification because the two
mean different things:

| | Meaning | Response |
|---|---|---|
| **Event chain failure** | The history itself was altered, inserted into or truncated | An incident. Not repairable by software. AR 195-5 3-3. |
| **Snapshot mismatch** | The history verifies; the item's summary of it is wrong | Recompute the summary from the history; find out how it was changed. |

The room-wide report (`IIntegrityVerificationService`) carries identifiers and problems only —
no description, serial number or custody party — so the application administrator, who holds
`VerifyIntegrity` but no evidence-read permission, can run it (IAM-009, IAM-017). A test asserts
the report row's shape.

### 4.4 Corrections never destroy history

Modelled on AR 195-5 **2-5b(5)** (an erroneous ledger entry is struck through *so it may still be
read*, and initialled — correction fluid, tape, labels and erasures are prohibited) and
**1-7c(3)** (the discovering custodian **immediately informs the supervisor** and prepares an
**MFR** stating the error and the corrective action, filed with the DA Form 4137 and copied to
the case file).

A correction is a **new event** of type `CorrectionEvent` that:

- references the corrected event (`CorrectsEventId`) — a **backward** reference only;
- names **exactly one field**, and records its **original value** and its **corrected value**;
- records **reason**, **correcting user**, **occurrence time** and **system entry time**;
- carries the **MFR reference** required by 1-7c(3), and the **supervisor notified** — printed
  name, grade or title, organization, and when — with an EMC user link only when the supervisor
  has one.

**For a correction to an accepted accountability record, the MFR reference and the supervisor
notification are required, and the correction is refused without them.** 1-7c(3)'s verbs are
"will immediately inform" and "will prepare"; they describe part of correcting the entry, not a
follow-up to it. An earlier version recorded the correction and displayed a warning, which
changed the accountability record before the documentation the regulation attaches to that change
existed. The requirement is scoped to the category 1-7c(3) actually governs: a submitting agent
correcting a draft under 2-3g, or a verifier fixing an OCR transcription, is not a custodian
finding an incorrect entry, and demanding a custodian-error MFR there would misstate the
regulation.

The supervisor is modelled as the MFR names them, not as an application account. The responsible
CI supervisor of 1-7c(3) frequently holds no EMC login — EMC is used by an evidence room's agents
and custodians, while the supervisor informed may sit in the operations section or a higher
headquarters — and a model that required a user id would have forced the custodian to either
leave the notification unrecorded or misattribute it to whoever did have an account. When a user
*is* linked, the recorded particulars are read from the user record, not the form.

**The corrected event is not touched.** There is no forward pointer, no status flag and no
"superseded" column, which is why the append-only triggers (§4.2) can reject every `UPDATE`
unconditionally instead of having to decide which column change is legitimate. Whether an event
has been corrected is *derived* from the existence of these records.

Three properties of this shape matter, and each replaces something that was wrong:

**Field-level, not event-level.** Correcting one field leaves the rest of the event standing. An
earlier design marked the whole event superseded and excluded superseded events from projections,
so correcting an item's location from Bin 14 to Bin 19 left the item with **no recorded location
at all**. `EffectiveItemEvent` now projects each event with its corrections applied field by
field.

**The original value is derived by the server** from the corrected event's own declared
correctable fields. There is no parameter through which a caller could state it. An "original
value" that arrived from a form post would be worth nothing as an audit record, since the party
making the correction could claim the record had said anything they liked.

**A field that names a row is corrected by naming the replacement row.** An item's storage
location and a change of custody's parties are `StorageLocation` and `CustodyParty` rows, not
text. A correction to one of them carries the replacement **identifier**, and its display text is
read *from that row* by the server — so the text and the identifier can never disagree. Without
this, correcting a location changed what the history displayed while every projection built on
`StorageLocationId` still pointed at the location that had just been declared wrong: an inventory
of Bin 19 would not have listed the item the record said was in it. The same evidence-room check
that governs assigning a location governs correcting one, so a correction is not a way around a
check the original action applied.

A field may be corrected more than once, and a correction may itself be corrected. Two rules
govern a chain of corrections to one field:

**Each correction records what it changed, not only what was first written.** `OriginalValue` is
the entry as originally recorded and is the same on every correction in the chain — 2-5b(5)'s
struck-through entry, always readable. `PreviousEffectiveValue` is the field as it read
immediately before *this* correction. For Bin 14 → Bin 19 → Bin 21 the second correction is a
correction *of Bin 19*: 1-7c(3)'s MFR must outline the error, and the error was Bin 19. Reporting
it as "Bin 14 → Bin 21" would describe a change that never happened. The no-change check and the
audit trail's previous value both use the effective value, so restating the current value is
refused even when it differs from the original, and restoring the original after a mistaken
correction is allowed.

**Which correction is current follows append order alone.** Corrections are ordered by
`SequenceNumber`, which the server assigns and which is gapless; never by `OccurredAtUtc`, which
comes from a form field. Back-dated entry is legitimate for events, so a user can supply any
occurrence time — and if ordering used it, whoever entered a correction last could back-date it
to take precedence over one entered before it. (Which *location event* is current is a different
question — the item physically moved at a time — and that one does use occurrence time.)

Nothing is ever hidden.

The read model shows the corrected value with a visible **"Corrected"** marker; the original is
one click away and is never hidden from the item history. The regulation's own metaphor is a
line through an entry that can still be read, and the UI keeps that metaphor.

### 4.4a Local time is the evidence room's, not the server's

AR 195-5 records local time — the ledger example is "03 SEP 26 09:15" (2-5b) and every
chain-of-custody entry carries a date and time (2-3f). EMC stores each local instant **with the
offset it had in the evidence room's zone** (`EvidenceRoom.TimeZoneId`), so what the paper says
and what the record says agree (AUD-011).

All interpretation goes through one service, `IEvidenceRoomTimeService`, backed by the pure
`LocalTimeInterpretation`. No page or service touches `TimeZoneInfo.Local` or `DateTime.Now`
(AUD-019): the earlier pages did, which meant a UTC-hosted IIS server would have recorded "09:15"
as 09:15Z and put every event four or five hours off the form. A test changes the process's own
zone three times and shows the interpretation does not move.

Daylight-saving edge cases are **explicit, never defaulted** (AUD-020):

- A time in the repeated hour (01:30 on the first Sunday of November, in a US zone) is
  *ambiguous*. It is refused until the custodian states which occurrence is meant — the form has
  a field for exactly that — because which one it was is a fact only they know.
- A time in the skipped hour (02:30 on the second Sunday of March) is *nonexistent* and is
  refused outright. Substituting an adjacent time would be inventing when something happened.

**Deployment constraint.** The solution is built with `InvariantGlobalization=true`, so no
Windows↔IANA zone-id conversion exists at run time. A room's `TimeZoneId` must be an identifier
the host resolves natively: a Windows id such as `Eastern Standard Time` on the IIS host; an
IANA id such as `America/New_York` on Linux development and test hosts. An id the host does not
know is reported as a configuration error for that room, never silently replaced by the host's
zone.

### 4.5 Two distinct logs

| | `ItemEvents` (+ `Inspections`, `InventoryObservations`, …) | `AuditEvents` | Diagnostic logs |
|---|---|---|---|
| **Contains** | The accountability record: custody, location, seals, examinations, corrections | Security/administrative audit: sign-in, role change, permission denial, export, source-document download, integrity verification, configuration change | Technical events: exceptions, timings, EF warnings |
| **Store** | SQL Server, append-only, hash-chained | SQL Server, append-only | Rolling files + Windows Event Log |
| **Retention** | Governed by ARIMS/RRS-A (see **AMB-07**) | Per organizational policy | Short, operational |
| **Sensitive content** | Yes — by design | Identifiers only | **Never** — see §10 |

Conflating these is a common and damaging mistake: diagnostic logs get rotated, shipped and
read casually, and evidence descriptions must not travel with them.

---

## 5. Authorization

**Server-side, per request, from the database. Client-submitted role information is never
trusted** — no role claims from cookies, form fields, query strings or hidden inputs are ever
read. The user's identity comes from Windows Authentication; the user's **roles come from the
`UserRoles` table**, resolved on each request and cached only for the request's lifetime.

### 5.1 Roles

| Role | AR 195-5 basis | May |
|---|---|---|
| **Agent** | 2-3b (first agent prepares the DA Form 4137) | Create cases and draft vouchers; add and edit items while `Draft`; submit for custodian acceptance; upload source documents |
| **PrimaryEvidenceCustodian** | 1-4g(1), 1-4h, 1-7a(1)(c) | Everything the evidence room requires: accept evidence, transcribe the document number, assign locations, release and return, manage suspense, act on disposition once authorized, participate in inventories |
| **AlternateEvidenceCustodian** | 1-4i | The primary's duties **while an appointment is active** — see §5.2 |
| **CommanderOrSac** | 1-4g(3), 3-1b(2), 2-8c | Inspections, attestations, discrepancy review, approvals, dashboards |
| **InspectorOrInventoryParticipant** | 3-1, 3-2 | Participate in an assigned inspection/inventory session only |
| **ApplicationAdministrator** | none — **[DESIGN]** | Accounts, roles, storage-location configuration, system configuration, maintenance |

### 5.2 Authority is time-bounded, not a flag

AR 195-5 does not grant custodial authority to a *role*. It grants it to a **person named in a
written appointment** (1-4g(1), 1-7b), and the alternate holds it only **during the primary's
temporary absence — more than 1 working day and not more than 30 consecutive days** (1-4i).

So EMC checks two things for a custodian action:

```
IsInRole(PrimaryEvidenceCustodian | AlternateEvidenceCustodian)
    AND an active CustodianAppointment exists for (this user, this evidence room, now)
```

`CustodianAppointment` is a first-class entity with an effective range, an appointment-order
reference, an appointing authority, and a supersession link (1-4i: emergency alternate orders
*supersede* the previous alternate's). A role flag alone cannot express "exactly one primary and
one alternate at a time" (1-4g(1)); an appointment with a date range, plus a database constraint,
can.

### 5.3 The administrator boundary

**The `ApplicationAdministrator` role grants no evidence-accountability permission whatsoever.**
It is not a superset of the other roles. Concretely:

- Every accountability handler requires a specific evidence permission. There is no
  "administrator bypass" branch anywhere in the codebase, and a test asserts that an
  administrator is denied on every accountability endpoint.
- An administrator **can** grant themselves a custodian role — that is inherent in administering
  accounts, and no application can prevent it. What EMC does is make it **loud**: role grants are
  written to `AuditEvents`, self-grants are flagged, and the administration surface says so.
- Beyond the application, the append-only triggers (§4.2) and the hash chain (§4.3) mean that
  even database-level access leaves detectable evidence.

This is stated plainly rather than overclaimed: EMC makes administrative tampering **visible**,
not impossible.

---

## 6. Evidence numbering

```
Draft voucher created        →  TMP-20260903-A014        (EMC-generated, unmistakably temporary)
Custodian receives evidence  →  037-26                   (transcribed from the bound ledger)
```

**Temporary identifier format:** `TMP-yyyyMMdd-<letter><3 digits>`, allocated per evidence room
per day. It is deliberately unlike the regulatory `NNN-YY` format so the two can never be
confused on a screen, in a search box, or on a printout.

**Official number entry** (`OfficialDocumentNumberAssignment`) records:

- the number as entered, and its parsed `Sequence` + `CalendarYear`;
- **who** entered it and **when** (system time);
- an explicit **attestation** that the number was assigned in the authoritative evidence ledger —
  a checkbox the custodian must tick, stored as a first-class boolean with the attesting user, not
  inferred;
- the assigning evidence room.

**Constraints:**

- Format `^\d{3}-\d{2}$` (2-4c).
- Unique on `(EvidenceRoomId, CalendarYear, Sequence)` — **not** globally. The series is per
  evidence room per calendar year (2-4c, and 2-7g which shows a receiving room assigns its own
  next number).
- A **non-blocking gap warning**: if the previous sequence for that room and year is absent, the
  UI says so. EMC cannot *know* the ledger's true state, so it warns and never blocks.
- On permanent transfer between evidence rooms (2-7g) a **new** assignment row is added and the
  previous is marked superseded. Prior numbers remain visible — the digital equivalent of "lined
  through in such a way that it remains legible."

**Post-approval path.** `SystemConfiguration.NumberingMode` selects
`ManualTranscription` (V1) or `SystemAssigned`. The second is implemented behind the same
interface, using a serializable transaction against a per-room-per-year counter — but it stays
switched off, and the administration UI states that enabling it for a CI organization requires
Army G-2X approval under 2-5c.

---

## 7. Attestations, not signatures

AR 195-5 requires **handwritten** signatures in the ledger (2-5b(2)), on custody transfers
(2-7b), on inspection and inventory certifications (3-1b(2), 3-2g) and in the Final Disposal
Authority block (2-8e(5)).

V1 therefore has **no electronic signature**. It has `AttestationRecord`, which asserts:

> *"I record that the paper certification required by AR 195-5 para X was executed on
> `<date>` by `<person>`."*

Stored with the attesting user, the timestamp, the AR 195-5 paragraph, and the exact prescribed
statement text. The UI never uses the word "sign" for this action. Getting this wrong would be
the single most dangerous compliance error the application could make, because it would invite a
user to believe an EMC record satisfies a regulatory signature requirement when it does not.

CAC/PIV-backed digital signatures are a plausible V2 once the organization's PKI posture and
G-2X's expectations are known. Not V1.

---

## 8. Security posture

| Control | Decision |
|---|---|
| **Authentication** | Windows Authentication (Negotiate/Kerberos) against the domain, which in an Army environment is CAC-backed. **EMC stores no passwords, no password hashes and no secrets in the user table.** `User` holds the AD `ObjectSid` as the stable key plus a UPN for display. Anything else would create a credential store that has to be defended, rotated and audited for no benefit. |
| **Authorization** | Server-side per request from the database (§5). Every accountability page carries an explicit policy; there is no default-allow. |
| **Transport** | HTTPS only; HSTS in non-development; secure and HttpOnly cookies. |
| **Anti-forgery** | Antiforgery validation on every state-changing request. |
| **Injection** | EF Core parameterization throughout. No dynamic SQL in application code. |
| **Concurrency** | `ConcurrencyStamp` (GUID) on mutable aggregates, checked on update, provider-independent so the same behaviour holds in tests. Document-number assignment additionally uses a serializable transaction. |
| **Uploads** | See §9. |
| **Errors** | No stack traces or SQL to the browser. Correlation ID displayed; detail goes to the diagnostic log. |
| **Headers** | `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, a restrictive `Content-Security-Policy` (no external origins — the application runs with no internet dependency), `Referrer-Policy: no-referrer`. |
| **Data at rest** | SQL Server TDE and BitLocker are deployment-level controls, specified in the deployment guide, not application code. |

---

## 9. Untrusted input: uploaded documents

A scanned document is **hostile input** and a **companion copy**. Both facts shape everything here.

**Companion copy.** `SourceDocument` is an immutable record of what was received: the bytes'
SHA-256 as stored, size, type, the uploader, and **provenance** — what paper was
scanned (physical original, physical copy, electronic copy, unknown). Provenance is a statement
about the scan's source, never a claim that the file is the original; the physical original's
whereabouts live on `PhysicalVoucherDocument` (§4.6), which an upload never touches. No UI text
calls the file "the original DA Form 4137".

**Hostile input.** Three layers, one number:

| Layer | Control |
|---|---|
| Request | Kestrel and IIS `MaxRequestBodySize`, multipart body limit, the upload page's `[RequestSizeLimit]` — the body is refused before application code runs |
| Application (web) | `PdfContentValidator` (header near the start, `%%EOF` near the end, size; the filename and declared type are not consulted — a byte scan, not a parse), active-content detection by token (script, launch, open actions — reported, never executed). The web process then stores, hashes and queues a render job; it never opens the PDF (DOC-014) |
| Application (worker) | Page-count limit and page-dimension/pixel limit from the page sizes **before** any page is rendered; a hard per-page timeout; every attempt an immutable `DocumentRenderRun` (DOC-015) |
| Storage | `FileSystemSourceDocumentStore`: generated keys, atomic partial-then-move writes, hash computed from the bytes written, key re-validation on every read, root containment check |

Display is by **server-rendered PNG page images**, rendered by the worker (PDFium through NuGet
runtime assets, in a child process — below). The PDF is never handed to the browser inline; the original is available only
as an attachment download under the separate `source-document.download` permission, audited.
Every page-image and download request authorizes on the owning evidence room **before** the
store is opened — a spy-store test proves zero reads for an unauthorized caller — and an
unauthorized or absent document is a 404 either way.

**Integrity.** The room-wide report re-hashes every stored document and page and reports a
mismatch or missing file as a **document-integrity failure**, a third category beside
event-chain failures and snapshot mismatches, in a row with identifiers only (AUD-022).

**Deployment (ACLs).** `SourceDocuments:RootPath` must be outside the site's physical path. The
deployer creates it; the application-pool identity gets Modify on it and no other principal
does; no static-file provider is mapped to it; it is backed up with the database. The store
refuses to start without a configured root.

**Isolation (DOC-014).** Nothing in the IIS process parses a PDF. `AddEmcInfrastructure` registers
no `IPdfRasterizer`, and `SourceDocumentService` has no dependency that could rasterize — a test
asserts both. Receipt writes the bytes and a `DocumentRenderJob`; the worker (`Emc.OcrWorker`,
§9.1) leases the job and renders each page through `IsolatedPdfRasterizer`, which starts the
worker's own executable in `render` mode as a **child process per invocation**: argument list
(never a shell line), a private per-invocation folder under `SourceDocuments:RenderWorkRoot`,
a minimal explicit environment, stdout/stderr consumed and discarded, a hard timeout that kills
the process tree, an exit code that is a category (0 ok, 2 malformed, 3 usage, 1 other), and an
output file the parent validates as bytes (PNG signature and IHDR size) rather than trusting a
claim. Timeout and crash are transient (requeued up to `MaxRenderAttempts`); malformed input and
limit breaches are final on the first attempt. The worker refuses to start without
`SourceDocuments:RenderHelperPath`.

**Every attempt is a record (DOC-015).** `DocumentRenderRun` is append-only: renderer version,
timing, outcome, failure category, page count, and — on success — the page images with their
hashes. "Is this document rendered" and "which pages are current" are derived from the newest
successful run; the document row never changes. A person may request a fresh render (a new job,
a new run); an OCR job names the one successful run it reads at request time (`OcrJobs.RenderRunId`),
so a later re-render never changes what an already-requested OCR job sees.

### 9.1 The OCR worker

**[DESIGN] / [CONTROL].** `Emc.OcrWorker` is a generic host that runs as a **Windows Service**
under the Service Control Manager (`AddWindowsService`; service name `EmcOcrWorker`) and as a
console when started by hand. It runs the render processor and `OcrJobProcessor` in a loop.
It has no HTTP surface and no listener of any kind. Installation is non-interactive
(`scripts/deploy/Install-EmcOcrWorker.ps1`; `docs/ocr-worker-deployment.md`): a dedicated
identity, least-privilege folder rights, automatic restart, event-log logging, outbound network
blocked by firewall rule, and prerequisite checks (paths, approved hashes, Windows-authenticated
connection string, lease arithmetic) that must pass before the service is registered.

- **Queue = a table.** `DocumentRenderJobs` and `OcrJobs` rows are leased by writing the worker's
  id and a lease expiry under the row's concurrency stamp; two workers racing for one job produce
  one winner and one conflict. Render jobs are drained before OCR jobs. An expired lease (the worker died) is taken over. Transient failures (timeout,
  engine crash, resource limit) requeue up to `MaxAttempts`; the rest are final. No broker, no
  queue service, nothing that is not SQL Server.
- **Engine = an external process.** Tesseract 5 is started with an argument list (never a shell
  command line), in a private per-invocation folder under `Ocr:WorkRoot` named by a random GUID
  and deleted in `finally`, with a minimal explicit environment (`TESSDATA_PREFIX`,
  `OMP_THREAD_LIMIT=1`), a hard timeout that kills the process tree, and stdout/stderr consumed
  and never logged. `docs/ocr-engine-evaluation.md` says why this engine and this shape.
- **Start-up is the check (OCR-017).** Constructing the engine hashes the binary and every
  model file and compares each with the SHA-256 in `Ocr:ApprovedArtifactHashes` - the values the
  reviewer recorded in the artifact manifest the bundle was exported with - BEFORE executing
  anything; a mismatch, a missing entry or an empty list is a start-up failure with the
  `ArtifactNotApproved` category. Only then is the binary executed for its version, under a
  bounded probe that kills a hung binary. Every `OcrRun` records engine name, version, model
  hashes and the preprocessing version, so an engine or model change is a visible difference
  between runs.
- **Leases are renewed, and settled only by their holder (OCR-011).** After every page the
  worker renews the job's lease under the concurrency stamp; the lease must be at least twice
  the page timeout or the worker refuses to start. A worker that dies stops renewing and the
  lease expires. A worker whose lease was taken over while it worked sees a concurrency
  conflict at renewal or settlement, discards its attempt and removes the blobs it wrote.
- **Blobs never dangle (OCR-018).** Page images are written before the run row; if the run is
  not persisted the images are removed. A crash between the two leaves an orphan the sweep
  reconciles against the database (`OrphanBlobSweeper`, on a schedule): committed and
  referenced - never touched; unreferenced and younger than the grace window - staged, left;
  unreferenced and older - orphan, removed; interrupted `.partial` - removed when old.
- **One open job per document** is enforced at the database by a filtered unique index on
  `OcrJobs` and on `DocumentRenderJobs`, not only by the application's check.
- **Preprocessing is EMC code** (`SkiaImagePreprocessor`): orientation from the engine's OSD
  when it is confident, otherwise a vote between 0°/180° and, if needed, 90°/270° by summed word
  confidence; small-angle deskew by projection profile; scale to 300 DPI; grayscale; a percentile
  contrast stretch. Deterministic; versioned.
- **Output is a proposal.** Fields land in `ExtractedFields` with raw text, a normalized
  candidate, confidence, band and box. High-consequence fields (OCR-003) and everything below the
  High band require a person's verification; verifications are append-only rows; the raw text is
  never edited (OCR-004). Nothing here touches an accountability record: that is reconciliation,
  a separate, explicit step.
- **Logs carry categories, not content.** Job and document ids, page counts, durations, the
  failure category, an exception's type name. Never a word the engine read, never an engine
  message, never a filename.
- **The worker is not a user.** It holds no role and no permission and its `ICurrentUser` is
  unauthenticated with no grants; nothing it does goes through authorization because nothing it
  does is a user's act.

**Deployment (ACLs).** A dedicated service account: modify on `SourceDocuments:RootPath` (page
images are written there), on `SourceDocuments:RenderWorkRoot` and on `Ocr:WorkRoot`, and nothing
else on disk; execute on the Tesseract install folder; a SQL login with
SELECT on `SourceDocuments`, SELECT/INSERT/UPDATE on `DocumentRenderJobs` and `OcrJobs`,
SELECT/INSERT on `DocumentRenderRuns`, `DocumentRenderPages`, `OcrRuns`, `OcrRunPages` and
`ExtractedFields`; modify on `SourceDocuments:RootPath` (it writes page images there) and on
`SourceDocuments:RenderWorkRoot`. No `db_owner`, no DDL, no web-application
folders. Outbound network access for the account: none, enforced by host firewall policy — the
process needs none and asks for none.

### Classification boundary — a real risk, stated plainly

AR 195-5 delegates classified evidence entirely to **AR 380-5** (2-6h, 2-7k, 2-9r, 4-1a) and CI
storage to **AR 381-20** (4-2a(2)). EMC invents no classified-handling requirements.

But there is an architectural consequence the organization must confront: **a database of CI
evidence descriptions may itself be classified**, and if it is, the system's accreditation,
hosting enclave and backup handling all change. V1's design control is:

- every free-text field that could carry classified content has an explicit
  `ClassificationMarking` (default `UNCLASSIFIED`), and the UI carries a banner stating the
  system's accredited level, driven from `SystemConfiguration`;
- the accredited level is **configuration**, so the same code can be deployed into a higher
  enclave without change;
- the security boundaries (authentication, authorization, audit, storage) are clean enough that
  additional AR 380-5 controls can be layered without redesign of the application's boundaries.
  That is a statement about where the seams are, not a claim that nothing else would be needed:
  accreditation at a higher level, like any Army G-2X approval of an automated equivalent under
  2-5c, may require controls the organization specifies, and those are new work.

**This is open decision DEC-06 and it must be answered by the organization's security manager
before the system holds real data.** It is not a decision the application can make.

---

## 10. Logging discipline

Diagnostic logs record **identifiers and outcomes, never investigative content**:

```
Good:  ItemAccepted ItemId=8814 VoucherId=2201 ByUserId=17 EvidenceRoomId=3
Bad:   ItemAccepted Description="Samsung SM-S921U IMEI 356938035643809 seized from ..."
```

Evidence descriptions, case control numbers, serial numbers, IMEIs, names of persons and
disposition narratives are **domain data**. They belong in the database, under the application's
access control and audit. Diagnostic logs are rotated, copied to support staff and read by people
who have no need to know. A `SensitiveDataGuard` helper and a code-review checklist item enforce
this; log messages use structured identifiers only.

---

## 11. Deployment

Single IIS application, single SQL Server database, no other runtime dependency.

- **Application pool** runs as a domain service account with **no** interactive logon.
- The application's SQL login is granted `db_datareader` + `db_datawriter` + `EXECUTE` and
  **not** `db_owner` or `db_ddladmin` — so the running application cannot drop the append-only
  triggers it depends on.
- **Migrations are applied deliberately**, by a separate deployment step with a separate
  higher-privilege login (`dotnet ef database update`, or a generated idempotent script reviewed
  before it runs). The application **never** migrates on startup — silent schema change on an
  accountability system is unacceptable.
- Backups: SQL Server native, encrypted, restore-tested. Source documents live on a separate
  file share included in the same backup schedule.
- **No outbound network access is required** for any normal operation.

---

## 12. Testing

Regulatory and domain rules get tests, and each test names its requirement ID so the traceability
matrix can be verified rather than asserted:

- **Domain tests** (no database): item numbering within a voucher, state transition legality,
  document-number format, `LAST ITEM` and `POSSIBLE BIOHAZARD` validation, SCRCNI rules,
  appointment-window arithmetic, correction construction.
- **Application tests** (SQLite in-memory): append-only enforcement, field-level corrections,
  hash-chain continuity and break detection, authorization denials (**including the
  administrator-denial test for every accountability operation**), document-number uniqueness
  scoping, derived voucher status, audit-event emission.

A test that asserts a *regulatory* rule carries the AR 195-5 paragraph in its name or an
attribute, so that if the regulation is revised the affected tests are greppable.

---

## 13. What V1 deliberately does not build

OCR ingestion, reconciliation, DA Form 4137 generation, disposition workflow, full inventory and
inspection execution, long-term retention containers, digital-forensic metadata, and the suspense
dashboard are **designed** (`docs/domain-model.md`) and **specified** (traceability matrix) but
**not implemented** in the first slice.

The first slice exists to prove the event and correction model is right, because every one of
those features is built on top of it. Getting the event model wrong and discovering it after four
subsystems depend on it is the expensive failure mode this sequencing avoids.
