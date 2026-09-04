# Hardening-pass regression tests

The second foundation-hardening review asked for a regression test for every issue it raised.
This maps each item to the tests that cover it. Every test name here is checked against the test
sources by the same resolver that checks `docs/requirements-traceability.md`; a name that stops
existing fails that check.

Lanes: **D** domain (no database) · **A** application/pages over SQLite · **S** SQL Server
release-validation lane (opt-in, offline; not yet executed against a real instance from this
repository's development environment).

## Read security

| Item | Tests |
|---|---|
| Unregistered authenticated principal reads nothing | A `ReadAuthorizationTests.AnAuthenticatedPrincipalWithNoEmcUserRecordReadsNothing`, `ReadAuthorizationHttpTests.AnUnregisteredDomainAccountSeesNoCasesInTheListing`, `ReadAuthorizationHttpTests.TheItemHistoryUrlLeaksNoEvidenceContentToAnUnregisteredAccount`, `AuthorizationMatrixTests.EachPrincipalGetsExactlyTheExpectedDecisions` (row 2) |
| Admin-only user reads no evidence content | A `ReadAuthorizationTests.AnAdministratorCannotReadEvidenceContent`, `ReadAuthorizationTests.AdministratorIsDeniedEveryEvidenceReadPermission`, `IntegrityVerificationTests.TheReportCarriesNoEvidenceContent`, matrix row 3 |
| Room A user cannot read room B | A `ReadAuthorizationTests.AUserCannotReadRecordsInAnotherEvidenceRoom`, matrix row 5, `AuthorizationMatrixTests.TheAgentInRoomBIsAllowedInRoomB` |
| Direct-ID navigation is denied | A `ReadAuthorizationHttpTests.DirectNavigationToEvidenceUrlsIsDeniedForAnUnregisteredAccount`, `ReadAuthorizationHttpTests.ForbiddenAndNonExistentRecordsAreIndistinguishable`, `ReadAuthorizationTests.GuessingIdentifiersCannotDistinguishAbsentFromForbidden` |

## Custodian authority

| Item | Tests |
|---|---|
| Primary appointment authorizes primary | A `AuthorizationTests.TheAppointedCustodianIsAllowed`, matrix row 6 |
| Alternate appointment alone does not authorize acting as primary | A `AuthorizationTests.AnAppointedAlternateWithNoOpenAbsenceCannotActAsCustodian`, matrix row 7 |
| Active temporary-custodianship period does | A `AuthorizationTests.AnAlternateWhoHasAssumedDutiesIsAuthorized`, matrix row 8 |
| Primary resumption ends alternate authority | A `AuthorizationTests.AlternateAuthorityCeasesWhenThePrimaryResumes` |
| 30-day rule uses the absence date, not the appointment date | D `CustodianDutyAssumptionTests.TheRegulatoryLimitMeasuresThePrimarysAbsence_NotTheActingPeriod`; A `AuthorizationTests.TheRegulatoryLimitRunsFromThePrimarysAbsence_NotTheAppointmentOrAssumption`, `AuthorizationTests.PastTheRegulatoryWindowTheAlternateIsDeniedNotWarned` |
| Civilian CI custodian validly appointed under 1-7a(2)(c) | D `CustodianAppointmentTests.ACivilianCanBeAppointedPrimaryCustodian`, `CustodianAppointmentTests.CivilianEligibilityCitesParagraph1_7a2c_AndImportsNoExtraRestrictions` |

## Corrections

| Item | Tests |
|---|---|
| Client cannot falsify OriginalValue | D `EventAndCorrectionTests.TheServerDerivesTheOriginalValue_TheClientCannotStateIt`; A `AppendOnlyAndCorrectionTests.TheClientCannotFalsifyTheOriginalValue` |
| Unsupported FieldName rejected | D `EventAndCorrectionTests.AnUnsupportedFieldNameIsRejected`; A `AppendOnlyAndCorrectionTests.AnUnsupportedFieldNameIsRejected` |
| Location correction produces the correct effective current location, by identifier | D `EffectiveProjectionTests.CorrectingALocationMovesTheIdentifier_NotOnlyTheDisplayedPath`; A `AppendOnlyAndCorrectionTests.CorrectingALocationProducesTheCorrectedCurrentLocation` |
| Custody correction produces the correct effective current custody | D `EffectiveProjectionTests.CorrectingACustodyRecipientMovesTheParty_NotOnlyTheName` |
| Multiple fields corrected without losing unrelated values | D `EffectiveProjectionTests.CorrectingOneFieldLeavesTheOthersAtTheirOriginalValues`; A `AppendOnlyAndCorrectionTests.SeveralFieldsOfOneEventCanBeCorrectedIndependently` |
| Original remains visible | D `EffectiveProjectionTests.TheOriginalEventRemainsAvailableAfterCorrection`; A `RazorPageSmokeTests.TheSupersededEntryStaysInTheRenderedHistory` |
| Chained corrections record what each changed; ordering by append sequence | D `EffectiveProjectionTests.ThreeSequentialCorrectionsEachRecordWhatTheyActuallyChanged`, `EffectiveProjectionTests.ABackDatedCorrectionDoesNotTakePrecedenceOverALaterAppendedOne`; A `AppendOnlyAndCorrectionTests.ThreeSequentialCorrectionsRecordTheChainEndToEnd` |
| 1-7c(3) documentation required before a post-acceptance correction commits | D `EventAndCorrectionTests.APostAcceptanceCorrectionIsRefusedWithoutItsParagraph1_7c3Documentation`; A `AppendOnlyAndCorrectionTests.APostAcceptanceCorrectionWithoutAnMfrIsRefusedAndNothingIsRecorded` |
| Supervisor need not hold an EMC account | D `EventAndCorrectionTests.TheSupervisorInformedNeedNotHoldAnEmcAccount`; A `AppendOnlyAndCorrectionTests.ASupervisorWithoutAnEmcAccountCanBeRecorded` |
| Pre-acceptance submitting-agent correction is separate from custodian correction | D `VoucherReviewTests.AnAcceptedVoucherCannotBeReturned_ThatIsAParagraph1_7c3Matter`, `VoucherReviewTests.OnlyTheSubmittingAgentRecordsTheCorrection`; A `CustodianReviewTests.TheSubmittingAgentEditsCorrectsAndResubmits_AndTheCustodianAccepts` |
| OCR verification category exists in the model | D `EventAndCorrectionTests.Paragraph1_7c3AppliesOnlyToPostAcceptanceCustodianCorrections` (`CorrectionCategory.TranscriptionVerification`) |

## Append-only

| Item | Tests |
|---|---|
| No ItemEvent UPDATE | A `AppendOnlyAndCorrectionTests.AnEventCannotBeModified`; S `SqlServerReleaseValidationTests.ItemEventsRejectUpdateAndDelete_OnCommonAndSubtypeColumns` |
| No ItemEvent DELETE | A `AppendOnlyAndCorrectionTests.AnEventCannotBeDeleted`; S as above |
| Subtype fields cannot be changed by raw SQL | S `SqlServerReleaseValidationTests.ItemEventsRejectUpdateAndDelete_OnCommonAndSubtypeColumns` (SQLite has no triggers; the SaveChanges layer is covered by the A tests) |
| Document-number history cannot be rewritten | A `VerticalSliceTests.ASupersededDocumentNumberIsNeverReissued`; S `SqlServerReleaseValidationTests.AuditEventsDocumentNumbersAndReviewActionsRejectUpdateAndDelete` |
| Hash-chain verification still works | A `AppendOnlyAndCorrectionTests.ChainVerificationDetectsAnEventModifiedOutsideTheApplication`, `AppendOnlyAndCorrectionTests.ChainVerificationDetectsARemovedEvent`; D `EventAndCorrectionTests.HashChain_VerifiesAnIntactChain` |

## Numbering

| Item | Tests |
|---|---|
| AR 195-5 default parses/renders 001-26 | D `NumberingPolicyTests.TheDefaultIsTheRegulationsLayout`, `DocumentNumberTests.Parse_AcceptsTheFormatTheRegulationPrescribes` |
| Configured local format parses/renders 26-01 | D `NumberingPolicyTests.ALocalLayoutWritesTheYearFirst`, `NumberingPolicyTests.ALocalLayoutReadsItsOwnNumbers`; A `DocumentNumberPolicyTests.ALocalLayoutIsAcceptedAsWrittenAndStoredCanonically` |
| Both map to canonical Year=2026, Sequence=1 | D `DocumentNumberTests.ALocalLayoutYieldsTheSameCanonicalNumber` |
| Superseded historical numbers cannot be reused | A `VerticalSliceTests.ASupersededDocumentNumberIsNeverReissued`, `DocumentNumberPolicyTests.TheSameNumberUnderTwoLayoutsIsOneNumber_AndIsNeverReused`; S `SqlServerReleaseValidationTests.TheCanonicalDocumentNumberIsUniqueAcrossAllHistory_AtTheDatabase` |
| Format changes are effective-dated | D `NumberingPolicyTests.PoliciesAreEffectiveDated`; A `DocumentNumberPolicyTests.TheSameNumberUnderTwoLayoutsIsOneNumber_AndIsNeverReused` |
| Century interpretation is deterministic | D `DocumentNumberTests.TheCalendarYearComesFromContext_NotFromTheClock`; A `DocumentNumberPolicyTests.TheStoredCalendarYearIsAFactOfTheRecord_NotOfTheClock`, `DocumentNumberPolicyTests.AYearThatDisagreesWithTheDateReceivedMustBeConfirmed` |

## Time

| Item | Tests |
|---|---|
| Server time zone cannot change recorded evidence-room time | A `EvidenceRoomTimeTests.TheHostsZoneDoesNotEnterIntoIt`, `EvidenceRoomTimeTests.TheServiceUsesEachRoomsOwnZone` |
| DST edge cases handled explicitly | A `EvidenceRoomTimeTests.ATimeInTheRepeatedHourIsAmbiguousAndIsNotResolvedByDefault`, `EvidenceRoomTimeTests.AnAmbiguousTimeIsResolvedByTheStatedChoice`, `EvidenceRoomTimeTests.ATimeInTheSkippedHourIsNonexistentAndIsRefused` |

## Integrity

| Item | Tests |
|---|---|
| Event-chain alteration detected | A `IntegrityVerificationTests.ARawEventChangeIsAChainFailure_NotASnapshotMismatch` |
| Snapshot / accountability-status mismatch detected | A `IntegrityVerificationTests.ARawStatusChangeIsASnapshotMismatch_WhileTheChainStillVerifies`; D `SnapshotVerificationTests.AStatusThatDisagreesWithTheHistoryIsASnapshotMismatch_NotAChainFailure` |
| LastEventHash mismatch detected | A `IntegrityVerificationTests.ARawSequenceOrHeadChangeIsReportedByKind` |

## Concurrency

| Item | Tests |
|---|---|
| Simultaneous temporary-ID creation cannot produce duplicates | A `TemporaryIdentifierAllocationTests.AStaleContextDoesNotReissueANumberAnotherRequestTook`, `TemporaryIdentifierAllocationTests.DraftsCreatedThroughTheServiceCarryDistinctIdentifiers` |
| Concurrency stamps conflict at the database | A `ConcurrencyStampTests.AStaleUpdateIsRejectedAtTheDatabase`; S `SqlServerReleaseValidationTests.ConcurrencyStampsConflictOnSqlServer` |

## Authorization matrix

`AuthorizationMatrixTests.EachPrincipalGetsExactlyTheExpectedDecisions` holds one row per kind
of principal — unauthenticated; unregistered Windows principal; administrator; agent in room A;
agent in room B only; appointed primary custodian; alternate by role only; alternate with duties
assumed; commander/SAC; inspection or inventory participant — against eleven permissions. The
review found one gap, now closed: `RecordCorrection` did not require an active custodian
appointment, so an alternate holding the role with no open assumption of duties could correct an
accepted record while being denied every other custodian act
(`AuthorizationMatrixTests.RecordingACorrectionRequiresAnActiveCustodianAppointment`).

## Source documents, OCR, verification, reconciliation and the paper record (third pass)

The regression set for the slice that added companion copies, local OCR, human verification,
reconciliation and the physical DA Form 4137 record. D = domain test project, A = application
test project (SQLite harness and the web host), S = SQL Server lane, T = requires a local
Tesseract install (skipped visibly otherwise).

| Item | Tests |
|---|---|
| Foundation: location may be assigned only to an item physically in the room; presence table exhaustive | D `AccountabilityStateTests.ANewLocationMayBeAssignedOnlyToAnItemPhysicallyInTheRoom`, `AccountabilityStateTests.ThePresenceTableCoversEveryStatus`, `AccountabilityStateTests.AReleasedItemRegainsTheLocationWorkflowOnlyByReturningToTheRoom` |
| 2-3g review: a returned form is a revision; a line entered in error is withdrawn, never deleted; a physical item cannot be dropped | D `VoucherReviewTests.*`, `AccountabilityStateTests.AWithdrawnLineIsTerminalAndReachableOnlyFromAcquired`; A `CustodianReviewTests.AnItemAlreadyOnTheRecordCannotBeDeletedFromAReturnedVoucher`, `CustodianReviewTests.ALineEnteredInErrorIsWithdrawn_KeptInHistory_AndNeverAccepted`, `CustodianReviewTests.APhysicalItemCannotBeDroppedThroughLineWithdrawal`, `CustodianReviewTests.AnotherAgentCannotWithdrawALine` |
| Physical DA Form 4137: filing, suspense, inactive, 3-year clock from the inactive date, destruction confirmed by a person, 50-voucher limit | D `PhysicalDocumentTests.*`; A `PhysicalDocumentServiceTests.*`, `PaperRecordReportTests.TheDashboardBucketsByFileAndByTheThreeYearClock_FromTheInactiveDateOnly`, `PaperRecordReportTests.AdvisoriesSayWhatDisagrees_AndChangeNothing`, `PaperDashboardHttpTests.TheRetentionDashboardRendersForTheRoom_AndNotForAnOutsider` |
| Source documents: content validation, immutability, generated keys, authorization before bytes, raster display, download audited, integrity | A `SourceDocumentTests.*`, `DocumentRenderIsolationTests.*`, `SourceDocumentHttpTests.UploadViewPageImageAndDownload_ThenDeniedToAnOutsider`, `SourceDocumentHttpTests.AnOversizeUploadIsRefusedAtTheRequestLayer`; S `SqlServerReleaseValidationTests.EveryAppendOnlyTriggerExists`, `SqlServerReleaseValidationTests.ExpectedIndexesExist` |
| OCR model: bands, high-consequence verification, raw text never edited, failure categories not text, job leasing | D `OcrModelTests.*` |
| OCR pipeline with a fake engine: request, lease, run, verify, timeout requeue, expired lease takeover, two workers, room scope, orientation vote | A `OcrProcessorTests.*` |
| Worker transactional discipline: one open job per document at the database, lease renewed per page and settled only by its holder, lost lease discards the attempt and its blobs, orphan sweep never deletes a referenced blob, approved-hash verification before execution, hung version probe killed | A `OcrLeaseAndBlobTests.*`; D `OcrModelTests.ALeaseIsRenewedOnlyByItsHolder_AndMustBePositive` |
| OCR engine, real, offline: start-up verification of binary and models, synthetic pages, rotation, deskew, timeout kills the process, no network location configurable, two-pass merge | A/T `TesseractEngineTests.*` |
| DA Form 4137 mapping: front/back/continuation faces, label-anchored fields, item and custody rows, unreadable block, conflicting number, normalizers, noisy labels | A/T `DaForm4137MappingTests.*` |
| Verification page: companion statement, run page images with boxes, decisions per field, outsider denied | A `OcrVerificationHttpTests.UploadRequestRunAndVerify_ThroughThePage` |
| Reconciliation: draft patch applied only by decision through the voucher service; nothing applied after acceptance or while verification is outstanding; document number never applied; 1-7c(3) with provenance; findings append-only | A `ReconciliationTests.*` |
| OCR security: logs and audit rows carry no extracted text; work folder cleaned on failure; engine invoked without a shell | A `OcrSecurityTests.*`, `TesseractEngineTests.TheWorkerConfigurationNamesNoNetworkLocation`, `TesseractEngineTests.ATimeoutKillsTheEngineProcess` |
| Air gap: locked restore, no floating versions, offline NuGet configuration, no remote assets, artifact manifest for the OCR engine and models | A `OfflineBuildTests.*` |
| Authorization matrix still holds with the new permissions | A `AuthorizationMatrixTests.EachPrincipalGetsExactlyTheExpectedDecisions`, `OcrProcessorTests.OcrIsRoomScoped_AndVerificationNeedsItsPermission`, `ReconciliationTests.FindingsAreAppendOnly_AndAnOutsiderSeesNothing` |

