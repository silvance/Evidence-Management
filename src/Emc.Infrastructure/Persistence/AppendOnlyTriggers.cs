namespace Emc.Infrastructure.Persistence;

/// <summary>
/// Layer 3 of the append-only enforcement (docs/architecture.md §4.2): SQL Server triggers that
/// reject UPDATE and DELETE on the accountability tables.
///
/// Layers 1 and 2 (domain immutability, and the SaveChanges guard) protect against mistakes made
/// THROUGH the application. This layer is what protects against changes made OUTSIDE it -
/// including by an administrator using SSMS - which is the case that matters for IAM-009.
///
/// It is not absolute: a principal holding ALTER can drop a trigger. That is why the per-item
/// hash chain exists as well (AUD-008), and why the deployment guide grants the application's
/// runtime login db_datareader/db_datawriter/EXECUTE and NOT db_owner or db_ddladmin.
///
/// SQL Server only. SQLite test runs exercise layers 1 and 2, which have their own tests.
/// </summary>
public static class AppendOnlyTriggers
{
    /// <summary>
    /// The one permitted mutation on an ItemEvent: SupersededByEventId, null -> value, once
    /// (invariant I-14). Modelled on AR 195-5 2-5b(5), where the erroneous entry is struck
    /// through and stays readable rather than being erased.
    /// </summary>
    public const string CreateItemEventsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_ItemEvents_AppendOnly_Update
        ON dbo.ItemEvents
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;

            -- Reject any change other than setting a previously-null supersession link.
            IF EXISTS (
                SELECT 1
                FROM inserted i
                INNER JOIN deleted d ON d.Id = i.Id
                WHERE d.SupersededByEventId IS NOT NULL
                   OR i.SupersededByEventId IS NULL
                   OR i.EvidenceItemId      <> d.EvidenceItemId
                   OR i.SequenceNumber      <> d.SequenceNumber
                   OR i.OccurredAtUtc       <> d.OccurredAtUtc
                   OR i.RecordedAtUtc       <> d.RecordedAtUtc
                   OR i.RecordedByUserId    <> d.RecordedByUserId
                   OR i.EventHash           <> d.EventHash
                   OR ISNULL(i.PreviousEventHash, '') <> ISNULL(d.PreviousEventHash, '')
                   OR ISNULL(i.Notes, '')             <> ISNULL(d.Notes, '')
            )
            BEGIN
                THROW 50001,
                    'ItemEvents is append-only. AR 195-5 para 2-5b(5) requires an erroneous entry to remain readable - it is voided with a single line and initialed, never erased. Record a correction instead. The only permitted update is setting SupersededByEventId once, from NULL.',
                    1;
            END;

            UPDATE e
            SET e.SupersededByEventId = i.SupersededByEventId
            FROM dbo.ItemEvents e
            INNER JOIN inserted i ON i.Id = e.Id;
        END;
        """;

    public const string CreateItemEventsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_ItemEvents_AppendOnly_Delete
        ON dbo.ItemEvents
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50002,
                'ItemEvents is append-only and cannot be deleted. AR 195-5 para 2-5b(5) prohibits erasing an entry; para 1-7c(3) requires the error and the corrective action to be documented. Record a correction instead.',
                1;
        END;
        """;

    public const string CreateAuditEventsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_AuditEvents_AppendOnly_Update
        ON dbo.AuditEvents
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50003, 'AuditEvents is append-only and cannot be modified.', 1;
        END;
        """;

    public const string CreateAuditEventsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_AuditEvents_AppendOnly_Delete
        ON dbo.AuditEvents
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50004, 'AuditEvents is append-only and cannot be deleted.', 1;
        END;
        """;

    /// <summary>
    /// AR 195-5 2-4c and 2-7g. A document-number assignment is a record of an act performed in
    /// the authoritative ledger. It may be superseded (2-7g: the prior number "will be lined
    /// through in such a way that it remains legible") but never rewritten.
    /// </summary>
    public const string CreateDocumentNumberUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_DocumentNumbers_AppendOnly_Update
        ON dbo.OfficialDocumentNumberAssignments
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;

            IF EXISTS (
                SELECT 1
                FROM inserted i
                INNER JOIN deleted d ON d.Id = i.Id
                WHERE d.SupersededByAssignmentId IS NOT NULL
                   OR i.SupersededByAssignmentId IS NULL
                   OR i.DocumentNumber  <> d.DocumentNumber
                   OR i.EvidenceRoomId  <> d.EvidenceRoomId
                   OR i.VoucherId       <> d.VoucherId
                   OR i.Sequence        <> d.Sequence
                   OR i.CalendarYear    <> d.CalendarYear
                   OR i.EnteredByUserId <> d.EnteredByUserId
            )
            BEGIN
                THROW 50005,
                    'OfficialDocumentNumberAssignments is append-only. AR 195-5 para 2-7g supersedes a prior document number and keeps it legible; it does not overwrite it.',
                    1;
            END;

            UPDATE a
            SET a.SupersededByAssignmentId = i.SupersededByAssignmentId,
                a.SupersessionReason       = i.SupersessionReason,
                a.SupersededAtUtc          = i.SupersededAtUtc
            FROM dbo.OfficialDocumentNumberAssignments a
            INNER JOIN inserted i ON i.Id = a.Id;
        END;
        """;

    public const string CreateDocumentNumberDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_DocumentNumbers_AppendOnly_Delete
        ON dbo.OfficialDocumentNumberAssignments
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50006,
                'OfficialDocumentNumberAssignments is append-only and cannot be deleted.',
                1;
        END;
        """;

    public static IReadOnlyList<string> All =>
    [
        CreateItemEventsUpdateTrigger,
        CreateItemEventsDeleteTrigger,
        CreateAuditEventsUpdateTrigger,
        CreateAuditEventsDeleteTrigger,
        CreateDocumentNumberUpdateTrigger,
        CreateDocumentNumberDeleteTrigger
    ];

    public static IReadOnlyList<string> DropAll =>
    [
        "DROP TRIGGER IF EXISTS TR_ItemEvents_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_ItemEvents_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_AuditEvents_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_AuditEvents_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_DocumentNumbers_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_DocumentNumbers_AppendOnly_Delete;"
    ];
}
