# Slice report — temporary release, chain of custody, suspense and return; review defects; rendering and OCR worker hardening

Written at the close of the slice that began after commit `ab77c96`. Everything below is what
the repository does now; nothing is projected. Labels: **[REG]** AR 195-5 requires it; **[REG-REF]**
AR 195-5 defers it to another authority; **[DESIGN]** / **[CONTROL]** / **[LOCAL]** the program's
own choices, which AR 195-5 neither requires nor forbids.

**Final SHA:** the commit carrying this report (its parent is `3abebf9`). The branch is
`claude/army-evidence-management-design-4omsf2`, the repository's only branch.

## 1. Commits in this slice (oldest first)

| Commit | What |
|---|---|
| b827516 | Paper record on two axes (where the original is; what the room retains); closure basis; active-file ranges, forms and concurrency-safe capacity |
| c32a0e5 | Repoint traceability rows at the renamed paper-record tests; restore the 2-4g reason test |
| b028257 | Reconciliation patches one field only; decisions bound to run and values; conflicts block; document ownership |
| a65ec7a | Move PDF rendering out of IIS: render jobs, killable child-process rasterizer, immutable render runs |
| 69655dd | Worker hardening: renewed leases, blob unwind and orphan sweep, DB one-open-job invariant, approved-hash start-up check, Windows Service deployment |
| 4403109 | Temporary release: item-level aggregate, real custody events, paper attestations, one-transaction orchestration, suspense contacts |
| 6d5cb92 | Physical copies and multi-recipient release (2-7b, SUSP-008, FIL-015) |
| 84cffca | Return from temporary release, laboratory and shipping cases, controlled-substance apparent change, not-returned accounting |
| 07d898e | Reconciliation routes a verified custody row to the custody workflow by explicit custodian action (REC-010) |
| 3abebf9 | Suspense dashboard, release/companion consistency advisories, the release, return and custody pages |
| (this) | SQL lane, authorization matrix and citation regression; this report |

## 2. Review defects found and fixed

| Defect | Where | Fix |
|---|---|---|
| The paper record collapsed "where is the original" and "what does the room retain" into one state, so a copy retained under 2-4g looked like an original | `PhysicalVoucherDocument` | Two axes: `OriginalDisposition` and `RetainedPaperStatus`; the closure basis names why a voucher's paper closed |
| Container capacity (50 per folder, 2-4f(1)) was checked by a count that two concurrent filings could both pass | filing | Counter on the container under its concurrency stamp; a binder carries its number range and form |
| Reconciliation could apply a whole-record patch, and a decision was not bound to the run and values it decided | reconciliation | One field per decision, bound to the OCR run and the two values; a changed value invalidates the decision; cross-page conflicts block; a document is owned by one voucher |
| PDF rendering ran inside the IIS worker process (a malformed PDF could take the site down) | web | Rendering moved to `Emc.OcrWorker`: `DocumentRenderJob` (leased), `DocumentRenderRun` and `DocumentRenderPage` (append-only); the rasterizer is a killable child process with a hard timeout; the web host registers no rasterizer at all (DOC-014) |
| An OCR or render job whose worker died kept its lease for the whole timeout, and a crash between blob write and commit left orphaned page images | worker | Lease renewed per page; blobs unwound on failure; a `.partial` protocol for every write; orphan sweep at start and daily; the "one open job per document" invariant is a filtered unique index, not an application check |
| Tesseract's version probe could hang the worker | worker | Bounded probe with kill; approved-hash verification happens before any execution, and the worker refuses to start without approved hashes |
| Reconciliation detected custody rows on a scan (REC-003) but nothing could record them | reconciliation, custody | `ICustodyEventService.RecordHistoricalCustodyEventAsync` (REC-010): the custodian's explicit act, the paper's date as OccurredAt, the finding consumed once |
| The state-machine comment said disposition approval takes the evidence out of the room | domain | 2-4f(3)(c): the ORIGINAL form goes for approval; the evidence stays; comment corrected and the dashboard models it as a paper state |
| "The paper remains authoritative" was cited to 2-5c, which is the automated-systems approval paragraph | OCR warning, consistency service, docs | Cited to the paragraphs that make the original the custodian's record: 2-4d, 2-4f, 2-4f(2), 2-4g, 2-4h, 2-7b, 2-7g; 2-5a for the ledger; 2-5c kept only for the companion posture |

## 3. Temporary-release model

`TemporaryRelease` (aggregate, `Emc.Domain.Suspense`): voucher, evidence room, category
(**USACIL**, **ADJUDICATION**, **PENDING DISPOSITION APPROVAL** — exactly 2-4f(3), and the third is
never a release of evidence), released-by and received-by `CustodyParty`, purpose, destination,
`ReleasedAtLocal` + `ReleasedAtUtc` apart from `RecordedAtUtc`, the suspense folder that holds the
first copy, which paper accompanied the evidence (`PaperCopyKind.Original` or
`AdditionalTemporaryReleaseCopy`), the five 2-7b attestations (`PaperReleaseAttestations`), the
laboratory details for the USACIL category (`LaboratorySubmission`: laboratory name, USACIL
coordination attested for any other laboratory, DD Form 2922 reference, shipping document
reference), the custodian's own `ExpectedFollowUpLocal`, notes, status Open/Closed.

- `TemporaryReleaseItem` per item: `Out`, `Returned`, `NotReturnedAccountedFor`, tied to its
  release and return `CustodyEvent`s. An item is out on at most one release
  (`UX_TemporaryReleaseItems_OneOpenPerItem`, filtered unique).
- `TemporaryReleaseEvent` (append-only): Released, ItemReturned, ItemAccountedForWithoutReturn,
  Note, Closed. `SuspenseContact` (append-only): the 2-7a record — when, how, whom, outcome,
  narrative, next LOCAL follow-up.
- `DaysOut` is a count. No property is named or shown as a deadline; the one threshold is
  `SystemConfiguration.LocalSuspenseReviewThresholdDays` (default 60), labelled LOCAL.
- Not-returned accounting: entered in the record of trial (final disposition, 2-8e(4)) or consumed
  / retained by the laboratory (2-7c(2), MFR reference required); the item moves to
  DispositionPending and the disposition workflow (not built) owns the rest.
- Controlled substance (2-7d): an apparent change on return is annotated in Purpose of Change of
  Custody with an MFR reference, refused after a laboratory release.

## 4. Custody model

Every release and return writes a real `CustodyEvent` on each item's chain, in sequence, hashed
into the chain: releasing custodian → recipient on release (SCRCNI for a sealed item, agency,
destination), returner → custodian on return, followed by the `StatusEvent`. The recipient may be
a person, an organization, or an accountable mail number (USACIL only, 2-7e); the USACIL's
mail number on the way back stands in Released By. A laboratory release also appends an
`ExaminationEvent`. `CurrentCustodyHolder` on the item history is derived from the chain and
equals the recipient while the item is out.

Historical custody rows the paper shows and the companion lacks are recorded through
`ICustodyEventService` by an appointed custodian only, with the scan as provenance and the
reconciliation finding consumed once (REC-010). It is not a correction (no 1-7c(3) MFR) and never
a temporary release.

## 5. Paper model

`PhysicalVoucherDocument` now carries the original's disposition, the retained paper status, the
first copy's folder, the count of additional copies out, and whether the note that copies were
made was recorded. Release takes the original (2-4f(2), 2-7b) and files the first copy in the
folder of the release's category; while the original is out a further recipient takes a copy
(2-7b), and several recipients at once use copies with the original staying in its binder,
annotated — the regulation is silent on where the original sits in that case, so this is
**[DESIGN]**, flagged FIL-015. On return the original goes to its active file and the first
copy is filed with it, or a returned copy's chain goes onto the first copy, which stays in
suspense until the last copy is back. The PENDING DISPOSITION APPROVAL folder holds the copy while
the original is with trial counsel; the evidence never leaves for it.

## 6. Reconciliation integration

Reconciliation applies one field per decision, to a draft only, with decisions bound to the OCR
run and the values decided; a document belongs to one voucher; cross-page conflicts block.
Custody rows are routed, not applied: the finding shows a hand-off link that opens the item
history page with the paper's parties, date and purpose prefilled, and nothing is recorded until
the custodian posts the custody form.

## 7. Suspense dashboard and consistency advisories

`/Suspense/Dashboard/{roomId}` (ViewVoucher, room-scoped): USACIL and ADJUDICATION rows from
open releases; PENDING DISPOSITION APPROVAL rows from the paper record; releases closed in the
last 30 days; SCV-001..008 advisories (item state vs release, chain holder vs recipient, original
and copies vs the paper record, folder vs first-copy folder, folder kind vs category). Advisories
change nothing and also appear on the voucher's physical/digital consistency report.

## 8. Migrations and schema

Regenerated from scratch, as this repository does: `InitialEvidenceModel` and
`AppendOnlyTriggers`; `db/schema-v1.sql` regenerated. 39 tables, 34 append-only triggers, error
numbers 50001–50034. Tables added in the slice: `DocumentRenderJobs`, `DocumentRenderRuns`,
`DocumentRenderPages`, `TemporaryReleases`, `TemporaryReleaseItems`, `TemporaryReleaseEvents`,
`SuspenseContacts`. Removed: `SourceDocumentPages` (page images belong to a render run now).
Mutable with concurrency stamps: `DocumentRenderJobs`, `OcrJobs`, `TemporaryReleases`,
`TemporaryReleaseItems`, `PhysicalVoucherDocuments`, `PhysicalFileContainers`. Filtered unique
indexes: one open render job and one open OCR job per document, one open release item per item.

## 9. Worker and deployment

`Emc.OcrWorker` renders (child process `render info|page`) and runs OCR, as a Windows Service
(`EmcOcrWorker`) under a dedicated service account with the Windows Event Log as its sink. It
refuses to start without a render helper path or, by default, without approved artifact hashes;
it verifies `tesseract.exe` and the model files against those hashes before any execution.
Scripts in `scripts/deploy/`: `Test-EmcOcrWorkerPrerequisites.ps1`, `Set-EmcOcrWorkerConfig.ps1`
(takes the installed-file hash from the reviewed manifest's `installedFiles`),
`Install-EmcOcrWorker.ps1`, `Uninstall-EmcOcrWorker.ps1`; procedure in
`docs/ocr-worker-deployment.md`. **Not executed here** (no Windows, no `pwsh`).

## 10. Dependencies and bundle

One package added: `Microsoft.Extensions.Hosting.WindowsServices` 10.0.11 (worker only). No
other package, no native dependency, no URL. The artifact manifest gains `installedFiles` so the
hash of the installed engine binary, not only the installer, is reviewed and pinned. Everything
is restored from the offline bundle; nothing is fetched at run time.

## 11. Tests

| Project | Passed | Skipped | Notes |
|---|---|---|---|
| Emc.Domain.Tests | 292 | 0 | |
| Emc.Application.Tests | 251 | 11 | the 11 are the SQL Server lane, opt-in |

New in the slice: `TemporaryReleaseTests`, `TemporaryReleaseServiceTests`,
`SuspenseDashboardTests`, `SuspenseHttpTests`, `ReconciliationCustodyTests`,
`ReconciliationPatchTests`, `DocumentRenderIsolationTests`, `OcrLeaseAndBlobTests`, the paper
record's two-axis tests, and the authorization matrix extended with the custody, release and
return columns. The doc-reference checker resolves every test name cited in the traceability and
regression documents.

## 12. SQL Server lane

Extended with every new trigger (50029–50034), the three filtered unique indexes, and a table
existence check over every accountability table. **Not executed against a real instance in this
environment** (none available). It runs offline against an approved local instance under the
release procedure.

## 13. Open decisions and things deliberately not built

- **DEC-09 decided:** one `TemporaryRelease` entity; the voucher-page form, the multi-recipient
  request and the release page are its entry points.
- **[LOCAL]** the review threshold value (60 days by default) and the 30-day "recently closed"
  window on the dashboard are local settings, not regulatory.
- **[DESIGN]** where the original sits during a simultaneous multi-recipient release (its binder,
  annotated); 2-7b says only that copies are used.
- Not built, by the brief: full disposition, monthly inspection and 100% joint inventory,
  DA Form 4137 generation, SUSP-009 (property / .0015 funds to a non-DA agency, 2-7i).
- Judicial shipping (2-7h) is final disposition and belongs to the disposition slice.
- Signatures are never authenticated; every attestation is a record that a paper act occurred.

## 14. Limitations

- No real form, case number, person or serial exists in the repository, by rule; accuracy of
  the release workflow against an organization's real paper is unmeasured.
- The Windows Service, the deployment scripts and the SQL Server lane were written and reviewed
  here but could not be run here.
- The dashboard threshold is one number per room; a per-folder threshold would be a further
  local choice, not a regulatory one.

## 15. Instructions checked against the regulation and the code

- PENDING DISPOSITION APPROVAL is a paper state, not a release of evidence (2-4f(3)(c)); the
  brief's "three suspense categories" is kept exactly, and only two of them can be a release.
- The five 2-7b attestations apply to a person or organization at the counter; a release by
  accountable mail to the USACIL (2-7e) has no one to inventory or sign, so none is required.
- 2-7d (controlled-substance apparent change) applies to a release OTHER than for laboratory
  examination; the annotation is refused after a laboratory release rather than always offered.
- The DFT receives a COPY of the form, never the original (2-7c(2)); the copy path is enforced.
- The regulation gives no number of days anywhere in 2-7 or 3-1a(4); none was invented.
- Custody backfill from a verified scan is not a 1-7c(3) correction: the companion was
  incomplete and the paper was right.
- 2-5c is not the authority for "the paper is authoritative"; the custodian's original under
  2-4d/2-4f is. Citations corrected in code, pages and documents.

## 16. Recommended next slice

**MONTHLY CI INSPECTION and the 100% JOINT INVENTORY — yes, next.** The prerequisite the last
report named is now met: every way evidence leaves and returns writes to the chain of custody, the
item's state and the paper record, and the dashboard already shows what is out and with whom.
An inspection (3-1a) checks exactly those records against the room, and the joint inventory
(3-1b(2)) on a change of custodian compares the room against a chain that is now complete.
Disposition (2-8, 2-9) should follow rather than precede them: an inventory that finds the
DispositionPending items this slice creates is the natural place to prove the disposition
records before building the workflow that makes more of them.
