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

    /// <summary>
    /// AR 195-5 2-3g. The record of a custodian's review - what was found wrong, who returned
    /// the form, who corrected it and when - is part of why a voucher was accepted when it was,
    /// and is kept on the same terms as the rest of the accountability record.
    /// </summary>
    public const string CreateVoucherReviewUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherReviewActions_AppendOnly_Update
        ON dbo.VoucherReviewActions
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50007,
                'VoucherReviewActions is append-only and cannot be modified. The record of a custodian review under AR 195-5 para 2-3g is kept as it happened.',
                1;
        END;
        """;

    public const string CreateVoucherReviewDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherReviewActions_AppendOnly_Delete
        ON dbo.VoucherReviewActions
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50008, 'VoucherReviewActions is append-only and cannot be deleted.', 1;
        END;
        """;

    /// <summary>
    /// AR 195-5 2-3g / VCH-025. A form revision is what the DA Form 4137 contained when it went to
    /// the custodian. It is the record that lets a corrected form differ from the submitted one
    /// without erasing anything, so it is kept on the same terms as the rest.
    /// </summary>
    public const string CreateFormRevisionsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherFormRevisions_AppendOnly_Update
        ON dbo.VoucherFormRevisions
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50009, 'VoucherFormRevisions is append-only and cannot be modified. A submitted DA Form 4137 revision is kept as it was submitted (AR 195-5 para 2-3g).', 1;
        END;
        """;

    public const string CreateFormRevisionsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherFormRevisions_AppendOnly_Delete
        ON dbo.VoucherFormRevisions
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50010, 'VoucherFormRevisions is append-only and cannot be deleted.', 1;
        END;
        """;

    public const string CreateFormRevisionLinesUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherFormRevisionLines_AppendOnly_Update
        ON dbo.VoucherFormRevisionLines
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50011, 'VoucherFormRevisionLines is append-only and cannot be modified.', 1;
        END;
        """;

    public const string CreateFormRevisionLinesDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_VoucherFormRevisionLines_AppendOnly_Delete
        ON dbo.VoucherFormRevisionLines
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50012, 'VoucherFormRevisionLines is append-only and cannot be deleted.', 1;
        END;
        """;

    /// <summary>AR 195-5 2-4f/2-4h. What happened to the paper DA Form 4137 is kept as it happened.</summary>
    public const string CreatePhysicalDocumentEventsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_PhysicalVoucherDocumentEvents_AppendOnly_Update
        ON dbo.PhysicalVoucherDocumentEvents
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50013, 'PhysicalVoucherDocumentEvents is append-only and cannot be modified.', 1;
        END;
        """;

    public const string CreatePhysicalDocumentEventsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_PhysicalVoucherDocumentEvents_AppendOnly_Delete
        ON dbo.PhysicalVoucherDocumentEvents
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50014, 'PhysicalVoucherDocumentEvents is append-only and cannot be deleted.', 1;
        END;
        """;

    /// <summary>DOC-002. A stored scan's record is immutable: its hash is what receipt recorded.</summary>
    public const string CreateSourceDocumentsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_SourceDocuments_AppendOnly_Update
        ON dbo.SourceDocuments
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50015, 'SourceDocuments is append-only and cannot be modified. A source document is an immutable companion copy; its recorded hash is what receipt recorded.', 1;
        END;
        """;

    public const string CreateSourceDocumentsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_SourceDocuments_AppendOnly_Delete
        ON dbo.SourceDocuments
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50016, 'SourceDocuments is append-only and cannot be deleted. Digital retention is undetermined (DEC-07); nothing is destroyed.', 1;
        END;
        """;

    public const string CreateSourceDocumentPagesUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_SourceDocumentPages_AppendOnly_Update
        ON dbo.SourceDocumentPages
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50017, 'SourceDocumentPages is append-only and cannot be modified.', 1;
        END;
        """;

    public const string CreateSourceDocumentPagesDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_SourceDocumentPages_AppendOnly_Delete
        ON dbo.SourceDocumentPages
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50018, 'SourceDocumentPages is append-only and cannot be deleted.', 1;
        END;
        """;


    public const string CreateOcrRunsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_OcrRuns_AppendOnly_Update
        ON dbo.OcrRuns
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50019, 'OcrRuns is append-only and cannot be modified: a run is a fact about what an engine read; re-run instead.', 1;
        END;
        """;

    public const string CreateOcrRunsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_OcrRuns_AppendOnly_Delete
        ON dbo.OcrRuns
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50020, 'OcrRuns is append-only and cannot be deleted: a run is a fact about what an engine read; re-run instead.', 1;
        END;
        """;

    public const string CreateExtractedFieldsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_ExtractedFields_AppendOnly_Update
        ON dbo.ExtractedFields
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50021, 'ExtractedFields is append-only and cannot be modified: the raw extraction is never edited (OCR-004); record a verification.', 1;
        END;
        """;

    public const string CreateExtractedFieldsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_ExtractedFields_AppendOnly_Delete
        ON dbo.ExtractedFields
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50022, 'ExtractedFields is append-only and cannot be deleted: the raw extraction is never edited (OCR-004); record a verification.', 1;
        END;
        """;

    public const string CreateFieldVerificationsUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_FieldVerifications_AppendOnly_Update
        ON dbo.FieldVerifications
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50023, 'FieldVerifications is append-only and cannot be modified: a second look is a second row.', 1;
        END;
        """;

    public const string CreateFieldVerificationsDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_FieldVerifications_AppendOnly_Delete
        ON dbo.FieldVerifications
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50024, 'FieldVerifications is append-only and cannot be deleted: a second look is a second row.', 1;
        END;
        """;


    public const string CreateOcrRunPagesUpdateTrigger = """
        CREATE OR ALTER TRIGGER TR_OcrRunPages_AppendOnly_Update
        ON dbo.OcrRunPages
        INSTEAD OF UPDATE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50025, 'OcrRunPages is append-only and cannot be modified: it is the image the engine read.', 1;
        END;
        """;

    public const string CreateOcrRunPagesDeleteTrigger = """
        CREATE OR ALTER TRIGGER TR_OcrRunPages_AppendOnly_Delete
        ON dbo.OcrRunPages
        INSTEAD OF DELETE
        AS
        BEGIN
            SET NOCOUNT ON;
            THROW 50026, 'OcrRunPages is append-only and cannot be deleted: it is the image the engine read.', 1;
        END;
        """;

    public static IReadOnlyList<string> All =>
    [
        CreateItemEventsUpdateTrigger,
        CreateItemEventsDeleteTrigger,
        CreateAuditEventsUpdateTrigger,
        CreateAuditEventsDeleteTrigger,
        CreateDocumentNumberUpdateTrigger,
        CreateDocumentNumberDeleteTrigger,
        CreateVoucherReviewUpdateTrigger,
        CreateVoucherReviewDeleteTrigger,
        CreateFormRevisionsUpdateTrigger,
        CreateFormRevisionsDeleteTrigger,
        CreateFormRevisionLinesUpdateTrigger,
        CreateFormRevisionLinesDeleteTrigger,
        CreatePhysicalDocumentEventsUpdateTrigger,
        CreatePhysicalDocumentEventsDeleteTrigger,
        CreateSourceDocumentsUpdateTrigger,
        CreateSourceDocumentsDeleteTrigger,
        CreateSourceDocumentPagesUpdateTrigger,
        CreateSourceDocumentPagesDeleteTrigger,
        CreateOcrRunsUpdateTrigger,
        CreateOcrRunsDeleteTrigger,
        CreateExtractedFieldsUpdateTrigger,
        CreateExtractedFieldsDeleteTrigger,
        CreateFieldVerificationsUpdateTrigger,
        CreateFieldVerificationsDeleteTrigger,
        CreateOcrRunPagesUpdateTrigger,
        CreateOcrRunPagesDeleteTrigger
    ];

    public static IReadOnlyList<string> DropAll =>
    [
        "DROP TRIGGER IF EXISTS TR_ItemEvents_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_ItemEvents_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_AuditEvents_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_AuditEvents_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_DocumentNumbers_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_DocumentNumbers_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_VoucherReviewActions_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_VoucherReviewActions_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_VoucherFormRevisions_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_VoucherFormRevisions_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_VoucherFormRevisionLines_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_VoucherFormRevisionLines_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_PhysicalVoucherDocumentEvents_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_PhysicalVoucherDocumentEvents_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_SourceDocuments_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_SourceDocuments_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_SourceDocumentPages_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_SourceDocumentPages_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_OcrRuns_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_OcrRuns_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_ExtractedFields_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_ExtractedFields_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_FieldVerifications_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_FieldVerifications_AppendOnly_Delete;",
        "DROP TRIGGER IF EXISTS TR_OcrRunPages_AppendOnly_Update;",
        "DROP TRIGGER IF EXISTS TR_OcrRunPages_AppendOnly_Delete;"
    ];
}
