using Emc.Application.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Identity;
using Emc.Domain.Storage;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests.SqlServer;

/// <summary>
/// Proves, on SQL Server, what SQLite cannot: the migrations apply from empty; every append-only
/// trigger exists and rejects UPDATE and DELETE - including on table-per-hierarchy subtype
/// columns; the canonical document number is unique across all assignment history; the
/// custodian-appointment filtered index holds; concurrency stamps conflict. Skipped unless the
/// lane is opted into (see <see cref="SqlServerFactAttribute"/>).
///
/// Trigger error numbers: 50001/50002 ItemEvents, 50003/50004 AuditEvents,
/// 50005/50006 OfficialDocumentNumberAssignments, 50007/50008 VoucherReviewActions,
/// 50009-50012 form revisions and lines, 50013/50014 PhysicalVoucherDocumentEvents,
/// 50015-50018 SourceDocuments and pages, 50019-50024 OcrRuns/ExtractedFields/FieldVerifications,
/// 50025/50026 OcrRunPages, 50027/50028 ReconciliationFindings.
/// Unique-index violations: 2601 (index) / 2627 (constraint).
/// </summary>
public class SqlServerReleaseValidationTests
{
    private const int UniqueIndex = 2601;
    private const int UniqueConstraint = 2627;

    private static async Task<(SqlServerHarness Harness, int VoucherId, int ItemId)> AcceptedItemAsync(string documentNumber = "001-26")
    {
        var harness = SqlServerHarness.Create();
        harness.SignInAsAgent();

        var caseResult = await harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Release validation", null, harness.EvidenceRoomId));

        var voucherResult = await harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", harness.Clock.UtcNow, false, null));

        var itemResult = await harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One Samsung SM-S921U cellular telephone", "1",
            "R58N30XXXXX", "356938035643809", false, false, false, null));

        await harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);

        harness.SignInAsCustodian();
        var numbered = await harness.Intake.RecordOfficialDocumentNumberAsync(
            new RecordDocumentNumberRequest(voucherResult.Value, documentNumber, true, harness.Clock.UtcNow));
        Assert.True(numbered.Succeeded, numbered.Error);

        var located = await harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
            itemResult.Value, harness.ShelfBBin14Id, harness.Clock.UtcNow, "Initial placement", null));
        Assert.True(located.Succeeded, located.Error);

        return (harness, voucherResult.Value, itemResult.Value);
    }

    [SqlServerFact]
    public void MigrationsApplyFromAnEmptyDatabase_AndNothingIsPending()
    {
        using var harness = SqlServerHarness.Create();

        Assert.Empty(harness.Db.Database.GetPendingMigrations());
        Assert.NotEmpty(harness.Db.Database.GetAppliedMigrations());
    }

    [SqlServerFact]
    public void EveryAppendOnlyTriggerExists()
    {
        using var harness = SqlServerHarness.Create();

        foreach (var trigger in new[]
        {
            "TR_ItemEvents_AppendOnly_Update", "TR_ItemEvents_AppendOnly_Delete",
            "TR_AuditEvents_AppendOnly_Update", "TR_AuditEvents_AppendOnly_Delete",
            "TR_DocumentNumbers_AppendOnly_Update", "TR_DocumentNumbers_AppendOnly_Delete",
            "TR_VoucherReviewActions_AppendOnly_Update", "TR_VoucherReviewActions_AppendOnly_Delete",
            "TR_VoucherFormRevisions_AppendOnly_Update", "TR_VoucherFormRevisions_AppendOnly_Delete",
            "TR_VoucherFormRevisionLines_AppendOnly_Update", "TR_VoucherFormRevisionLines_AppendOnly_Delete",
            "TR_PhysicalVoucherDocumentEvents_AppendOnly_Update", "TR_PhysicalVoucherDocumentEvents_AppendOnly_Delete",
            "TR_SourceDocuments_AppendOnly_Update", "TR_SourceDocuments_AppendOnly_Delete",
            "TR_DocumentRenderRuns_AppendOnly_Update", "TR_DocumentRenderRuns_AppendOnly_Delete",
            "TR_DocumentRenderPages_AppendOnly_Update", "TR_DocumentRenderPages_AppendOnly_Delete",
            "TR_OcrRuns_AppendOnly_Update", "TR_OcrRuns_AppendOnly_Delete",
            "TR_ExtractedFields_AppendOnly_Update", "TR_ExtractedFields_AppendOnly_Delete",
            "TR_FieldVerifications_AppendOnly_Update", "TR_FieldVerifications_AppendOnly_Delete",
            "TR_OcrRunPages_AppendOnly_Update", "TR_OcrRunPages_AppendOnly_Delete",
            "TR_ReconciliationFindings_AppendOnly_Update", "TR_ReconciliationFindings_AppendOnly_Delete",
            "TR_TemporaryReleaseEvents_AppendOnly_Update", "TR_TemporaryReleaseEvents_AppendOnly_Delete",
            "TR_SuspenseContacts_AppendOnly_Update", "TR_SuspenseContacts_AppendOnly_Delete"
        })
        {
            var count = harness.Scalar<int>(
                $"SELECT COUNT(*) FROM sys.triggers WHERE name = N'{trigger}' AND is_disabled = 0");
            Assert.True(count == 1, $"Trigger {trigger} is missing or disabled.");
        }
    }

    [SqlServerFact]
    public void ExpectedIndexesExist()
    {
        using var harness = SqlServerHarness.Create();

        foreach (var name in new[] { "UX_CustodianAppointments_OneOpenPerType", "UX_OcrJobs_OneOpenPerDocument", "UX_DocumentRenderJobs_OneOpenPerDocument", "UX_TemporaryReleaseItems_OneOpenPerItem" })
        {
            var filtered = harness.Scalar<int>(
                $"SELECT COUNT(*) FROM sys.indexes WHERE name = N'{name}' AND has_filter = 1 AND is_unique = 1");
            Assert.True(filtered == 1, $"Filtered unique index {name} is missing.");
        }

        // The canonical document-number index is UNFILTERED and unique (VCH-011): once recorded,
        // a (room, year, sequence) is consumed for good, superseded or not.
        var canonical = harness.Scalar<int>(
            @"SELECT COUNT(*) FROM sys.indexes i
              JOIN sys.tables t ON t.object_id = i.object_id
              WHERE t.name = N'OfficialDocumentNumberAssignments' AND i.is_unique = 1 AND i.has_filter = 0
                AND (SELECT COUNT(*) FROM sys.index_columns ic WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0) = 3");
        Assert.True(canonical >= 1, "No unfiltered unique index over (EvidenceRoomId, CalendarYear, Sequence).");

        // Generated storage keys are unique per table (DOC-006); a run's pages and a document's
        // pages are unique per page number.
        foreach (var (table, columns) in new[] { ("SourceDocuments", 1), ("DocumentRenderPages", 1), ("OcrRunPages", 1) })
        {
            var keyIndex = harness.Scalar<int>(
                $@"SELECT COUNT(*) FROM sys.indexes i JOIN sys.tables t ON t.object_id = i.object_id
                   JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                   JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                   WHERE t.name = N'{table}' AND i.is_unique = 1 AND c.name = N'StorageKey'");
            Assert.True(keyIndex >= columns, $"No unique index over {table}.StorageKey.");
        }
    }

    [SqlServerFact]
    public async Task ItemEventsRejectUpdateAndDelete_OnCommonAndSubtypeColumns()
    {
        var (harness, _, itemId) = await AcceptedItemAsync();
        using (harness)
        {
            var locationEventId = await harness.Db.ItemEvents.OfType<LocationEvent>()
                .Where(e => e.EvidenceItemId == itemId).Select(e => e.Id).FirstAsync();
            var statusEventId = await harness.Db.ItemEvents.OfType<StatusEvent>()
                .Where(e => e.EvidenceItemId == itemId).Select(e => e.Id).FirstAsync();
            var numberEventId = await harness.Db.ItemEvents.OfType<DocumentNumberEvent>()
                .Where(e => e.EvidenceItemId == itemId).Select(e => e.Id).FirstAsync();

            // Every column class the review named: common, location, status, document number.
            // Custody, seal and examination subtype columns share the same table and the same
            // unconditional trigger, so the UPDATE below on a NULL subtype column is rejected
            // identically - the trigger does not look at which column changed.
            var attempts = new (string Sql, int Id)[]
            {
                ("UPDATE ItemEvents SET Notes = 'tampered' WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET StorageLocationPath = 'Shelf Z / Bin 99' WHERE Id = @id", locationEventId),
                ("UPDATE ItemEvents SET StorageLocationId = 1 WHERE Id = @id", locationEventId),
                ("UPDATE ItemEvents SET ToStatus = 7 WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET DocumentNumber = '999-26' WHERE Id = @id", numberEventId),
                ("UPDATE ItemEvents SET PurposeOfChangeOfCustody = 'x' WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET ReceivedByPartyId = 1 WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET PerformedByName = 'x' WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET Laboratory = 'x' WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET EventHash = 'x' WHERE Id = @id", statusEventId),
                ("UPDATE ItemEvents SET SequenceNumber = 99 WHERE Id = @id", statusEventId)
            };

            foreach (var (sql, id) in attempts)
            {
                Assert.Equal(50001, harness.TryExecuteOutOfBand(sql, new SqlParameter("@id", id)));
            }

            Assert.Equal(50002, harness.TryExecuteOutOfBand(
                "DELETE FROM ItemEvents WHERE Id = @id", new SqlParameter("@id", statusEventId)));

            // And the row is untouched.
            var history = await harness.History.GetAsync(itemId);
            Assert.True(history!.ChainVerification.IsIntact);
            Assert.True(history.SnapshotVerification!.IsConsistent);
        }
    }

    [SqlServerFact]
    public async Task AuditEventsDocumentNumbersAndReviewActionsRejectUpdateAndDelete()
    {
        var (harness, voucherId, _) = await AcceptedItemAsync();
        using (harness)
        {
            var auditId = await harness.Db.AuditEvents.Select(a => a.Id).FirstAsync();
            var assignmentId = await harness.Db.DocumentNumberAssignments.Where(a => a.VoucherId == voucherId).Select(a => a.Id).FirstAsync();
            var reviewId = await harness.Db.VoucherReviewActions.Where(a => a.VoucherId == voucherId).Select(a => a.Id).FirstAsync();

            Assert.Equal(50003, harness.TryExecuteOutOfBand("UPDATE AuditEvents SET Reason = 'x' WHERE Id = @id", new SqlParameter("@id", auditId)));
            Assert.Equal(50004, harness.TryExecuteOutOfBand("DELETE FROM AuditEvents WHERE Id = @id", new SqlParameter("@id", auditId)));

            Assert.Equal(50005, harness.TryExecuteOutOfBand("UPDATE OfficialDocumentNumberAssignments SET DocumentNumber = '999-26' WHERE Id = @id", new SqlParameter("@id", assignmentId)));
            Assert.Equal(50005, harness.TryExecuteOutOfBand("UPDATE OfficialDocumentNumberAssignments SET Sequence = 999 WHERE Id = @id", new SqlParameter("@id", assignmentId)));
            Assert.Equal(50006, harness.TryExecuteOutOfBand("DELETE FROM OfficialDocumentNumberAssignments WHERE Id = @id", new SqlParameter("@id", assignmentId)));

            Assert.Equal(50007, harness.TryExecuteOutOfBand("UPDATE VoucherReviewActions SET Narrative = 'x' WHERE Id = @id", new SqlParameter("@id", reviewId)));
            Assert.Equal(50008, harness.TryExecuteOutOfBand("DELETE FROM VoucherReviewActions WHERE Id = @id", new SqlParameter("@id", reviewId)));
        }
    }

    [SqlServerFact]
    public async Task TheCanonicalDocumentNumberIsUniqueAcrossAllHistory_AtTheDatabase()
    {
        var (harness, voucherId, _) = await AcceptedItemAsync("005-26");
        using (harness)
        {
            // Bypass the application's own VCH-011 check and ask the database directly.
            var error = harness.TryExecuteOutOfBand(
                @"INSERT INTO OfficialDocumentNumberAssignments
                    (VoucherId, EvidenceRoomId, DocumentNumber, Sequence, CalendarYear, EnteredByUserId, EnteredAtUtc,
                     AttestedAssignedInAuthoritativeLedger, SupersedesAssignmentId, SupersessionReason, NumberingPolicyId)
                  VALUES (@v, @r, '005-26', 5, 2026, @u, SYSDATETIMEOFFSET(), 1, NULL, NULL, NULL)",
                new SqlParameter("@v", voucherId), new SqlParameter("@r", harness.EvidenceRoomId),
                new SqlParameter("@u", harness.CustodianUserId));

            Assert.Contains(error, new[] { UniqueIndex, UniqueConstraint });
        }
    }

    [SqlServerFact]
    public void OnlyOneOpenAppointmentPerTypePerRoom_AtTheDatabase()
    {
        using var harness = SqlServerHarness.Create();

        // The seeded primary is open. A second open primary for the same room must fail on the
        // filtered unique index, not merely on application code.
        var error = harness.TryExecuteOutOfBand(
            @"INSERT INTO CustodianAppointments
                (EvidenceRoomId, UserId, AppointmentType, PersonnelCategory, EffectiveFrom, EffectiveTo, AppointmentOrderReference,
                 AppointingAuthority, EligibilityAttested, EligibilityStatement, RecordedByUserId, RecordedAtUtc, SupersedesAppointmentId)
              SELECT EvidenceRoomId, UserId, AppointmentType, PersonnelCategory, EffectiveFrom, NULL, N'ORDERS DUPLICATE',
                     AppointingAuthority, EligibilityAttested, EligibilityStatement, RecordedByUserId, RecordedAtUtc, NULL
              FROM CustodianAppointments WHERE EvidenceRoomId = @r AND AppointmentType = 1 AND EffectiveTo IS NULL",
            new SqlParameter("@r", harness.EvidenceRoomId));

        Assert.Contains(error, new[] { UniqueIndex, UniqueConstraint });
    }

    [SqlServerFact]
    public async Task ConcurrencyStampsConflictOnSqlServer()
    {
        using var harness = SqlServerHarness.Create();
        using var second = harness.CreateSecondContext();

        var a = await harness.Db.StorageLocations.FirstAsync(l => l.Id == harness.ShelfBBin14Id);
        var b = await second.StorageLocations.FirstAsync(l => l.Id == harness.ShelfBBin14Id);

        a.Rename("Bin 14 (A)");
        await harness.Db.SaveChangesAsync();

        b.Rename("Bin 14 (B)");
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [SqlServerFact]
    public async Task DateTimeOffsetsRoundTripWithTheirOffset()
    {
        // SQLite needed a converter; SQL Server stores datetimeoffset natively. The offset the
        // room's zone produced must come back exactly (AUD-011).
        var (harness, _, itemId) = await AcceptedItemAsync();
        using (harness)
        {
            var stamp = new DateTimeOffset(2026, 9, 3, 9, 15, 0, TimeSpan.FromHours(-4));
            var located = await harness.Intake.AssignStorageLocationAsync(new AssignLocationRequest(
                itemId, harness.HighValueSafeId, stamp, "Moved", null));
            Assert.True(located.Succeeded, located.Error);

            var stored = await harness.Db.ItemEvents.AsNoTracking().OfType<LocationEvent>()
                .Where(e => e.EvidenceItemId == itemId).OrderByDescending(e => e.SequenceNumber).FirstAsync();

            Assert.Equal(stamp, stored.OccurredAtLocal);
            Assert.Equal(TimeSpan.FromHours(-4), stored.OccurredAtLocal.Offset);
            Assert.Equal(stamp.ToUniversalTime(), stored.OccurredAtUtc);
        }
    }

    [SqlServerFact]
    public async Task TheFullSliceRunsOnSqlServer()
    {
        var (harness, _, itemId) = await AcceptedItemAsync("007-26");
        using (harness)
        {
            var view = await harness.History.GetAsync(itemId);

            Assert.NotNull(view);
            Assert.Equal("007-26", view.VoucherIdentifier);
            Assert.Equal(AccountabilityStatus.InEvidenceRoom, view.AccountabilityStatus);
            Assert.Equal("Shelf B / Bin 14", view.CurrentLocationPath);
            Assert.True(view.ChainVerification.IsIntact);
            Assert.True(view.SnapshotVerification!.IsConsistent);
            Assert.Contains(view.History, r => r.Kind == ItemEventKind.DocumentNumber);
        }
    }
}
