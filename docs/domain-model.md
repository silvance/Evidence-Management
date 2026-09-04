# EMC Domain Model

Companion to `docs/regulatory-requirements.md` (AR 195-5 extract) and `docs/architecture.md`.
Paragraph references in this document are to **AR 195-5, 25 August 2019**.

Labels follow the repository convention: **[REG]** regulatory, **[REG-REF]** deferred to another
authority, **[DESIGN]** project design decision, **[CONTROL]** integrity/security control.

---

## 1. The hierarchy

```
Case  (CI investigation, identified by case control number)                   2-3b
 └── EvidenceVoucher  (one DA Form 4137)                                      2-3a
      └── EvidenceItem  (one numbered line on the form)      ◄── unit of accountability
           └── ItemEvent  (append-only history)
                ├── CustodyEvent          2-3f, 2-7b
                ├── LocationEvent         2-4e  [DESIGN: history retained]
                ├── SealEvent             2-2a, 2-3e
                ├── ExaminationEvent      2-7c
                ├── StatusEvent           [DESIGN]
                ├── DocumentNumberEvent   2-4c, 2-7g
                └── CorrectionEvent       2-5b(5), 1-7c(3)
```

### Why `EvidenceItem` is the unit of accountability — the regulatory argument

1. **2-2f** — "each item will be sealed in its own separate container"; items under separate
   numbers "will not be sealed together." Physical identity is per item.
2. **2-1b** — one DA Form 4002 tag per item or container. Identity is tagged per item.
3. **2-5b(1)(d)** — "When a DA Form 4137 contains several items that are not disposed of on the
   same date, **the date of disposition for each item** will be shown opposite the item's
   description." Disposition is per item, in the regulation's own ledger format.
4. **2-4h** — the voucher becomes inactive only "after **all** items of evidence listed on a
   DA Form 4137 have been properly disposed." Voucher state is a **function of** item state.
5. **2-13b** — a long-term retention certificate identifies contents "by specific document number
   **and by citing the absence of specific item numbers**." Items are individually addressable
   even inside a sealed box.
6. **2-3j** — a portion of a single item can be extracted for outside-laboratory examination,
   tracked by item number.

Items on one voucher genuinely diverge: different locations, custody histories, temporary-release
states, examinations, disposition dates, disposition methods and authorities. A voucher-level
status column cannot represent that without losing information.

### Voucher status is derived — never stored as a maintained column

`EvidenceVoucher.DerivedStatus` is computed from its items:

| Derived voucher status | Rule |
|---|---|
| `Draft` | Voucher has never been submitted |
| `AwaitingCustodianAcceptance` | Submitted; no item accepted yet |
| `PartiallyAccepted` | Some but not all items accepted |
| `Active` | ≥1 item accepted and ≥1 item not in a terminal state |
| `Inactive` | **[DESIGN]** no active accountability remains in this room: every current form line is terminal (`Disposed`, `ReliefGranted`, or `PermanentlyTransferred`, terminal for the *sending* room). The **basis** is carried separately as `VoucherClosureBasis`: only `AllItemsFinallyDisposed` is the **2-4h** condition ("after all items ... have been properly disposed"); `AllItemsPermanentlyTransferred` is **2-7g** (the original goes with the evidence, the sending room keeps a copy); `AllItemsReliefGranted` is **3-3c** (relief "permits the closure of the DA Form 4137"); the mixed bases are named as such. The physical-document workflow uses the basis, never the status. A line withdrawn as entered in error (VCH-026) is not a form line |

**[DESIGN]** the *names*; **[REG]** the `Inactive` rule and the item-level derivation (2-4h).

---

## 2. Entity catalogue

Changes from the originally proposed entity list are marked **▲ CHANGED** or **✚ ADDED** with the
reason. Everything else keeps the proposed name and intent.

### 2.1 Identity and authority

| Entity | Notes |
|---|---|
| `User` | **▲ CHANGED — no credentials.** Keyed by AD `ObjectSid` (stable across renames) with UPN and display name. **No password, no hash, no secret.** Authentication is Windows Authentication (`docs/architecture.md` §8). A credential store would be a liability with no benefit. |
| `Role`, `UserRole` | Six roles (architecture §5.1). Roles are resolved **server-side per request from the database**; client-supplied role data is never read. |
| `CustodianAppointment` | **✚ ADDED. [REG] 1-4g(1), 1-4i, 1-7b, 1-7c.** The single most important addition. AR 195-5 vests custodial authority in **a person named in a written appointment**, not in a role: exactly **one primary and one alternate** at a time (1-4g(1)); the alternate acts only during the primary's temporary absence, defined as **more than 1 working day and not more than 30 consecutive days** (1-4i); emergency alternate orders **supersede** the previous alternate's (1-4i); appointment documents are retained while the position is held (1-7b). Fields: evidence room, user, `AppointmentType` (Primary/Alternate), `EffectiveFrom`/`EffectiveTo`, appointment-order reference, appointing authority, `SupersedesAppointmentId`, and an eligibility attestation for **1-7a(1)(c)** (credentialed CI agent; not probationary). Without this entity, "may this person accept evidence today?" is unanswerable. |
| `CustodianTransitionRecord` | **✚ ADDED. [REG] 1-7c(1), 1-7c(2), 3-2g(3).** The assumption, resumption and change-of-custodian statements that AR 195-5 requires to be **handwritten and signed in the evidence ledger**. EMC records that the paper entry was made (§6, attestations) and links the change-of-custodian joint inventory required by 3-2d. |

### 2.2 Case and voucher

| Entity | Notes |
|---|---|
| `Case` | CI **case control number** (2-3b). **▲ CHANGED:** a voucher may carry **two** case numbers — 2-3b requires that for evidence collected under a **request for assistance (RFA)**, "both the seizing and requesting offices['] law enforcement report number will be recorded." Modelled as `Case` (seizing/controlling) plus an optional `RequestingOfficeCaseNumber` on the voucher. |
| `EvidenceVoucher` | One DA Form 4137. Holds receiving activity, location, person/place from whom received, date/time received, preparing agent (**2-3b**: the agent who *first acquired* the evidence prepares the form), the temporary identifier, the current official document-number assignment, and the RFA requesting-office number. `DerivedStatus` is computed, never stored as maintained state. |
| `OfficialDocumentNumberAssignment` | **✚ ADDED (was a column). [REG] 2-4c, 2-7g.** A voucher can hold **more than one** document number over its life: on permanent transfer between evidence rooms the receiving custodian assigns the next number of the *receiving* room and the prior number is "lined through in such a way that it remains legible" (2-7g). A single column cannot represent a superseded-but-still-legible value. Fields: raw number, parsed `Sequence` + `CalendarYear`, evidence room, entering user, entry time, **`AttestedAssignedInAuthoritativeLedger`** (explicit custodian attestation, not inferred), and - on a later assignment - the supersession reason. Rows are append-only: a superseding assignment is a NEW row that names its reason, the earlier row is never updated, and "current" is derived from order. Unique on **`(EvidenceRoomId, CalendarYear, Sequence)`** across ALL rows, superseded or not (an unfiltered index, VCH-011): once a number is recorded it is consumed for good. |
| `EvidenceItem` | The numbered line on the form. `ItemNumber` unique within voucher, contiguous from 1. Description (**2-3d**), quantity/approximation, serial number, unique device identifier, `IsPossibleBiohazard` (**2-3l**), `IsFungible`, `IsSealed`, `IsCurrency` with denomination breakdown (**2-3d**), `AccountabilityStatus`, `IsLastItem` handling (**2-3d**). |

**`EvidenceItem` is one *numbered line*, whose quantity may exceed one** — 2-1b permits grouped
items ("a box containing tools") to be listed as one item with one DA Form 4002. See **AMB-04**.

### 2.3 Events

| Entity | Notes |
|---|---|
| `ItemEvent` (abstract) | **▲ CHANGED — TPH base type.** Common: `Id`, `EvidenceItemId`, `SequenceNumber` (per-item monotonic), `OccurredAtUtc` + `OccurredAtLocal` + `OccurredAtOffset`, `RecordedAtUtc`, `RecordedByUserId`, `Notes`, `SourceDocumentId`, `PreviousEventHash`, `EventHash`, `HashSchemaVersion`. Rationale in `docs/architecture.md` §4.1. |
| `CustodyEvent` | **[REG] 2-3f, 2-7b, 2-7e.** Releasing party, receiving party, purpose of change of custody, destination, agency, `IsScrcni` (**2-3e/2-3f**), source document. |
| `LocationEvent` | **[REG] 2-4e** for *current* location; **[DESIGN]+[CONTROL]** for retaining history — see §7. |
| `SealEvent` | **[REG] 2-2a, 2-3e, 3-2f.** `SealAction` (Sealed / Breached / Resealed), who, time/date across seals, MFR reference (**2-3e** requires an MFR affixed to the original form as a permanent attachment on custodian breach; **3-2f** requires an MFR from the supervisor directing a breach during inventory), directing supervisor. |
| `ExaminationEvent` | **[REG] 2-7c, 2-3j.** Laboratory, request reference (DD Form 2922), exhibit number, partial-extraction details, result reference. |
| `StatusEvent` | **[DESIGN].** Records each `AccountabilityStatus` transition with actor and reason so the workflow itself is auditable. |
| `DocumentNumberEvent` | **[REG] 2-4c, 2-7g.** Item-visible record that the voucher's official number was assigned or superseded. |
| `CorrectionEvent` | **[REG-modelled] 2-5b(5), 1-7c(3).** See §5. |
| `VoucherFormRevision` / `VoucherFormRevisionLine` | **✚ ADDED. [DESIGN] modelled on 2-3g.** What the form contained each time it went to the custodian. Append-only; SQL triggers. Lets the corrected form differ from the submitted one without erasing anything. Not the 2-5b(5) ledger procedure and not a 1-7c(3) correction. |
| `PhysicalFileContainer` | **✚ ADDED. [REG] 2-4f, 2-4h.** A paper folder/binder/file in the evidence room: active DA Form 4137 file (50-voucher limit, number range on the label), inactive file (month/year of disposition), or one of the three suspense folders. |
| `PhysicalVoucherDocument` / `PhysicalVoucherDocumentEvent` | **✚ ADDED. [REG] 2-4d, 2-4f, 2-4g, 2-4h, 2-7b, 2-7g.** Where the PHYSICAL original DA Form 4137 is for one voucher — filed active, out with released evidence (suspense copy retained), sent for disposition approval, filed inactive, transferred to the gaining room, or copy-only because the original is unavailable — plus the inactive date, the computed 3-year eligibility, and confirmed destruction. `PaperCopyKind` names the four pieces of paper 2-7b talks about (original, first copy, additional temporary-release copy, inactive retained copy); while copies are in use the record holds where the first copy is, how many copies are out and that the note was made (FIL-015). Voucher-level; independent of any scan; knows nothing of the case. Events append-only. |
| `VoucherReviewAction` | **✚ ADDED. [REG] 2-3g.** One step of the custodian's pre-acceptance review — submitted, returned (with what the custodian identified), corrected by the submitting agent (with what was corrected and the attestation that the paper form was corrected and initialed), resubmitted, accepted. Append-only; SQL trigger. `EvidenceVoucher.ReviewStage` is the one stored workflow state on the voucher; `IsSubmitted` is derived from it. Distinct from `CorrectionEvent`: that is the 1-7c(3) path for an accepted record. |
| `EvidenceRoomNumberingPolicy` | **✚ ADDED. [LOCAL]** How an evidence room writes its document numbers, effective-dated: layout (`SequenceThenYear` — the regulation's `001-26` — or `YearThenSequence`, `26-01`), sequence width, year width, separator, basis (`RegulationDefault` / `LocalAuthorized` with authority cited / `LegacyObserved`, flagged), notes. Structured, not a regex. The regulation's layout is the default when a room has recorded none. |
| `TemporaryIdentifierCounter` | **✚ ADDED. [CONTROL]** Last temporary-identifier ordinal issued per evidence room per date, with a concurrency stamp. Replaces `COUNT(*) + 1`. Not a regulatory number. |
| `AuditEvent` | **▲ CHANGED — narrowed.** Security/administrative audit **only**: sign-in, role change, permission denial, export, source-document download, integrity verification, configuration change. The accountability record lives in `ItemEvent`. Rationale: `docs/architecture.md` §4.5. |

### 2.4 Custody parties — a necessary correction to the original model

| Entity | Notes |
|---|---|
| `CustodyParty` | **✚ ADDED. [REG] 2-7b, 2-7e, 3-2g(5).** **A chain-of-custody counterparty is frequently not an EMC user**, so `ReceivedBy` must not be a foreign key to `User`. AR 195-5 contemplates counterparties that are: an internal user; an external person (trial counsel, civilian prosecutor, an Art. 32 investigating officer, a property owner); an organization (USACIL, AFMES/DFT, the US Secret Service, another agency); and — explicitly — **a registered or other accountable mail number**, which 2-7e directs be entered *in the Received By block*. 3-2g(5) adds the literal string **"N/A Custodian Unable to Sign"**. `CustodyParty` carries a `PartyKind` discriminator over exactly these cases. Forcing a `User` FK here would have made the regulation's own examples unrepresentable. |

### 2.5 Storage

| Entity | Notes |
|---|---|
| `StorageLocation` | **▲ CHANGED — hierarchical and kinded.** Self-referencing parent (Evidence room → container/shelf → bin) with a materialized path for display. `StorageLocationKind`: `EvidenceRoom` (4-1), `EvidenceDepository` (GSA safe, 4-1d), `TemporaryEvidenceFacility` (4-3), `ImpoundLotOrWarehouse` (2-6f), `LongTermStorageContainer` (2-13), `HighValueContainer`, `Shelf`, `Bin`. |
| `EvidenceRoom` | **✚ ADDED. [REG] 2-4c, 2-7g, 3-1, 3-2.** The accountability boundary. The document-number series, the custodian appointments, the inventory population, the monthly inspection and access scoping are **all per evidence room**. Retrofitting this key later would be painful; see **AMB-03**. |

> **A temporary release is not a storage location.** "Released to USACIL" is a *custody* state,
> not a place on a shelf. Conflating them is exactly the information loss the design avoids. An
> item on temporary release has a **custody** party and a suspense record; its last *physical
> storage* location remains its last known location until it returns.

### 2.6 Source documents (designed; not implemented in V1)

| Entity | Notes |
|---|---|
| `SourceDocument` | Immutable. Original file, original filename (as **data**, never as a path), **SHA-256**, received date, source, page count, document type, import status. |
| `ExtractedField` | **Raw extracted text AND verified normalized value, both retained permanently.** Confidence band `High`/`Medium`/`LowOrUnreadable`, `RequiresExplicitVerification`, verifying user, verification time. **OCR output never becomes accountability data without human verification.** |
| `ReconciliationFinding` | **✚ ADDED.** A detected difference between a scanned form and the companion record, with its resolution decision (`AddToRecord` / `MarkExtractionIncorrect` / `FlagForCustodianReview`), the deciding user, reason and time. **Never silently merge or overwrite.** Every decision is audit logged. |

**High-consequence fields requiring explicit verification even at high confidence:** evidence
document number, case control number, evidence item number, serial number, IMEI or comparable
unique device identifier, names in custody transfers, dates/times, currency amounts, disposition
information.

### 2.7 Suspense, disposition, retention (temporary release implemented; disposition and retention designed)

| Entity | Notes |
|---|---|
| `TemporaryRelease` / `TemporaryReleaseItem` / `TemporaryReleaseEvent` | **[REG] 2-7a, 2-7b, 2-7e, 2-4f(2), 2-4f(3). Implemented.** `SuspenseCategory` is exactly the regulation's folder names: **`Usacil`**, **`Adjudication`**, **`PendingDispositionApproval`** (2-4f(3)) — no fourth category is invented, and PENDING DISPOSITION APPROVAL is not a release of evidence (the original goes out for approval; the evidence stays). Item-level: each member item is tied to the `CustodyEvent` that took it out and the one that brought it back. Releasing custodian and recipient are `CustodyParty` rows (a person, an organization, or — for mail to the USACIL — an accountable mail number, 2-7e). When it left (local and UTC) apart from when it was recorded; purpose; destination; a LOCAL expected follow-up; the suspense folder the first copy went into; the five 2-7b paper **attestations** (inventoried, signed on the original, signed on the first copy, identification presented, obligations explained — records that paper acts occurred, never signatures); append-only events; return per item; closes when nothing is out. Recorded as ONE unit of work with the item custody/status events and the paper original leaving. Days out is a count. **No regulatory day limit exists** — see §8. |
| `SuspenseContact` | **✚ ADDED. [REG] 2-7a. Implemented.** Each follow-up contact: when, how, whom, outcome, narrative, next LOCAL follow-up. Append-only (SQL trigger). This is the evidence that 2-7a's contact obligation was met. |
| `DispositionRequest` / `DispositionAction` | **[REG] 2-8, 2-9.** Item-level. Workflow, not a boolean — see §4. |
| `DispositionAuthority` | **✚ ADDED. [REG] 2-8a-c, 2-8e.** *Which* authority may approve depends on case posture (known vs unknown subject, unfounded, unsolved, no-evidentiary-value, pre-evidence-room, permanent release to another agency). 2-8e(5) explicitly allows **more than one authority on a single DA Form 4137** via a continuation sheet. |
| `RetentionRule` | **✚ ADDED. [REG] 2-8c(2)(a), 2-15a.** Rules that **block** disposition: indefinite retention for unsolved homicide, rape, sexual assault, undetermined death, missing person, and any offense with no statute of limitations (2-8c(2)(a)); five years from date of seizure for unrestricted sexual assault physical/forensic evidence (2-15a). |
| `LongTermStorageContainer` | **✚ ADDED. [REG] 2-13.** Included **document numbers** and **excluded item numbers** (2-13b), custodian, **disinterested witness not in the chain of custody** (2-13a/b), certificate/MFR reference, seal events, container location, breach events. **Firearms excluded** (2-13c). **The container is packaging — it never becomes a new evidence item**, and the underlying DA Forms 4137 stay in the **active** file (2-13b). |
| `DigitalForensicReference` | **✚ ADDED. [DESIGN].** Accountability **metadata only**: device, forensic image identifier, SHA-256, external storage reference. **EMC never ingests bulk forensic data** — no disk images, no extractions, no case files. Bulk storage stays in the forensic environment. |

### 2.8 Inspection, inventory, discrepancy

| Entity | Notes |
|---|---|
| `Inspection` | **[REG] 1-4g(3), 3-1a, 3-1b(2).** Monthly. **For CI, an `Inspection` owns an `InventorySession`** — 3-1b(2) requires a **100 percent joint inventory** by the CI Commander/SAC **and** the Primary Evidence Custodian at the monthly inspection. Also records the 3-1a(1)-(4) determinations, including SF 700 verification (3-1a(2)) and the excessive-temporary-release check (3-1a(4)). |
| `InventorySession` | Participants, date/time, `InventoryType` (`MonthlyCiJointHundredPercent` (3-1b(2)), `ChangeOfPrimaryCustodian` (3-2d), `LossOrSecurityBreach` (3-2e), `NewSupervisorInitial` (3-1a)), expected population snapshot, exceptions, corrective actions, completion/attestation status. |
| `InventoryObservation` | **One row per expected item per session** — never a flag on the item. `ObservationOutcome`: `PhysicallyVerified`, `SealedContainerVerifiedNotBreached` (3-2f, 3-2b(1)), `AccountedForOnTemporaryRelease` (3-2b(1)(d), 3-1a(4)), `NotLocated` (3-3a), `UnexpectedlyPresent` (App B-4e(4)), `NotYetChecked`. Observer, time, notes. |
| `Discrepancy` | **[REG] 3-3a.** Opened date/time, affected evidence, discoverer, source session, **`RegulatoryResolutionDeadline` = 5 working days (3-3a)**, actions taken, resolution, MFR reference (3-3a requires corrective actions fully documented in an MFR attached to the DA Form 4137), escalation/inquiry status. **Never a `missing = true` flag.** |
| `Inquiry` | **[REG] 3-3b, 3-3c.** Initiated per AR 15-6 **[REG-REF]**. Reporting to **Army G-2X** for CI units (3-3b). Outcome including **relief for accountability granted** — for CI, **by Army G-2X** (3-3c) — which **permits closure of the DA Form 4137** (3-3c(1)) and **has no bearing on administrative or judicial action** (3-3c(2)). |

### 2.9 Configuration

| Entity | Notes |
|---|---|
| `SystemConfiguration` | `AuthoritativeMode` (`Companion` / `AuthoritativeLedger`), `NumberingMode` (`ManualTranscription` / `SystemAssigned`), accredited classification level and banner text, evidence-room defaults, **local** suspense review thresholds (explicitly **not** regulatory), duty-calendar selection for working-day arithmetic. |
| `DutyCalendar` | **✚ ADDED. [REG-dependent] 2-4a, 3-3a.** Both the turn-in expectation and the **5-working-day** inquiry clock require knowing which days are working days. AR 195-5 does not define the term — see **AMB-02**. Guessing a definition inside business logic would silently produce wrong deadlines on a rule that has real consequences. |

---

## 3. State: four independent axes

Collapsing these into one status loses information, so EMC keeps them separate.

### 3.1 `AccountabilityStatus` — workflow

```
Draft
  └─► Acquired                     2-1a, 2-3b  agent has custody, form being prepared
        ├─► TemporaryStorage       4-3a        secured during non-duty hours
        └─► AwaitingCustodian      2-4a        submitted; NLT first working day
              └─► InEvidenceRoom   2-4c        custodian accepted; document number assigned
                    ├─► TemporarilyReleased ──► InEvidenceRoom          2-7a, 2-7b
                    ├─► DispositionPending ──► Disposed                 2-8, 2-9
                    ├─► DiscrepancyReview ──┬─► InEvidenceRoom (resolved)   3-3a
                    │                        └─► Inquiry                     3-3b
                    │                              ├─► InEvidenceRoom (recovered)
                    │                              └─► ReliefGranted         3-3c
                    ├─► LongTermRetention ──► InEvidenceRoom            2-13
                    └─► PermanentlyTransferred                          2-7g
```

Refinements from the originally proposed names, with reasons:

- **`ReliefGranted` added — [REG] 3-3c.** The proposal ended an unresolved inquiry at
  "appropriate final status." AR 195-5 names the outcome: relief for accountability, granted for
  CI units by **Army G-2X**, which **permits the closure of the DA Form 4137**. It is a terminal
  accountability state distinct from `Disposed` — the item was never disposed of; accountability
  for it was relieved. Merging the two would misstate the record.
- **`PermanentlyTransferred` added — [REG] 2-7g.** Transfer to another evidence room is terminal
  *for this room's accountability* but is not disposition. The receiving room assigns its own
  document number.
- **`LongTermRetention` added — [REG] 2-13.** Items remain accountable and their vouchers stay
  in the **active** file (2-13b), but they cannot be visually verified at inventory because the
  box "will not be opened to conduct inventories, unless tampering is evident or a competent
  authority so directs" (2-13d).
- **`ACQUIRED` retained**, matching 2-1a/2-3b.

Terminal states: `Disposed`, `ReliefGranted`, `PermanentlyTransferred` (terminal for the sending
room only - the evidence continues under the receiving room's number), and
`WithdrawnAsEnteredInError` (pre-acceptance only; never a physical item, VCH-026).

### 3.2 `CustodyState` — derived, never stored

From the latest non-superseded `CustodyEvent`: who holds the item now, under what purpose. AR
195-5's glossary defines chain of custody as "a chronological written record reflecting the
release and receipt of evidence from initial acquisition until final disposition" — a sequence,
which is what the event log is.

### 3.3 `PhysicalLocation` — derived, never stored

From the latest non-superseded `LocationEvent`. Distinct from custody: an item can be in
`Shelf B / Bin 14` while custody sits with the custodian, and an item on temporary release has a
custody party but no current *storage* location.

### 3.4 `DispositionState` — derived, never stored

From `DispositionRequest` / `DispositionAction`. Item-level (2-5b(1)(d)).

---

## 4. Disposition as a workflow

**[REG] 2-8, 2-9, 1-4h(5).** Never `Disposed = true`.

```
1  Disposition requested                          item-level
2  SJA coordination documented                    2-8 opening — required prior to disposition
3  Approving authority identified                 2-8a-c, 2-8e — depends on case posture
4  Final Disposal Authority documented            2-8e(5); completed before signature, 1-4h(5)
5  Physical disposal / release occurs             2-9; witness physically views the items
6  Custody / disposition event created            2-3f
7  DA Form 4137 documentation confirmed           2-8 opening
8  Case-record documentation confirmed            2-8 opening — hard copy AND online case records
9  Item closed                                    → voucher inactive when ALL items closed, 2-4h
```

Items on one voucher may be disposed on different dates, under different authorities, by
different methods — 2-5b(1)(d) and 2-8e(5) both contemplate this directly.

---

## 5. Immutable versus mutable

### Immutable — INSERT ONLY: never updated, never deleted

`ItemEvent` and all subtypes · `AuditEvent` · `InventoryObservation` (once the session is
completed) · `OfficialDocumentNumberAssignment` · `SourceDocument` · `AttestationRecord` ·
`SuspenseContact` · `DispositionAction`

**There is no permitted mutation at all.** Every update and delete is rejected at three layers
(`docs/architecture.md` §4.2).

An earlier design allowed exactly one — a forward "superseded by" pointer — which forced the
database trigger to prove every *other* column was unchanged. In a table-per-hierarchy table that
is easy to get wrong, and the trigger in fact compared only the columns common to all event types:
subtype columns such as `StorageLocationPath` and `PurposeOfChangeOfCustody` could be rewritten
alongside a legitimate supersession and pass. Backward references removed the exception, and the
triggers are now unconditional.

### Mutable — with concurrency control and full audit

`EvidenceItem` while `Draft` (2-3g contemplates the custodian having the agent correct errors
before acceptance) · `Case` header · `EvidenceVoucher` header while `Draft` · `StorageLocation`
definitions · `User`, `Role`, `UserRole` · `SystemConfiguration` · open `Discrepancy` /
`Inquiry` working fields

**Once a voucher leaves `Draft`, item accountability fields become append-only.** After that,
change happens only through a `CorrectionEvent`.

### Derived — computed, never stored as maintained state

`EvidenceVoucher.DerivedStatus` · `CustodyState` · `PhysicalLocation` · `DispositionState` ·
inventory totals · suspense age

### The correction pattern

Corrections are **field-level**, the original value is **derived by the server**, the reference
is **backward only** — so no accountability row is ever updated — and a field that names a row is
corrected by naming the **replacement row**, not by supplying replacement text.

Prohibited:

```
UPDATE CustodyEvents SET ReceivedBy = 'Jones' WHERE Id = 219;   -- destroys the record
```

Required:

```
CustodyEvent #219   ReceivedByPartyId = 88 ("Smith")   ← untouched, still readable
CorrectionEvent #402
    CorrectsEventId      = 219
    FieldName            = "ReceivedBy"
    OriginalValue        = "Smith"                ← derived from #219, never posted
    CorrectedValue       = "Jones"                ← read from CustodyParty 91, never posted
    ReferenceKind        = CustodyParty
    OriginalReferenceId  = 88
    CorrectedReferenceId = 91                     ← the replacement ROW
    PreviousEffectiveValue       = "Smith"        ← what THIS correction changed
    PreviousEffectiveReferenceId = 88             ← (= original here; differs in a chain)
    Reason               = "Transcription error; DA Form 4137 shows Jones"
    CorrectedByUserId    = 17
    OccurredAtUtc        = 2026-09-03T14:22:11Z
    MfrReference         = "MFR-2026-014"         ← 1-7c(3), REQUIRED for this category
    SupervisorNotifiedUserId     = null           ← 1-7c(3); the supervisor has no EMC account
    SupervisorNotifiedName       = "RIVERA, LUIS M."
    SupervisorNotifiedGradeOrTitle = "CW3"
    SupervisorNotifiedOrganization = "902d MI Group"
    SupervisorNotifiedAtUtc      = 2026-09-03T14:31:02Z   ← REQUIRED for this category
```

**Fields that name a row carry the identifier, not just the text.** An item's storage location
and a change of custody's parties are rows. A correction to one of them supplies the replacement
identifier and the server reads the display text *from that row*, so the two can never disagree.
A text-only correction to a location would change what the history displayed while every
projection built on `StorageLocationId` still pointed at the location just declared wrong — an
inventory of the new bin would not have listed the item the record said was in it. The
evidence-room check that governs assigning a location (invariant I-08) governs correcting one
identically. **[DESIGN]** — AR 195-5 knows nothing of identifiers; it is a form on which a
location is written in pencil (2-4e). The requirement is EMC's own, because EMC's own projections
depend on it.

Modelled on **2-5b(5)** — an erroneous ledger entry is struck through with one line **so it may
still be read** and initialled; correction fluid, tape, labels and erasures are prohibited — and
**1-7c(3)** — the discovering custodian **immediately informs the supervisor** and prepares an
**MFR** stating the error and corrective action, filed with the DA Form 4137 and copied to the
case file.

The UI shows the current interpretation cleanly, marked **"Corrected"**, with the original one
click away. An auditor can always see the original history.

> **Honesty note.** 2-5b(5) governs the paper **ledger**. AR 195-5 contains no general
> append-only rule for electronic records, because it does not contemplate a general electronic
> record. EMC's event store is **[DESIGN] + [CONTROL] modelled on** 2-5b(5) and 1-7c(3). Do not
> describe it as an AR 195-5 mandate.

---

## 6. Attestations

**[REG] 2-5b(2), 2-7b, 3-1b(2), 3-2g, 2-8e(5)** — all require **handwritten signatures**.

`AttestationRecord` asserts *"the paper certification required by AR 195-5 para X was executed on
`<date>` by `<person>`"*. It stores the attesting user, timestamp, paragraph reference and the
regulation's prescribed statement text. **It is not a signature and the UI never calls it one.**

---

## 7. Location history — an explicit design decision

**AR 195-5 2-4e says: location is recorded *in pencil* in the location block of the DA Form 4137,
and "location changes in the evidence room will be kept current by *erasing the previous entry*
and noting the new location."**

The regulation therefore requires the **current** location and explicitly contemplates that the
previous one is **erased**.

EMC retains the full history anyway, as **[DESIGN] + [CONTROL]**, because it materially improves
inventory reconstruction, discrepancy investigation (3-3a) and inquiry support (3-3b). Two
consequences must be stated wherever this is discussed:

1. **EMC must not claim AR 195-5 requires location history.** It does not.
2. **EMC's history may legitimately diverge from the paper form**, which by design shows only the
   current location. A divergence here is **not** evidence that the form is wrong.

```
03 SEP 26 09:15  Intake                      LocationEvent  #1
03 SEP 26 09:31  Shelf B / Bin 14            LocationEvent  #2
19 OCT 26 13:22  High-Value Safe / Drawer 2  LocationEvent  #3
04 JAN 27 08:41  (temporarily released)      CustodyEvent   #4   ← custody, not location
22 JAN 27 15:17  Shelf B / Bin 19            LocationEvent  #5
```

---

## 8. Suspense ageing — no invented deadlines

**AR 195-5 gives no numeric limit** for any temporary-release category. It requires "reasonable
and adequate contact" (2-7a) and that evidence not be released "for an excessive period"
(2-7b, 3-1a(4)).

EMC therefore shows **days out** and applies **locally configured review thresholds**, labelled as
local management thresholds:

```
017-26   USACIL                          Out      94 days     ⚑ exceeds local review threshold (60)
021-26   ADJUDICATION                    Out      61 days     ⚑ exceeds local review threshold (60)
034-26   PENDING DISPOSITION APPROVAL    Paper    23 days
```

The flag reads *"exceeds local review threshold"* — never *"exceeds AR 195-5 deadline."* There is
one threshold for the room (`SystemConfiguration.LocalSuspenseReviewThresholdDays`, default 60),
not one per folder: the regulation names no number for any of them, so EMC invents none. The
PENDING DISPOSITION APPROVAL row is a **paper** state — the original DA Form 4137 is out with trial
counsel for approval and the evidence is still in the room (2-4f(3)(c)) — and comes from
`PhysicalVoucherDocument`, not from a `TemporaryRelease`. `SuspenseContact` rows are the record
that 2-7a's contact obligation was met; the custodian's own next follow-up date on a release or a
contact is a local management date and is shown as due, never as overdue.

The dashboard also carries the SUSP-017 advisories (SCV-001..008): release records checked against
item states, the chain of custody, the paper record and the folders. They are read-only findings
for a person; none of them changes a state.

**The one real deadline in this area is elsewhere: 3-3a's 5 working days** to resolve
apparently-missing evidence before an inquiry is initiated. That one is regulatory, is modelled on
`Discrepancy.RegulatoryResolutionDeadline`, and depends on the duty calendar (**AMB-02**).

---

## 9. Invariants

Enforced by database constraints, EF Core configuration and domain guards — not by UI validation
alone.

### Structural

| # | Invariant | Basis |
|---|---|---|
| I-01 | `EvidenceItem.ItemNumber` unique within a voucher, contiguous from 1 | 2-3d **[REG]** |
| I-02 | A voucher has ≥1 item before it may be submitted | 2-3a **[REG]** |
| I-03 | Official document number matches `^\d{3}-\d{2}$` | 2-4c **[REG]** |
| I-04 | Official number unique on `(EvidenceRoomId, CalendarYear, Sequence)` among non-superseded rows | 2-4c, 2-7g **[REG]** |
| I-05 | At most one **non-superseded** `OfficialDocumentNumberAssignment` per voucher | 2-7g **[REG]** |
| I-06 | At most one active `Primary` and one active `Alternate` appointment per evidence room at any instant | 1-4g(1) **[REG]** |
| I-07 | `ItemEvent.SequenceNumber` unique and gapless per item | **[CONTROL]** |
| I-08 | `LocationEvent.StorageLocationId` must resolve within the item's evidence room | **[DESIGN]** |

### Behavioural

| # | Invariant | Basis |
|---|---|---|
| I-10 | Items may be added or edited only while the voucher is a draft or has been returned by the custodian under 2-3g; a submitted line is never deleted — on a returned form it is withdrawn as entered in error, with the agent's attestation that no physical item corresponds to it | 2-3g, 2-8a **[REG]** |
| I-11 | Only a user with an **active `CustodianAppointment`** for that evidence room may accept evidence, transcribe the document number, or assign a location | 1-4g(1), 1-4i, 2-4c **[REG]** |
| I-12 | An item cannot reach `InEvidenceRoom` without an official document number on its voucher | 2-4c **[REG]** |
| I-13 | `ApplicationAdministrator` alone grants **no** accountability permission | **[DESIGN]** |
| I-14 | No `ItemEvent` may be updated or deleted, without exception | 2-5b(5) modelled **[CONTROL]** |
| I-23 | A correction's original value is derived from the stored event, never accepted from the client | **[CONTROL]** |
| I-24 | Current-state projections use **effective** values, so a corrected field reads its corrected value rather than being excluded | 2-5b(5) **[CONTROL]** |
| I-25 | A canonical `(EvidenceRoom, CalendarYear, Sequence)` is never reused, including by superseded assignments | 2-4c, 2-7g **[REG]** |
| I-26 | Only the administrator role may be granted globally; operational roles name an evidence room | **[DESIGN]** |
| I-27 | An alternate custodian acts only during an open duty-assumption period | 1-4i **[REG]** |
| I-28 | An alternate custodian's authority ends when the primary's absence exceeds 30 consecutive days; there is no override | 1-4i, 3-2d **[REG]** |
| I-29 | A change of primary custodian is incomplete until the joint inventory is recorded with discrepancies resolved and the ledger statement entered | 3-2d, 3-2g(3) **[REG]** |
| I-30 | A correction to a field that names a row carries the replacement identifier, and its display text is read from that row | **[DESIGN]** |
| I-31 | A corrected storage location resolves within the item's own evidence room, on the same terms as an assigned one | 2-4c, 2-4e **[DESIGN]** |
| I-32 | A correction records the value it changed as well as the original; which correction to a field is current is decided by server-assigned append order, never by a user-supplied occurrence time | 1-7c(3), 2-5b(5) **[CONTROL]** |
| I-33 | A voucher's review stage moves only Draft → Submitted → (Returned → Corrected → Resubmitted)* → Accepted; each step is an append-only `VoucherReviewAction` with actor and time | 2-3g, 2-4c **[REG]** |
| I-34 | Only the submitting agent records the correction to, or resubmits, a returned voucher; an accepted voucher is never returned | 2-3g **[REG]** |
| I-35 | EMC records the agent's attestation that the paper form was corrected and initialed; it supplies no initials | 2-3g, 2-5c **[REG]** |
| I-36 | A document number's identity is `(EvidenceRoom, CalendarYear, Sequence)`; the text as written is presentation under the room's numbering policy and is preserved verbatim | 2-4c **[DESIGN]** |
| I-37 | The four-digit calendar year is resolved from the date of receipt when the number is recorded and is never re-derived from the clock; a disagreement is confirmed by the custodian, not guessed | 2-4c **[CONTROL]** |
| I-38 | Only the regulation's layout may be recorded as the regulation default; any other layout cites a local authority or is flagged as awaiting validation | 2-4c **[LOCAL]** |
| I-42 | Each submission of a DA Form 4137 takes an immutable snapshot of its lines (`VoucherFormRevision`); the current corrected form is the voucher's non-withdrawn items. Item numbers are never reassigned after a withdrawal | 2-3g **[DESIGN]** |
| I-43 | A new evidence-room location is recorded only for an item physically in the room; released and missing items re-enter the location workflow only through their regulatory state transitions | 2-4e, 3-3 **[REG]** |
| I-44 | The physical DA Form 4137 record is voucher-level and independent of any scan; a scan's provenance says what paper was scanned, the paper record says where the paper is, and neither changes the other | 2-4d, 2-4f **[DESIGN]** |
| I-45 | An active folder/binder holds at most 50 vouchers; an inactive file is labeled by disposition month and year | 2-4f(1), 2-4h **[REG]** |
| I-46 | Paper destruction eligibility is exactly three years from the date the record became inactive, depends on no case fact, is never acted on automatically, and never applies to digital records | 2-4h **[REG]/[CONTROL]** |
| I-39 | Whether an item has been received by the custodian, and whether it may hold a location, are named predicates on the state machine; no code compares `AccountabilityStatus` by numeric order | **[DESIGN]** |
| I-40 | `EvidenceItem.AccountabilityStatus`, `LastEventSequenceNumber` and `LastEventHash` are derived summaries of the events and are verified against them; a disagreement is a snapshot mismatch, reported apart from a chain failure | **[CONTROL]** |
| I-41 | A temporary identifier is issued from a per-room, per-date counter under optimistic concurrency; two drafts never share one | **[CONTROL]** |
| I-15 | A `CorrectionEvent` must carry a reason; a post-acceptance correction must also carry the MFR reference and the supervisor notification, and is refused without them | 1-7c(3) **[REG]** |
| I-16 | Voucher status is always computed; there is no settable status column | 2-4h **[REG]** |
| I-17 | Terminal-state items accept no further custody or location events except corrections | **[DESIGN]** |
| I-18 | A disposition action requires a recorded approving authority | 2-8e(5), 1-4h(5) **[REG]** |
| I-19 | An item under an active `RetentionRule` cannot be disposed | 2-8c(2)(a), 2-15a **[REG]** |
| I-20 | A long-term container's disinterested witness must not appear in the chain of custody of any contained item | 2-13a, 2-13b **[REG]** |
| I-21 | A controlled-substance destruction witness must not be in the chain of custody, and an alternate custodian who held the room while the evidence was in it is ineligible | 2-9c **[REG]** |
| I-22 | Every regulated action writes an attributable event: user, timestamp, action type, affected record, previous value, new value, reason | **[CONTROL]** |

**Invariants implemented in the first vertical slice:** I-01 – I-08, I-10 – I-14, I-16, I-22.
The remainder are specified here and implemented with their subsystems.

---

## 10. Time

DA Form 4137 and the ledger use **local** date/time (`03 SEP 26 09:15`). Army CI operates across
time zones and a UTC-only store would misrepresent what the paper form says.

Every event therefore stores **three** values: `OccurredAtUtc` (ordering and arithmetic),
`OccurredAtLocal` + `OccurredAtOffset` (what the paper form says), and `RecordedAtUtc` (when EMC
learned of it). Displays use the evidence room's configured zone. Ordering always uses
`OccurredAtUtc`, then `SequenceNumber` as the tie-break.

`RecordedAtUtc` distinct from `OccurredAtUtc` matters: back-dated entry is legitimate and common
(a custody transfer at 0200 recorded at 0800), and an auditor must be able to see both.

---

## 11. First vertical slice

**Implemented:** `User`, `Role`, `UserRole`, `EvidenceRoom`, `CustodianAppointment`, `Case`,
`EvidenceVoucher`, `OfficialDocumentNumberAssignment`, `EvidenceItem`, `StorageLocation`,
`ItemEvent` (+ `CustodyEvent`, `LocationEvent`, `StatusEvent`, `DocumentNumberEvent`,
`CorrectionEvent`), `CustodyParty`, `AuditEvent`, `SystemConfiguration`.

**Designed here, not implemented:** OCR ingestion, reconciliation, DA Form 4137 generation,
disposition, inventory and inspection execution, long-term retention, digital-forensic metadata,
suspense dashboard.

The slice exists to prove the event and correction model, because every deferred subsystem is
built on it.
