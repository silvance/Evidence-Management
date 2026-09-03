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
    /// INSERT ONLY. Both triggers reject unconditionally.
    ///
    /// The earlier ItemEvents trigger permitted an UPDATE that set only a forward "superseded by"
    /// pointer, and had to prove every other column was unchanged. It compared only the columns
    /// common to all event types, so a table-per-hierarchy subtype column - StorageLocationPath,
    /// PurposeOfChangeOfCustody, a seal field - could be rewritten alongside a legitimate
    /// supersession and pass. Corrections now use backward references, so there is no legitimate
    /// UPDATE left to allow and the trigger needs no column comparison at all.
    /// </summary>
    public const string CreateItemEventsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_ItemEvents_AppendOnly_Update
        ON dbo.ItemEvents
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50001,
                'ItemEvents is append-only and cannot be modified. AR 195-5 para 2-5b(5) requires an erroneous entry to remain readable - it is voided with a single line and initialed, never erased. Record a correction instead.',
                1;
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
    /// AR 195-5 2-4c and 2-7g. A document-number assignment records an act performed in the
    /// authoritative ledger. Para 2-7g supersedes a number by recording a NEW assignment that
    /// names the one it replaces; the old row is never touched, so this trigger is unconditional
    /// too.
    /// </summary>
    public const string CreateDocumentNumberUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_DocumentNumbers_AppendOnly_Update
        ON dbo.OfficialDocumentNumberAssignments
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50005,
                'OfficialDocumentNumberAssignments is append-only and cannot be modified. AR 195-5 para 2-7g supersedes a prior document number with a new assignment and keeps the prior one legible; it does not overwrite it.',
                1;
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
