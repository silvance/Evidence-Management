# Slice report — source documents, physical DA Form 4137, local OCR, verification, reconciliation

Written at the close of the slice that began after commit `935e8f9`. Everything below is what
the repository does now; nothing is projected. Labels: **[REG]** AR 195-5 requires it; **[REG-REF]**
AR 195-5 defers it to another authority; **[DESIGN]** / **[CONTROL]** / **[LOCAL]** the program's
own choices, which AR 195-5 neither requires nor forbids.

## 1. Commits in this slice (oldest last)

- 9b41802 Paper/companion cross-check advisories and the paper-file retention dashboard
- 5788987 Reconciliation: verified scan against the companion record, decided one difference at a time
- 67cd2a8 Human verification page: the engine's page images with field boxes, decisions per field
- 6f60a64 DA Form 4137 template mapping: label-anchored fields, page faces, continuation pages, synthetic fixtures
- b64e837 Local OCR: job leasing, immutable runs, banded fields, verification, Tesseract worker process
- a2a6e9f Evaluate local OCR engines; carry reviewed non-NuGet artifacts in the dependency bundle
- 7db88e6 Immutable source-document storage, authorization before bytes, raster display and integrity
- 7719c55 Model the physical DA Form 4137 apart from any scan: files, suspense, inactive filing, retention
- 2bde142 Fix location-state semantics and the 2-3g/2-5b(5) correction-model conflation

The final commit of the slice is the one carrying this report.

## 2. Defects found and fixed

| Defect | Where | Fix |
|---|---|---|
| `MayHoldEvidenceRoomLocation` allowed a new bin for an item on temporary release or one that could not be located | state machine / intake | Replaced by `IsPhysicallyInEvidenceRoom` (InEvidenceRoom, DispositionPending, LongTermRetention only); LOC-008; exhaustive presence table test |
| Para 2-5b(5) (ledger strike-through) cited as the DA Form 4137 correction rule, conflating it with 2-3g | domain, services, UI text, docs | 2-3g is the custodian-review / agent-corrects rule; 2-5b(5) citations for the form removed; claims table entry |
| VCH-010 ("items may be removed from a draft") contradicted `RemoveItem` on a returned form | voucher | Revision model: each submission is a snapshot; a returned line is withdrawn as entered in error (VCH-026, attestation that no physical item exists), never deleted; a physical item leaves only through 2-8 |
| Razor Pages reserved route key `page` swallowed the page-image query parameter, so every page image was 404 | web | Parameter renamed `pageNumber`; HTTP test covers it |
| IIS request filtering defaults to 30,000,000 bytes, below the configured 50 MB upload | deployment | `web.config` with `requestLimits` carried in source; DOC-004 wording corrected |
| `IISServerOptions` is a stub in the Linux reference pack, so the Windows-only limit could not be compiled on the test lane | web | Limit expressed through Kestrel, the per-page attribute (which IIS in-process honours) and web.config |
| No single Tesseract segmentation mode reads both the small printed labels and the boxed values on every input | OCR engine | Two passes (psm 3 and 6) merged by geometry; documented in the engine |
| Tolerant label matching took the earliest fuzzy span, claiming "BY PURPOSE OF CHANGE OF CUSTODY" and losing "RECEIVED BY" | template mapper | Best-span selection; tolerance scaled to phrase length; unit tests for merged tokens and near-misses |
| Test fade helper used 0-255 offsets where SkiaSharp takes 0-1, producing a blank page | tests | Corrected; the faint/speckled case now exercises the contrast stretch |
| PDFsharp needed a font resolver before any font was created; the fixture created fonts first | tests | Resolver installed in the fixture's static constructor (embedded package fonts, no system fonts, no network) |

## 3. OCR engine choice, and what was rejected

**Chosen:** Tesseract 5, run by `Emc.OcrWorker` as an external process (argument list, private
per-invocation folder, minimal environment, hard timeout that kills the process tree, output
consumed and never logged), with `eng.traineddata` and `osd.traineddata` as reviewed bundle
artifacts. Reasons and the criteria table are in `docs/ocr-engine-evaluation.md`.

**Rejected:** Windows.Media.Ocr (no pinnable artifact, no confidence output, untestable on the
Linux lane); Python engines (EasyOCR, docTR, Kraken, TrOCR: interpreter and framework footprint);
every cloud service and their "disconnected" containers (licence activation needs the Internet).
**Deferred, not rejected:** RapidOCR/PaddleOCR on ONNX Runtime, behind the same `IOcrEngine`
interface, if Tesseract's accuracy on the organization's real scans proves insufficient.

## 4. Native and model dependencies introduced

| Dependency | Kind | Path into the enclave |
|---|---|---|
| PDFtoImage 5.4.0 → SkiaSharp 4.150.1 (+ native assets Linux / Win32 / macOS), bblanchon.PDFium 152.0.7961 | NuGet, runtime | lock files; `packages/` in the bundle |
| PDFsharp 6.2.4 | NuGet, test-only (synthetic fixtures) | lock files; `packages/` |
| Tesseract 5.3.x engine | non-NuGet, runtime, `ocr-engine` | `artifacts/ocr-engine/` via the reviewed artifact manifest |
| `eng.traineddata`, `osd.traineddata` | non-NuGet, runtime, `ocr-model` | `artifacts/ocr-model/` via the reviewed artifact manifest |

No dependency is fetched at run time. No component names a URL. `Emc.OcrWorker` uses the ASP.NET
Core shared framework for hosting and adds no package.

## 5. Dependency bundle changes

`Export-DependencyBundle.ps1 -ArtifactManifest` takes a reviewed input manifest
(`emc-artifact-manifest/1`, example in `scripts/staging/artifacts.manifest.example.json`) with
name, kind, version, path, origin, SHA-256, licence, classification, model/language id, retrieval
date and review fields; it re-hashes every file and refuses an unreviewed or altered one.
`manifest.json` is now schema `emc-dependency-bundle/2`. Both verifiers check kind, approval and
licence and warn when a bundle carries no engine or no model. `Restore-Build-Test-Offline.ps1`
takes `-TesseractPath` / `-TessdataPath` and says plainly when offline OCR was not validated.
**Not executed here:** the PowerShell scripts (no `pwsh` in this environment); the bash verifier
was exercised against a synthetic bundle.

## 6. Migrations

Regenerated from scratch, as this repository does: `InitialEvidenceModel` and
`AppendOnlyTriggers`; `db/schema-v1.sql` regenerated (33 tables, 28 append-only triggers,
error numbers 50001-50028). Tables added in the slice: `VoucherFormRevisions`,
`VoucherFormRevisionLines`, `PhysicalFileContainers`, `PhysicalVoucherDocuments`,
`PhysicalVoucherDocumentEvents`, `SourceDocuments`, `SourceDocumentPages`, `OcrJobs`, `OcrRuns`,
`OcrRunPages`, `ExtractedFields`, `FieldVerifications`, `ReconciliationFindings`. `OcrJobs` is
the one mutable work table (leases under a concurrency stamp); every other new table is
append-only at all three layers.

## 7. Authorization changes

New permissions: `physical-file.manage` (active custodian appointment required), `ocr.request`,
`ocr.verify`, `reconciliation.decide`; all four are accountability permissions, granted to agents
(except physical-file.manage) and custodians, and denied to the ApplicationAdministrator. Applying
a reconciliation difference to a draft additionally needs `voucher.edit-draft`; initiating a
post-acceptance correction needs `evidence.record-correction` (active appointment). The
authorization matrix test still holds unchanged. The OCR worker is not a principal: it holds no
role, no permission, and an unauthenticated `ICurrentUser`.

## 8. Tests

| Project | Passed | Skipped | Notes |
|---|---|---|---|
| Emc.Domain.Tests | 274 | 0 | |
| Emc.Application.Tests | 202 | 10 | the 10 are the SQL Server lane, opt-in |

Real-engine tests (Tesseract) ran here against the distribution's Tesseract 5.3.4; they are
skipped visibly where no engine is installed. The doc-reference checker resolves every test name
cited in the traceability and regression documents.

## 9. SQL Server lane

Extended with every new trigger and the unique storage-key indexes. **Not executed against a
real instance in this environment** (none available). The staging workflow runs it against a
disposable container; the release procedure runs it offline against an approved local instance.

## 10. Open decisions and things deliberately not built

- **DEC-07** stands: paper destruction is confirmed by a person and the digital record is never
  destroyed; the retention dashboard computes eligibility from the inactive date alone.
- **No custody-event recording workflow exists yet.** Reconciliation *detects* chain-of-custody
  rows on the scan (REC-003) and records "missing historical event" findings; it cannot create
  custody events, because temporary release / suspense is the next slice and no service records
  custody transfers. The brief's "custody events via the normal service" is therefore recorded as
  a finding routed to that future workflow, not performed.
- OSD orientation confidence threshold (5) and the confidence bands (90/60) are [DESIGN]
  constants; they change what is prepopulated, never what is authoritative.
- Accuracy on the organization's real scanner output is unmeasured: no real form exists in this
  repository, by rule.
- Signatures are never authenticated; a signature block is at most present-or-absent.

## 11. Handwriting

Tesseract reads printed and typed entries. Handwritten names, initials and free-text descriptions
will mostly land in the Low/Unreadable band, where no guess is offered and the value is entered
from the paper by a person and recorded as such. That is the designed behaviour. Better handwriting
would need a different engine (TrOCR-class), which would still require a person to verify every
field, and is not in scope.

## 12. Readiness

Ready to use as designed, inside the companion boundary: companion copies, verification,
reconciliation to a DRAFT, findings on accepted vouchers, the physical file record and the
retention dashboard. Not ready: any act the custody, release, inventory, inspection or disposition
workflows own; the OCR worker as a Windows Service (a wrapper or scheduler is documented, not
shipped); the SQL Server lane and the bundle export until run where they can be.

## 13. Recommended next feature

**Temporary release / suspense (option 2), before inventory/inspection (option 1).** Three
reasons from the code as it stands: reconciliation already surfaces custody rows with nowhere
to record them (REC-003 is detection only); the paper suspense states (FIL) and the
physical/digital advisories PDC-002/003 exist and wait on item release states that no service can
set; and an inventory or inspection compares the room against a chain of custody that, without
release and return events, is incomplete - building it first would compare against a record
that cannot yet be right.

## 14. Instructions corrected against the regulation

- Para 2-5b(5) is the LEDGER strike-through rule; the DA Form 4137 correction path before
  acceptance is 2-3g. Citations were corrected, not mechanically implemented.
- The traceability document already defined REC-003 as custody-row detection; the brief's
  "document number never assigned by OCR" rule was given its own ID (REC-005) rather than
  overwriting it, and the provenance link became REC-006.
- The regulatory document-number form is a three-digit sequence and two-digit year
  (2-4f(1): "001 - 18"); the mapper's candidate normalizer follows it, not a four-digit form.
- Continuation pages: 2-3h (Description of Articles, bond paper, the sentence at the top, LAST
  ITEM) and 2-3i (a NEW DA Form 4137 with "Continuation of Chain of Custody, dated ...") are
  classified as two different faces, as the regulation describes them.
