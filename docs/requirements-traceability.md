# EMC Requirements Traceability Matrix

Maps every EMC requirement to its AR 195-5 basis (where one exists), its classification, its
implementation status and its test coverage.

**Source:** AR 195-5, *Evidence Procedures*, 25 August 2019.

## Legend

| Type | Meaning |
|---|---|
| **REG** | Directly required by the cited AR 195-5 paragraph |
| **REG-REF** | AR 195-5 defers to another authority (AR 380-5, AR 381-20, AR 15-6, AR 25-400-2, ARIMS/RRS-A). EMC must not invent that authority's content |
| **DESIGN** | Project design decision. AR 195-5 neither requires nor forbids it |
| **CONTROL** | Recommended integrity/security control. Not regulatory |

| Status | Meaning |
|---|---|
| **Implemented** | Built in the current slice |
| **Partial** | Partially built; remainder specified |
| **Specified** | Designed and documented; not yet built |
| **Deferred** | Out of scope for V1 by decision |
| **Blocked** | Awaiting an organizational decision (see `docs/open-policy-decisions.md`) |

**A blank AR 195-5 column is meaningful** — it means the requirement is a design decision or a
control, not a regulatory mandate. Do not present those rows as regulation.

---

## EMC — Companion posture and system boundary

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| EMC-001 | EMC operates as a system used in conjunction with / to enhance AR 195-5, not as a stand-alone automated evidence ledger or accountability system | 2-5c | REG | Implemented | `AuthoritativeModeTests.DefaultsToCompanionMode` |
| EMC-002 | EMC must not assign the authoritative sequential evidence document number in V1 | 2-4c, 2-5a, 2-5c | REG | Implemented | `AuthoritativeModeTests.CompanionMode_RefusesSystemAssignedNumbering` |
| EMC-003 | Every accountability view states which record is authoritative (bound ledger and original DA Form 4137) | 2-5a, 2-5c | REG | Implemented | `AuthoritativeModeTests.CompanionNotice_NamesTheAuthoritativeRecords`, `WebHostSmokeTests.PagesRenderForAnAuthenticatedUser` |
| EMC-004 | Architecture supports becoming an approved automated equivalent without redesign; switching requires explicit configuration | 2-5c | DESIGN | Implemented | `AuthoritativeModeTests.SwitchingMode_RequiresARecordedApprovalReference` |
| EMC-005 | Administration UI states that enabling system-assigned numbering for a CI organization requires Army G-2X approval | 2-5c | REG | Implemented | `AuthoritativeModeTests.DefaultsToCompanionMode` |
| EMC-006 | EMC must not create a substitute local evidence form; printed outputs are a compliant DA Form 4137 or clearly-labelled working aids | Supplementation (title page); 2-3a | REG | Specified | — |
| EMC-007 | No cloud or internet dependency for any normal operation | — | DESIGN | Implemented | Deployment review |
| EMC-008 | EMC is a companion; the bound ledger and original DA Form 4137 remain authoritative in V1 | 2-5a, 2-5c | REG | Implemented | `AuthoritativeModeTests.CompanionNotice_NamesTheAuthoritativeRecords`, `WebHostSmokeTests.PagesRenderForAnAuthenticatedUser` |

## IAM — Identity, roles, custodial authority

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| IAM-001 | Every regulated action is attributable to an authenticated user, timestamp, action type, affected record, previous value, new value and reason where applicable | 1-7c(3) modelled | CONTROL | Implemented | `AuthorizationTests.AnAgentCannotRecordTheDocumentNumberThroughTheService` |
| IAM-002 | Authorization is resolved server-side per request from the database; client-submitted role information is never trusted | — | CONTROL | Implemented | `AuthorizationTests.AnUnauthenticatedUserIsDenied`, `AuthorizationTests.AnAgentCannotPerformCustodianActions` |
| IAM-003 | EMC stores no passwords or password hashes; authentication is Windows Authentication | — | CONTROL | Implemented | Schema review (`docs/db/README.md`); no credential column exists |
| IAM-004 | Exactly one active primary and one active alternate evidence custodian per evidence room at any instant | 1-4g(1) | REG | Implemented | `CustodianAppointmentTests.AnAppointmentConfersAuthorityOnlyWithinItsEffectiveRange`; DB index `UX_CustodianAppointments_OneOpenPerType` |
| IAM-005 | Custodial authority requires an active written appointment, not merely a role | 1-4g(1), 1-7b | REG | Implemented | `AuthorizationTests.ACustodianRoleWithoutAWrittenAppointmentIsDenied` |
| IAM-006 | Alternate custodian authority requires an open recorded assumption of duties during the primary's temporary absence, not merely an alternate appointment | 1-4i, 1-7c(1) | REG | Implemented | `AuthorizationTests.AnAppointedAlternateWithNoOpenAbsenceCannotActAsCustodian`, `AuthorizationTests.AnAlternateWhoHasAssumedDutiesIsAuthorized`, `AuthorizationTests.AlternateAuthorityCeasesWhenThePrimaryResumes` |
| IAM-007 | Emergency alternate appointment supersedes the previous alternate's orders | 1-4i | REG | Implemented | `CustodianAppointmentTests.EmergencyOrders_SupersedeThePreviousAlternate` |
| IAM-008 | CI evidence custodians must be credentialed CI agents; probationary CI agents are ineligible | 1-7a(1)(c) | REG | Partial (recorded as an eligibility attestation on appointment; EMC cannot verify credentialing) | `CustodianAppointmentTests.Appointment_RequiresTheEligibilityAttestation` |
| IAM-009 | Application Administrator has no evidence-accountability permission | — | DESIGN | Implemented | `AuthorizationTests.AdministratorIsDeniedOnEveryAccountabilityPermission`, `AuthorizationTests.RolePermissionMap_GivesTheAdministratorNoAccountabilityPermission` |
| IAM-010 | Role grants are audit logged; self-grants are flagged | — | CONTROL | Implemented | `UserRole.IsSelfGrant`; audit emission covered by `AuthorizationTests.AnAgentCannotRecordTheDocumentNumberThroughTheService` |
| IAM-011 | Agents cannot assign document numbers, accept evidence into the evidence room, or perform custodian-reserved actions | 2-4c, 1-4h | REG | Implemented | `AuthorizationTests.AnAgentCannotPerformCustodianActions` |
| IAM-012 | Custodian assumption / resumption / change statements are recorded as having been entered and signed in the ledger | 1-7c(1), 1-7c(2), 3-2g(3) | REG | Specified | — |
| IAM-013 | Custodian appointment documents are retained while the position is held | 1-7b, AR 25-400-2 | REG-REF | Specified | — |
| IAM-014 | Primary and alternate custodians hold the security clearance required for classified evidence stored | 4-1a, AR 380-5 | REG-REF | Specified | — |
| IAM-015 | A role grant is bounded in time and cannot end before it becomes effective | — | CONTROL | Implemented | `RoleAssignmentTests.AGrantCannotEndBeforeItBegins`, `RoleAssignmentTests.ARevokedGrantConfersNothingAfterRevocation` |
| IAM-016 | Operational roles are granted for a named evidence room; only the Application Administrator role may be held globally | 1-4g(1), 2-4c | DESIGN | Implemented | `RoleAssignmentTests.AnOperationalRoleCannotBeGrantedGlobally`, `RoleAssignmentTests.TheAdministratorRoleCannotBeScopedToARoom` |
| IAM-017 | Authorization for an evidence room is denied when the user holds no grant in that room, before any appointment is considered | 1-4g(1) | CONTROL | Implemented | `AuthorizationTests.AnAppointmentIsScopedToItsEvidenceRoom` |
| IAM-019 | An assumption of duties records the primary's absence start, the alternate's assumption instant and the 1-7c(1)/(2) ledger attestations, and cannot be recorded out of order | 1-4i, 1-7c(1), 1-7c(2) | REG | Implemented | `CustodianDutyAssumptionTests.TheAssumptionRequiresTheLedgerAttestation`, `CustodianDutyAssumptionTests.DutiesCannotBeAssumedBeforeTheAbsenceBegins`, `CustodianDutyAssumptionTests.ThePrimaryCannotResumeTwice` |
| IAM-020 | Once the primary's absence exceeds 30 consecutive days, an alternate acting solely under the temporary-absence provision is denied every accountability permission; there is no override | 1-4i, 3-2d | REG | Implemented | `AuthorizationTests.PastTheRegulatoryWindowTheAlternateIsDeniedNotWarned`, `AuthorizationTests.ExactlyThirtyDaysOfAbsenceIsStillAuthorized`, `AuthorizationTests.ANewlyAppointedPrimaryIsAuthorizedAfterTheProperTransition` |
| IAM-021 | An absence known at the outset to exceed 30 days, or already past 30 days, cannot be recorded as a temporary assumption of duties | 1-4i, 3-2d | REG | Implemented | `CustodianDutyAssumptionTests.AnAbsenceKnownAtTheOutsetToExceedThirtyDaysIsNotATemporaryAssumption`, `CustodianDutyAssumptionTests.DutiesCannotBeAssumedAfterTheAbsenceHasAlreadyExceededTheLimit` |
| IAM-022 | A change of primary custodian is incomplete until the joint inventory is recorded with all discrepancies resolved and the ledger statement entered | 3-2d, 3-2g(3) | REG | Implemented | `PrimaryCustodianTransitionTests.ATransitionIsIncompleteUntilTheJointInventoryIsRecorded`, `PrimaryCustodianTransitionTests.UnresolvedDiscrepanciesBlockCompletion`, `PrimaryCustodianTransitionTests.AnOutgoingCustodianIsOptional` |
| IAM-023 | The 30-day limit measures the primary's absence, not the alternate's acting period, and is compared exactly rather than truncated to whole days | 1-4i | REG | Implemented | `CustodianDutyAssumptionTests.TheRegulatoryLimitMeasuresThePrimarysAbsence_NotTheActingPeriod`, `CustodianDutyAssumptionTests.TheBoundaryIsExact_NotTruncatedToWholeDays`, `AuthorizationTests.TheRegulatoryLimitRunsFromThePrimarysAbsence_NotTheAppointmentOrAssumption` |
| IAM-024 | An absence exceeding 30 days requires a 100 percent inventory on the primary's resumption; 30 days or less does not | 1-7c(2) | REG | Implemented | `CustodianDutyAssumptionTests.AnAbsenceOfThirtyDaysOrLessNeedsNoHundredPercentInventory` |

## CASE — Cases

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| CASE-001 | Every voucher carries the Army CI case control number | 2-3b | REG | Implemented | `VerticalSliceTests.ADuplicateCaseControlNumberIsRefused` |
| CASE-002 | For evidence collected under an RFA, both the seizing and requesting offices' numbers are recorded | 2-3b | REG | Implemented | `VerticalSliceTests.ARequestForAssistanceRecordsBothCaseNumbers` |
| CASE-003 | Case header data is mutable with concurrency control and full audit | — | DESIGN | Implemented | `VerticalSliceTests.ADuplicateCaseControlNumberIsRefused` |
| CASE-004 | Cross-references between vouchers from the same investigation are supported | 2-5b(1)(f) | REG | Specified | — |

## VCH — Vouchers (DA Form 4137) and evidence numbering

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| VCH-001 | All physical evidence is inventoried and accounted for on a DA Form 4137 | 2-3a | REG | Implemented | `VerticalSliceTests.TheFullSlice_ProducesACompleteChronologicalItemHistory` |
| VCH-002 | The agent who first acquired the evidence prepares the form | 2-3b | REG | Implemented | `VerticalSliceTests.TheFullSlice_ProducesACompleteChronologicalItemHistory` |
| VCH-003 | Draft vouchers carry an unmistakably temporary identifier (`TMP-yyyyMMdd-Annn`) until the official number is assigned | 2-4c | DESIGN | Implemented | `DocumentNumberTests.TemporaryIdentifier_IsVisuallyDistinctFromTheRegulatoryFormat`, `DocumentNumberTests.TemporaryIdentifier_RollsIntoTheNextBlockAfter999` |
| VCH-004 | Official document number format is `NNN-YY`; sequence begins at 001 for the first form received in the calendar year | 2-4c | REG | Implemented | `DocumentNumberTests.Parse_AcceptsTheFormatTheRegulationPrescribes`, `VerticalSliceTests.AMalformedDocumentNumberIsRefusedWithTheRegulatoryCitation` |
| VCH-005 | The official number is unique per evidence room per calendar year, not globally | 2-4c, 2-7g | REG | Implemented | `VerticalSliceTests.ADocumentNumberCannotBeReusedInTheSameRoomAndYear` |
| VCH-006 | The custodian's entry of the official number records who, when, and an explicit attestation that it was assigned in the authoritative ledger | 2-4c, 2-5a | REG | Implemented | `VoucherStatusTests.RecordingADocumentNumber_RequiresTheLedgerAttestation`, `VerticalSliceTests.RecordingADocumentNumberWithoutTheLedgerAttestationIsRefused` |
| VCH-007 | Voucher status is derived from its items; a voucher becomes inactive only when all its items are in a terminal state | 2-4h | REG | Implemented | `VoucherStatusTests.VoucherBecomesInactiveOnlyWhenEveryItemIsTerminal`, `VoucherStatusTests.PartiallyAccepted_IsReportedAsSuch` |
| VCH-008 | A voucher may hold multiple document numbers over its life; superseded numbers remain visible | 2-7g | REG | Implemented | `VoucherStatusTests.SupersededDocumentNumbers_RemainVisible` |
| VCH-009 | A non-blocking warning is raised when the previous sequence number for that room and year is absent | 2-4c | CONTROL | Implemented | `VerticalSliceTests.ASequenceGapProducesAWarningAndNotABlock` |
| VCH-010 | Items may be added, edited or removed only while the voucher is `Draft` | 2-3g | REG | Implemented | `EvidenceItemTests.ItemsCannotBeEditedOnceTheVoucherIsSubmitted`, `VerticalSliceTests.ItemsCannotBeAddedOnceTheVoucherHasBeenSubmitted` |
| VCH-011 | A voucher must contain at least one item before submission | 2-3a | REG | Implemented | `EvidenceItemTests.AVoucherCannotBeSubmittedWithNoItems`, `VerticalSliceTests.AVoucherCannotBeSubmittedWithNoItems` |
| VCH-012 | Continuation of Description of Articles pages are supported | 2-3h | REG | Specified | — |
| VCH-013 | Continuation of Chain of Custody uses a new DA Form 4137 with the prescribed header entry | 2-3i | REG | Specified | — |
| VCH-014 | Active voucher files are organized in numerical sequence, no more than 50 per folder/binder | 2-4f(1) | REG | Specified | — |
| VCH-015 | Computer-generated DA Form 4137 generation, two-sided with vertical flip where reasonably possible | 2-3a | REG | Deferred (V2) | — |
| VCH-016 | Evidence is released to the custodian no later than the first working day after acquisition (two working days if served from a separate location); a delay requires an MFR | 2-4a, App B-4a(8) | REG | Blocked (**DEC-02**) | — |

## ITEM — Evidence items

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| ITEM-001 | The evidence item, not the voucher, is the primary unit of accountability | 2-2f, 2-5b(1)(d), 2-4h, 2-13b | REG | Implemented | `EvidenceItemTests.ItemNumbers_AreContiguousFromOne` |
| ITEM-002 | Item numbers are unique within a voucher and contiguous from 1 | 2-3d | REG | Implemented | `EvidenceItemTests.ItemNumbers_AreContiguousFromOne`, `EvidenceItemTests.RemovingAnItem_RenumbersTheRemainder` |
| ITEM-003 | Descriptions individualize the item to the exclusion of any other item and contain only descriptive information — no supposition | 2-3d | REG | Implemented (validated with guidance and a supposition-phrase warning) | `EvidenceItemTests.SuppositionPhrases_AreDetectedButNotBlocked`, `VerticalSliceTests.ASuppositionPhraseProducesAWarningAndNotABlock` |
| ITEM-004 | Serial numbers are recorded when available | 2-3d | REG | Implemented | `RazorPageSmokeTests.TheItemHistoryViewRendersEveryFieldThePageDisplays` |
| ITEM-005 | Large quantities are recorded as approximations | 2-3d | REG | Implemented | `EvidenceItemTests.ItemNumbers_AreContiguousFromOne` |
| ITEM-006 | Seized or safeguarded funds record the exact amount by denomination | 2-3d | REG | Implemented | `EvidenceItemTests.Currency_RecordsTheExactAmountByDenomination` |
| ITEM-007 | `POSSIBLE BIOHAZARD` in all capitals after each item with suspected blood or bodily fluids | 2-3l | REG | Implemented | `EvidenceItemTests.PossibleBiohazard_IsAnnotatedInAllCapitals`, `RazorPageSmokeTests.TheItemHistoryViewRendersEveryFieldThePageDisplays` |
| ITEM-008 | `LAST ITEM` annotation after the last listed item | 2-3d, 2-3h | REG | Implemented (derived on render, never hand-maintained) | `EvidenceItemTests.LastItem_IsDerivedFromPosition` |
| ITEM-009 | Sealing is annotated in the Description of Articles section | 2-3c | REG | Implemented | `EvidenceItemTests.SealedItem_RequiresTheSealingAnnotation` |
| ITEM-010 | A grouped set listed as one item is one item with one DA Form 4002 | 2-1b | REG | Implemented (subject to **DEC-04**) | `EvidenceItemTests.ItemNumbers_AreContiguousFromOne` (an item is the numbered line; see DEC-04) |
| ITEM-011 | Each item is sealed in its own separate container; separately-numbered items are not sealed together | 2-2f | REG | Specified (recorded, not enforced — physical act) | — |
| ITEM-012 | Unique device identifiers (IMEI or comparable) are captured as first-class identifying characteristics | 2-3d | DESIGN | Implemented | `EvidenceItemTests.ItemNumbers_AreContiguousFromOne` |

## COC — Chain of custody

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| COC-001 | Custody history is event-based; current custody is derived from event history, never stored as maintained state | Glossary "Chain of custody"; 2-3f | REG | Implemented | `RazorPageSmokeTests.TheItemHistoryViewRendersEveryFieldThePageDisplays` |
| COC-002 | Any change of custody after first acquisition is recorded | 2-3f | REG | Implemented | `EventAndCorrectionTests.EventsRecordBothOccurrenceAndSystemEntryTime` |
| COC-003 | A custody event records releasing party, receiving party, timestamp, purpose, destination, agency, SCRCNI, notes, recording user, system entry time and source document | 2-3f, 2-7b, 2-7e | REG | Implemented | `EventAndCorrectionTests.EventsRecordBothOccurrenceAndSystemEntryTime` |
| COC-004 | Custody counterparties may be internal users, external persons, organizations, or an accountable mail number | 2-7b, 2-7e | REG | Implemented | `EventAndCorrectionTests.AnAccountableMailNumberIsAValidCustodyParty` |
| COC-005 | `SCRCNI` is annotated when custody of sealed evidence changes | 2-3e, 2-3f | REG | Implemented | `EventAndCorrectionTests.ScrcniIsAnnotatedInThePurposeOfChangeOfCustody` |
| COC-006 | Registered or other accountable mail numbers are recorded in the Received By / Released By positions | 2-7e | REG | Implemented | `EventAndCorrectionTests.AnAccountableMailNumberIsAValidCustodyParty`, `EventAndCorrectionTests.CustodianUnableToSignIsAValidCustodyParty` |
| COC-007 | `N/A Custodian Unable to Sign` is supported in the Released By position | 3-2g(5) | REG | Specified | — |
| COC-008 | Custody events are append-only | 2-5b(5) modelled | CONTROL | Implemented | `AppendOnlyAndCorrectionTests.AnEventCannotBeModified`, `AppendOnlyAndCorrectionTests.AnEventCannotBeDeleted` |

## LOC — Physical location

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| LOC-001 | The current evidence location within the evidence room is maintained | 2-4e | REG | Implemented | `VerticalSliceTests.TheFullSlice_ProducesACompleteChronologicalItemHistory` |
| LOC-002 | Complete location history is retained; prior locations are never overwritten | — (**2-4e contemplates erasure of the previous entry**) | DESIGN + CONTROL | Implemented | `VerticalSliceTests.TheFullSlice_ProducesACompleteChronologicalItemHistory` |
| LOC-003 | Documentation and UI must not claim AR 195-5 requires location history | 2-4e | DESIGN | Implemented | Documentation review |
| LOC-004 | Storage locations are hierarchical and kinded (evidence room, depository, temporary facility, impound lot, long-term container, high-value container, shelf, bin) | 4-1, 4-1d, 4-3, 2-6f, 2-13 | REG | Implemented | `VerticalSliceTests.TheFullSlice_ProducesACompleteChronologicalItemHistory` |
| LOC-005 | Temporary release is a custody state, not a storage location | 2-7a, 2-7b | REG | Implemented | Model review - `TemporarilyReleased` is an `AccountabilityStatus`, not a `StorageLocation` |
| LOC-006 | Location assignment is restricted to a user with an active custodian appointment | 1-4h, 2-4e | REG | Implemented | `AuthorizationTests.AnAgentCannotPerformCustodianActions` |
| LOC-007 | A storage location resolves within the item's own evidence room; both assignment and correction enforce it identically | 2-4c, 2-4e | DESIGN | Implemented | `AppendOnlyAndCorrectionTests.ALocationCorrectionCannotNameAnotherEvidenceRoomsLocation`, `AppendOnlyAndCorrectionTests.ALocationCorrectionCannotNameALocationThatDoesNotExist` |

## DOC — Source documents

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| DOC-001 | Each imported source PDF retains original file, original filename, SHA-256, received date, source, page count, document type and import status | — | DESIGN | Specified | — |
| DOC-002 | Source documents are immutable | — | CONTROL | Specified | — |
| DOC-003 | Uploads are validated by content, not by extension or client-supplied content type | — | CONTROL | Specified | — |
| DOC-004 | File size limits are enforced at the framework and storage layers | — | CONTROL | Specified | — |
| DOC-005 | Embedded PDF content is never executed; display uses rasterized page images | — | CONTROL | Specified | — |
| DOC-006 | Files are stored outside the web root under generated names; user-supplied filenames are data only | — | CONTROL | Specified | — |
| DOC-007 | MFRs are attached to the appropriate DA Form 4137 and copied to the case file | 1-7c(3), 2-3e, 3-2f, 3-3a | REG | Specified | — |
| DOC-008 | Receipts and chain-of-custody documents from non-Army agencies are attached to the DA Form 4137 | 2-3g | REG | Specified | — |
| DOC-009 | Source-document downloads are audit logged | — | CONTROL | Specified | — |

## OCR — Scanned form ingestion

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| OCR-001 | OCR never silently becomes authoritative accountability information | 2-3g modelled | CONTROL | Specified | — |
| OCR-002 | Three confidence bands: High (prepopulate, still reviewable), Medium (prepopulate, prominently flagged), Low/Unreadable (no guess, manual entry required) | — | DESIGN | Specified | — |
| OCR-003 | High-consequence fields require explicit verification even at high confidence: document number, case control number, item number, serial number, IMEI or comparable identifier, names in custody transfers, dates/times, currency amounts, disposition information | — | DESIGN | Specified | — |
| OCR-004 | Both raw extracted text and the verified normalized value are retained permanently; correcting a value never destroys the OCR result | — | CONTROL | Specified | — |
| OCR-005 | Extracted information is presented beside the original scan for verification | — | DESIGN | Specified | — |
| OCR-006 | The OCR subsystem runs fully locally/offline; documents are never sent to a public or cloud service | — | DESIGN | Specified | — |
| OCR-007 | Template/coordinate-aware extraction is preferred, exploiting DA Form 4137's predictable layout | — | DESIGN | Specified | — |
| OCR-008 | The importer supports multi-page forms, Continuation of Description of Articles pages, Continuation of Chain of Custody pages, attached MFRs and outside-agency custody paperwork | 2-3h, 2-3i, 2-3g | REG | Specified | — |
| OCR-009 | Multiple documents belonging to one intake package are recognized | — | DESIGN | Specified | — |

## REC — Reconciliation

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| REC-001 | A scanned form conflicting with stored data is never silently merged or overwritten | 2-3g modelled | CONTROL | Specified | — |
| REC-002 | Conflicts are surfaced in a reconciliation workflow with explicit actions: add, mark extraction incorrect, flag for custodian review | — | DESIGN | Specified | — |
| REC-003 | Custody events present on the source document but absent from the companion record are detected | 2-3f | REG | Specified | — |
| REC-004 | Every reconciliation decision is audit logged with user, time and reason | — | CONTROL | Specified | — |

## INV — Inventories

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| INV-001 | For CI, the monthly inspection includes a 100 percent joint inventory by the Commander/SAC and the Primary Evidence Custodian | 3-1b(2) | REG | Specified | — |
| INV-002 | The prescribed joint-inventory certification statement is recorded as entered and signed in the ledger | 3-1b(2) | REG | Specified | — |
| INV-003 | Each inventory observation is stored as its own record, one per expected item per session; an item is never merely flagged "inventoried" | 3-1b(2), 3-2b(1)(b) | REG | Specified | — |
| INV-004 | Observation outcomes distinguish physically verified, sealed-container verified without breach, accounted for on temporary release, not located, unexpectedly present, and not yet checked | 3-1b(2), 3-2f, 3-1a(4), 3-3a, App B-4e(4) | DESIGN (built on cited paragraphs) | Specified | — |
| INV-005 | Inventory sessions record participants, date/time, type, expected population, per-item observations, exceptions, corrective actions and completion/attestation status | 3-1b(2), 3-2g | REG | Specified | — |
| INV-006 | Inventories are conducted on change of primary custodian; the outgoing custodian resolves all discrepancies before transfer of accountability | 3-2a(2), 3-2d | REG | Specified | — |
| INV-007 | Inventories are conducted on loss of evidence or breach of security, by the person assigned the inquiry, in the presence of the primary or alternate custodian | 3-2a(3), 3-2e | REG | Specified | — |
| INV-008 | Sealed containers are not breached for any inventory unless directed by the responsible supervisor; a breach requires an MFR attached to the DA Form 4137 | 3-2f | REG | Specified | — |
| INV-009 | No joint inventory is required when the alternate replaces the primary for 30 consecutive calendar days or less | 1-7c(2), 3-2d | REG | Specified | — |
| INV-010 | Long-term retention containers are not opened for inventory unless tampering is evident or competent authority directs | 2-13d | REG | Specified | — |
| INV-011 | Quarterly-inventory applicability for CI units | 3-2a(1), 3-2b | REG | Blocked (**DEC-01**) | — |
| INV-012 | Inventory attestations remain compatible with the paper ledger signature requirement in V1 | 2-5b(2), 3-2g | REG | Specified | — |

## INSP — Inspections

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| INSP-001 | A monthly inspection of the evidence room is supported | 1-4g(3), 3-1a | REG | Specified | — |
| INSP-002 | For CI, the inspection is conducted by the CI unit commander, or the acting commander when the commander is unavailable | 1-4g(3) | REG | Specified | — |
| INSP-003 | The first inspection by a new supervisor assuming supervisory control includes an inventory of all evidence | 3-1a | REG | Specified | — |
| INSP-004 | Inspections record the 3-1a determinations, including SF 700 spare key/combination verification | 3-1a(1)-(4) | REG | Specified | — |
| INSP-005 | Inspections check that evidence on temporary release has not been released for an excessive period | 3-1a(4), App B-4c(11) | REG | Specified | — |
| INSP-006 | The Appendix B internal control evaluation checklist is available to support the inspection | 3-1a, App B | REG | Specified | — |
| INSP-007 | Discrepancies identified during the previous inspection are followed up | App B-4d(28) | REG | Specified | — |

## SUSP — Temporary release and suspense

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| SUSP-001 | Evidence leaves the evidence room only for permanent disposal or authorized temporary release | 2-7a | REG | Specified | — |
| SUSP-002 | Suspense categories use the regulation's folder names: USACIL, ADJUDICATION, PENDING DISPOSITION APPROVAL | 2-4f(3) | REG | Specified | — |
| SUSP-003 | Temporary releases record released-to party, date released, reason, destination, expected follow-up, contact history, return date and current age | 2-7a, 2-7b | REG | Specified | — |
| SUSP-004 | Ageing thresholds are local management thresholds; EMC must never present a day count as an AR 195-5 deadline | 2-7a, 2-7b, 3-1a(4) | DESIGN | Specified | — |
| SUSP-005 | Contact/follow-up history evidences the "reasonable and adequate contact" obligation | 2-7a | REG | Specified | — |
| SUSP-006 | An aging/suspense dashboard shows days out per released item | 3-1a(4) | DESIGN | Specified | — |
| SUSP-007 | On temporary release the original DA Form 4137 accompanies the evidence and a copy is retained in the suspense folder | 2-4f(2), 2-7b | REG | Specified | — |
| SUSP-008 | Items on one voucher released to more than one agency at the same time are supported | 2-7b | REG | Specified | — |
| SUSP-009 | US Government property and .0015 funds may be temporarily released to a non-DA agency only after processing into the evidence room and with approval | 2-7i, 2-3n | REG | Specified | — |

## DISP — Disposition

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| DISP-001 | Disposition is modelled as a workflow, never as a boolean | 2-8, 2-9 | REG | Specified | — |
| DISP-002 | Disposition is item-level; items on one voucher may be disposed on different dates, under different authorities, by different methods | 2-5b(1)(d), 2-8e(5) | REG | Specified | — |
| DISP-003 | SJA coordination is documented prior to disposition | 2-8 opening | REG | Specified | — |
| DISP-004 | The approving authority is identified according to case posture | 2-8a, 2-8b, 2-8c, 2-8e | REG | Specified | — |
| DISP-005 | Final Disposal Action and Final Disposal Authority areas are completed before the approval authority signs | 1-4h(5) | REG | Specified | — |
| DISP-006 | More than one approving authority on a single DA Form 4137 is supported via a continuation sheet | 2-8e(5) | REG | Specified | — |
| DISP-007 | All final disposition actions are documented in the hard copy case file and online database case records in addition to the DA Form 4137 | 2-8 opening, 2-9 opening | REG | Specified | — |
| DISP-008 | A destruction witness physically views the items, not merely the container | 2-9 opening | REG | Specified | — |
| DISP-009 | A controlled-substance destruction witness must not be in the chain of custody; an alternate custodian who held the room while the evidence was in it is ineligible | 2-9c | REG | Specified | — |
| DISP-010 | Evidence entered as a permanent part of the record of trial is treated as final disposition and annotated on the DA Form 4137 | 2-8e(4) | REG | Specified | — |
| DISP-011 | Permanent transfer to another evidence room is documented as disposition by the sending unit; the receiving room assigns its next document number and the prior number remains legible | 2-7g, 2-7h | REG | Partial (numbering implemented; workflow specified) | `VerticalSliceTests.ADocumentNumberCannotBeReusedInTheSameRoomAndYear` |

## LOSS — Discrepancies, missing evidence, inquiries

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| LOSS-001 | Missing evidence is modelled as a `Discrepancy` record, never as a `missing = true` flag | 3-3a | REG | Specified | — |
| LOSS-002 | Up to 5 working days are allowed to resolve apparently missing evidence before an official inquiry is initiated | 3-3a | REG | Blocked (**DEC-02** — "working day" undefined) | — |
| LOSS-003 | Losses, security breaches and inquiry initiations are reported; for CI units, to DCS G-2 (Army G-2X). EMC prompts and records; it does not perform the report | 3-3b | REG | Specified | — |
| LOSS-004 | Inquiries are conducted in accordance with AR 15-6 | 3-3b | REG-REF | Specified | — |
| LOSS-005 | Relief for accountability is a terminal state; for CI units relief is granted by Army G-2X, and relief permits closure of the DA Form 4137 | 3-3c, 3-3c(1) | REG | Implemented (state); workflow specified | `AccountabilityStateTests.ReliefGrantedIsTerminal`, `VoucherStatusTests.ReliefGranted_ClosesTheVoucherWithoutDisposition` |
| LOSS-006 | Relief from accountability has no bearing on administrative or judicial action; EMC must not imply otherwise | 3-3c(2) | REG | Specified | — |
| LOSS-007 | Discrepancies record opened date/time, affected evidence, discoverer, source, regulatory deadline, actions taken, resolution, MFR reference and escalation status | 3-3a | REG | Specified | — |
| LOSS-008 | Corrective actions resolving a discrepancy are documented in an MFR attached to the appropriate DA Form 4137 | 3-3a | REG | Specified | — |
| LOSS-009 | Evidence apparently missing from a received parcel triggers immediate supervisor and sender notification | 3-3d | REG | Specified | — |

## AUD — Auditability and immutability

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| AUD-001 | Accountability history is append-only; historical events are never updated or deleted | 2-5b(5), 1-7c(3) modelled | CONTROL | Implemented | `AppendOnlyAndCorrectionTests.AnEventCannotBeModified`, `AppendOnlyAndCorrectionTests.AnEventCannotBeDeleted` |
| AUD-002 | Append-only is enforced in the domain, in `SaveChanges`, and by database triggers | — | CONTROL | Implemented | `AppendOnlyAndCorrectionTests.AnEventCannotBeModified`, `AppendOnlyAndCorrectionTests.AnAuditEventCannotBeModifiedOrDeleted` (domain + SaveChanges layers; the SQL Server triggers are verified at deployment) |
| AUD-003 | A correction is a new field-level record; the corrected event is never rewritten, never hidden, and its uncorrected fields keep their original values | 2-5b(5) | CONTROL | Implemented | `AppendOnlyAndCorrectionTests.CorrectingALocationProducesTheCorrectedCurrentLocation`, `AppendOnlyAndCorrectionTests.SeveralFieldsOfOneEventCanBeCorrectedIndependently`, `AppendOnlyAndCorrectionTests.ACorrectionsOnlyAffectTheEventTheyName` |
| AUD-004 | A correction records the corrected record, original value, corrected value, reason, correcting user and date/time | 1-7c(3) | REG | Implemented | `EventAndCorrectionTests.ACorrectionMustStateAReasonAndActuallyChangeTheValue`, `EventAndCorrectionTests.TheServerDerivesTheOriginalValue_TheClientCannotStateIt` |
| AUD-005 | A post-acceptance correction is refused unless it records the MFR reference and the supervisor notification 1-7c(3) requires; pre-acceptance and transcription corrections are not subject to 1-7c(3) | 1-7c(3) | REG | Implemented (enforced, not flagged) | `EventAndCorrectionTests.APostAcceptanceCorrectionIsRefusedWithoutItsParagraph1_7c3Documentation`, `EventAndCorrectionTests.Paragraph1_7c3AppliesOnlyToPostAcceptanceCustodianCorrections`, `AppendOnlyAndCorrectionTests.APostAcceptanceCorrectionWithoutAnMfrIsRefusedAndNothingIsRecorded` |
| AUD-006 | The UI presents the corrected current interpretation cleanly while keeping the original visible to an auditor | 2-5b(5) | CONTROL | Implemented | `RazorPageSmokeTests.TheSupersededEntryStaysInTheRenderedHistory`, `WebHostSmokeTests.TheCaseAndVoucherAndItemPagesAllRender` |
| AUD-007 | Application administrators cannot rewrite evidence history through the application | — | DESIGN | Implemented | `AuthorizationTests.AdministratorIsDeniedOnEveryAccountabilityPermission`, `AuthorizationTests.RolePermissionMap_GivesTheAdministratorNoAccountabilityPermission` |
| AUD-008 | Events are hash-chained per item so out-of-band modification is detectable by any reader | — | CONTROL | Implemented | `EventAndCorrectionTests.HashChain_VerifiesAnIntactChain`, `AppendOnlyAndCorrectionTests.ChainVerificationDetectsAnEventModifiedOutsideTheApplication`, `AppendOnlyAndCorrectionTests.ChainVerificationDetectsARemovedEvent` |
| AUD-009 | Domain accountability records and application diagnostic logs are clearly distinguished | — | CONTROL | Implemented | `AuthorizationTests.AnAgentCannotRecordTheDocumentNumberThroughTheService` |
| AUD-010 | Diagnostic logs never duplicate sensitive investigative data | — | CONTROL | Implemented | Code review checklist (`docs/architecture.md` §10) |
| AUD-011 | Events record both occurrence time (local, as on the form) and system entry time | 2-3f | REG | Implemented | `EventAndCorrectionTests.EventsRecordBothOccurrenceAndSystemEntryTime`, `AccountabilityTimeTests.EventTimestampsAreNormalizedToWholeMilliseconds` |
| AUD-012 | Database migrations are reproducible and source controlled; the application never migrates on startup | — | CONTROL | Implemented | `Emc.Infrastructure/Migrations`; `dotnet ef migrations has-pending-model-changes` reports no drift |
| AUD-013 | Attestations are records that a paper signature was executed; they are not signatures and are never described as such | 2-5b(2), 2-7b, 3-1b(2), 2-8e(5) | REG | Implemented | `VoucherStatusTests.RecordingADocumentNumber_RequiresTheLedgerAttestation` |
| AUD-014 | The value as originally recorded is derived by the server from the stored event; a client-supplied original value is impossible, and a correction naming a field the event type does not expose is rejected | 2-5b(5) | CONTROL | Implemented | `EventAndCorrectionTests.TheServerDerivesTheOriginalValue_TheClientCannotStateIt`, `EventAndCorrectionTests.AnUnsupportedFieldNameIsRejected`, `AppendOnlyAndCorrectionTests.TheClientCannotFalsifyTheOriginalValue`, `AppendOnlyAndCorrectionTests.AnUnsupportedFieldNameIsRejected` |
| AUD-015 | Corrections are field-level: a field may be corrected repeatedly, the most recent correction is what the record reads, and fields never corrected keep their original values | 2-5b(5) | CONTROL | Implemented | `EffectiveProjectionTests.AFieldCorrectedTwiceTakesTheMostRecentCorrection`, `EffectiveProjectionTests.CorrectingOneFieldLeavesTheOthersAtTheirOriginalValues`, `AppendOnlyAndCorrectionTests.SeveralFieldsOfOneEventCanBeCorrectedIndependently` |
| AUD-016 | A correction to a field that names a row carries the replacement IDENTIFIER, and its display text is read from that row by the server; free-text corrections carry no identifier | — | DESIGN | Implemented | `EventAndCorrectionTests.AFieldThatNamesARowCannotBeCorrectedWithTextAlone`, `EventAndCorrectionTests.AFreeTextFieldCannotBeCorrectedByNamingARow`, `EffectiveProjectionTests.CorrectingALocationMovesTheIdentifier_NotOnlyTheDisplayedPath`, `AppendOnlyAndCorrectionTests.ACorrectedLocationIsFoundByItsNewIdentifier`, `AppendOnlyAndCorrectionTests.ALocationCorrectionMustNameAReplacementLocation`, `AppendOnlyAndCorrectionTests.AFreeTextCorrectionCannotCarryAnIdentifier` |
| AUD-017 | A correction records the value it actually changed (the field as it read immediately before it) as well as the value as originally recorded; the no-change test and the audit trail use the former, and which correction is current follows server-assigned append order, never a user-supplied occurrence time | 1-7c(3), 2-5b(5) | CONTROL | Implemented | `EffectiveProjectionTests.ThreeSequentialCorrectionsEachRecordWhatTheyActuallyChanged`, `EffectiveProjectionTests.ABackDatedCorrectionDoesNotTakePrecedenceOverALaterAppendedOne`, `EffectiveProjectionTests.ACorrectionThatRestatesTheCurrentValueIsRefused_EvenIfItDiffersFromTheOriginal`, `EffectiveProjectionTests.RestoringTheOriginalValueIsAValidCorrection`, `AppendOnlyAndCorrectionTests.ThreeSequentialCorrectionsRecordTheChainEndToEnd`, `AppendOnlyAndCorrectionTests.RestatingTheCurrentValueIsRefusedAfterAnEarlierCorrection` |
| AUD-018 | The supervisor informed under 1-7c(3) is recorded by printed name, grade and organization with the moment of notification; an EMC user link is optional, and when present the particulars are read from the user record rather than the request | 1-7c(3) | DESIGN | Implemented | `EventAndCorrectionTests.TheSupervisorInformedNeedNotHoldAnEmcAccount`, `EventAndCorrectionTests.ASupervisorWithAnAccountIsRecordedFromTheUserRecord_NotFromTheCaller`, `EventAndCorrectionTests.TheSupervisorNotificationIsHashed`, `AppendOnlyAndCorrectionTests.ASupervisorWithoutAnEmcAccountCanBeRecorded`, `AppendOnlyAndCorrectionTests.ASupervisorWithAnAccountIsRecordedFromTheUserRecord`, `AppendOnlyAndCorrectionTests.AnInactiveOrUnknownSupervisorUserIsRefused` |
| AUD-019 | Every timestamp EMC writes comes from the injected application clock (`IClock`); application, infrastructure and page code never read the ambient system clock for a recorded value | — | CONTROL | Partial (`DateTime.Now` / `TimeZoneInfo.Local` remain in three pages as form defaults for LOCAL time entry, pending the evidence-room time-zone service) | Code review: `grep DateTimeOffset.UtcNow src/` returns only `SystemClock` and `EvidenceDocumentNumber.CalendarYear` (the latter is scheduled for removal with configurable numbering) |

## RET — Retention

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| RET-001 | Evidence in unsolved homicide, rape, sexual assault, undetermined death and missing person cases, and any offense with no statute of limitations, is retained indefinitely | 2-8c(2)(a) | REG | Specified | — |
| RET-002 | Unrestricted sexual assault physical and forensic evidence (less the SAFE kit) is retained 5 years from the date of seizure | 2-15a | REG | Specified | — |
| RET-003 | Retention rules block disposition while active | 2-8c(2)(a), 2-15a | REG | Specified | — |
| RET-004 | Long-term retention containers reference included document numbers and excluded item numbers; contained items retain their identities and their vouchers remain in the active file | 2-13b | REG | Specified | — |
| RET-005 | Long-term retention requires a disinterested witness not in the chain of custody, and a signed certificate/memorandum | 2-13a, 2-13b | REG | Specified | — |
| RET-006 | Firearms are not stored or sealed in a consolidated evidence box | 2-13c | REG | Specified | — |
| RET-007 | Inactive DA Forms 4137 are disposed of 3 years after becoming inactive; ledgers may be disposed 3 years after the last item is disposed | 2-4h, 2-5a | REG | Specified | — |
| RET-008 | Retention and disposition of EMC's own records under ARIMS/RRS-A | 1-5 | REG-REF | Blocked (**DEC-07**) | — |
| RET-009 | Evidence retained in other serious unsolved cases requires a memorandum explaining the reason, maintained in the case file | 2-8c(2)(c) | REG | Specified | — |

## SEC — Security boundaries

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| SEC-001 | Classified evidence handling is governed by AR 380-5; EMC invents no classified requirements | 2-6h, 2-7k, 2-9r, 4-1a | REG-REF | Implemented (boundary only) | Documentation review |
| SEC-002 | CI evidence storage is governed by AR 381-20 | 4-2a(2) | REG-REF | Specified | — |
| SEC-003 | The system's accredited classification level is configuration-driven and displayed in a banner | — | DESIGN | Implemented | `WebHostSmokeTests.PagesRenderForAnAuthenticatedUser` |
| SEC-004 | Security boundaries are clean enough that AR 380-5 controls can be layered without redesign | — | DESIGN | Implemented | Architecture review |
| SEC-005 | Whether EMC's aggregate content is itself classified, and the enclave it is accredited for | 4-1a, AR 380-5 | REG-REF | Blocked (**DEC-06**) | — |
| SEC-006 | Uploaded documents are treated as untrusted input | — | CONTROL | Specified | — |
| SEC-007 | Concurrency control uses database-level checks, not UI validation | — | CONTROL | Implemented | Model review - `IConcurrencyStamped` enforced in `EmcDbContext.SaveChanges` |
| SEC-008 | Child exploitation imagery release restrictions | 2-7j | REG | Specified | — |
| SEC-009 | The application's SQL login cannot alter schema or drop the append-only triggers | — | CONTROL | Specified (deployment) | Deployment review |

## DFE — Digital forensic evidence

| ID | Requirement | AR 195-5 | Type | Status | Tests |
|---|---|---|---|---|---|
| DFE-001 | EMC stores accountability metadata only — device, forensic image identifier, SHA-256, external storage reference | — | DESIGN | Specified | — |
| DFE-002 | EMC never ingests or stores bulk forensic data (disk images, extractions, forensic case files) | — | DESIGN | Specified | — |
| DFE-003 | Forensic data storage remains separate from the EMC metadata database | — | DESIGN | Specified | — |
| DFE-004 | Digital media storage-condition guidance is available to the custodian | 2-6g | REG | Specified | — |
| DFE-005 | Computer and network hardware may be released for final disposal after a forensically sound image is obtained, under the authorities named in the regulation | 2-8d | REG | Specified | — |

---

## Blocked requirements

These cannot be correctly implemented until the organization decides. Implementing a guess would
produce a system that states a wrong regulatory deadline or a wrong scope — the specific failure
mode this project is meant to avoid.

| Requirement | Decision | Why it blocks |
|---|---|---|
| INV-011 | **DEC-01** | Whether CI units conduct quarterly inventories in addition to the monthly 100 percent joint inventory |
| LOSS-002, VCH-016, IAM-006 | **DEC-02** | "Working day" is undefined; 3-3a's 5-working-day clock is a real deadline |
| VCH-005 scope | **DEC-03** | One evidence room per instance, or several |
| ITEM-010 | **DEC-04** | Confirmation that an item is the numbered line, whose quantity may exceed one |
| IAM-006, IAM-020 | **DEC-05** | *Closed.* Hard denial past 30 days, no override; only the working-day vs calendar-day count (AMB-05) remains open |
| SEC-005 | **DEC-06** | Accredited classification level of the system |
| RET-008 | **DEC-07** | Records status of EMC's own data under ARIMS/RRS-A |
| — | **DEC-08** | Whether EMC may hold restricted-reporting sexual assault data |
