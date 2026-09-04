using System.Security.Cryptography;
using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Emc.Application.Documents;

public sealed record UploadSourceDocumentRequest(
    int EvidenceRoomId,
    int? CaseId,
    int? VoucherId,
    SourceDocumentType DocumentType,
    ScanProvenance Provenance,
    string OriginalFilename,
    byte[] Content,
    string ClassificationMarking,
    string? ProvenanceNotes = null);

/// <summary>
/// Where a document stands with rendering. DERIVED from its render jobs and runs, never stored on
/// the document (DOC-015): the document is immutable, and "rendered" is a fact about the latest
/// successful run.
/// </summary>
public enum DocumentRenderStatus
{
    NotRequested = 0,
    Queued = 1,
    Rendering = 2,
    Rendered = 3,
    Failed = 4
}

public sealed record SourceDocumentPageRow(int PageNumber, int WidthPx, int HeightPx, int RenderDpi);

public sealed record RenderJobRow(int JobId, RenderJobStatus Status, int Attempts, DateTimeOffset RequestedAtUtc, DateTimeOffset? FinishedAtUtc, RenderFailureCategory LastFailureCategory);

public sealed record RenderRunRow(int RunId, int RenderJobId, DateTimeOffset CompletedAtUtc, RenderRunOutcome Outcome, RenderFailureCategory FailureCategory, int? PageCount, string RendererVersion);

public sealed record SourceDocumentView(
    int Id,
    int EvidenceRoomId,
    int? CaseId,
    int? VoucherId,
    string? VoucherIdentifier,
    SourceDocumentType DocumentType,
    ScanProvenance Provenance,
    string OriginalFilename,
    long ContentLength,
    string Sha256,
    string ReceivedByName,
    DateTimeOffset ReceivedAtUtc,
    string ClassificationMarking,
    string? ProvenanceNotes,
    DocumentRenderStatus RenderStatus,
    RenderFailureCategory LastRenderFailure,
    bool HasOpenRenderJob,
    int? CurrentRenderRunId,
    int? PageCount,
    IReadOnlyList<SourceDocumentPageRow> Pages,
    IReadOnlyList<RenderJobRow> RenderJobs,
    IReadOnlyList<RenderRunRow> RenderRuns);

public sealed record SourceDocumentListRow(
    int Id, SourceDocumentType DocumentType, ScanProvenance Provenance, string OriginalFilename,
    int? PageCount, DateTimeOffset ReceivedAtUtc, DocumentRenderStatus RenderStatus);

public interface ISourceDocumentService
{
    /// <summary>Validates the envelope, stores and hashes the bytes, records the document and queues its render job. Never parses the PDF (DOC-014).</summary>
    Task<OperationResult<int>> UploadAsync(UploadSourceDocumentRequest request, CancellationToken ct = default);

    /// <summary>Metadata, derived render state and the current page list, or null when absent OR unauthorized (indistinguishable).</summary>
    Task<SourceDocumentView?> GetAsync(int documentId, CancellationToken ct = default);

    Task<IReadOnlyList<SourceDocumentListRow>> ListForVoucherAsync(int voucherId, CancellationToken ct = default);

    /// <summary>A page image from the CURRENT successful render run. Authorizes on the owning room BEFORE any bytes are read; null when absent or unauthorized.</summary>
    Task<Stream?> OpenPageImageAsync(int documentId, int pageNumber, CancellationToken ct = default);

    /// <summary>The original PDF bytes. Requires the download permission; every success is audit logged (DOC-009).</summary>
    Task<Stream?> OpenOriginalForDownloadAsync(int documentId, CancellationToken ct = default);

    /// <summary>Queues a new render job for a document whose rendering failed (or to re-render). A new attempt; every earlier run stays on record (DOC-015).</summary>
    Task<OperationResult<int>> RequestRenderAsync(int documentId, CancellationToken ct = default);
}

/// <summary>
/// Source-document receipt, viewing and download. Every operation authorizes on the owning
/// evidence room before touching the store; an unauthorized document reads as absent (IAM-018).
///
/// Receipt treats the upload as hostile and DOES NOT PARSE IT (DOC-014): content validation of
/// the envelope only (DOC-003), size limits (DOC-004) at this layer as well as the request layer,
/// active-content detection by token, then the bytes are stored under a generated key (DOC-006),
/// hashed from what was written (AUD-022), and a render job is queued for the worker. Page count,
/// page dimensions and the render itself happen in the worker's killable child process. If the
/// database write fails, the blob it would have referenced is removed.
/// </summary>
public sealed class SourceDocumentService : ISourceDocumentService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly ISourceDocumentStore _store;
    private readonly SourceDocumentOptions _options;

    public SourceDocumentService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        ICurrentUser currentUser,
        IAuditRecorder audit,
        IClock clock,
        ISourceDocumentStore store,
        IOptions<SourceDocumentOptions> options)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _clock = clock;
        _store = store;
        _options = options.Value;
    }

    public async Task<OperationResult<int>> UploadAsync(UploadSourceDocumentRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The room the document is claimed for must be the room its voucher/case belongs to.
        // For a voucher-attached document the CASE is the voucher's case, derived here and never
        // taken from the client (DOC-013): a document cannot say "voucher from case A, case B".
        int? ownerRoom = null;
        string? voucherIdentifier = null;
        var caseIdForDocument = request.CaseId;

        if (request.VoucherId is int voucherId)
        {
            var voucher = await _db.EvidenceVouchers.AsNoTracking()
                .Include(v => v.DocumentNumberAssignments)
                .FirstOrDefaultAsync(v => v.Id == voucherId, ct);
            ownerRoom = voucher?.EvidenceRoomId;
            voucherIdentifier = voucher?.DisplayIdentifier;
            if (voucher is not null)
            {
                if (request.CaseId is int claimed && claimed != voucher.CaseId)
                {
                    return OperationResult<int>.Failure(
                        "The document names a case that is not the voucher's case. A voucher-attached document belongs to the voucher's own case; leave the case blank or name that case.", "DOC-013");
                }

                caseIdForDocument = voucher.CaseId;
            }
        }
        else if (request.CaseId is int caseId)
        {
            ownerRoom = await _db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => (int?)c.EvidenceRoomId).FirstOrDefaultAsync(ct);
        }

        if (ownerRoom is null || ownerRoom != request.EvidenceRoomId)
        {
            return OperationResult<int>.Failure("The voucher or case was not found in this evidence room.", "DOC-001");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.UploadSourceDocument, request.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(SourceDocument), voucherIdentifier, reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult<int>.Failure(decision.Reason!, decision.RequirementId);
        }

        // SEC-003. The marking may not exceed what the deployment is accredited for.
        var configuration = await _db.SystemConfigurations.AsNoTracking().FirstOrDefaultAsync(ct);
        var marking = Guard.TrimToNull(request.ClassificationMarking) ?? configuration?.AccreditedClassificationLevel ?? "UNCLASSIFIED";
        if (configuration is not null && !string.Equals(marking, configuration.AccreditedClassificationLevel, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(marking, "UNCLASSIFIED", StringComparison.OrdinalIgnoreCase))
        {
            return OperationResult<int>.Failure(
                $"This deployment is accredited for {configuration.AccreditedClassificationLevel} (DEC-06). A document marked "
                + $"\"{marking}\" cannot be stored here.", "SEC-003");
        }

        // DOC-003 / DOC-004 - the envelope by content, not extension; size at this layer as well
        // as the request layer. This is a byte scan, not a parse: no PDF library runs here.
        var validation = PdfContentValidator.Validate(request.Content, _options.MaxContentBytes);
        if (!validation.IsValid)
        {
            return OperationResult<int>.Failure(validation.Error!, validation.RequirementId!);
        }

        var sha256 = Convert.ToHexString(SHA256.HashData(request.Content)).ToLowerInvariant();
        var now = _clock.UtcNow;

        // DOC-010 - a repeated request, not a second document.
        var window = now.AddSeconds(-_options.DuplicateRequestWindowSeconds);
        var repeated = await _db.SourceDocuments.AsNoTracking().AnyAsync(d =>
            d.Sha256 == sha256 && d.EvidenceRoomId == request.EvidenceRoomId
            && d.VoucherId == request.VoucherId && d.CaseId == caseIdForDocument
            && d.ReceivedByUserId == _currentUser.UserId && d.ReceivedAtUtc >= window, ct);
        if (repeated)
        {
            return OperationResult<int>.Failure(
                "These exact bytes were received from you for this record moments ago. The earlier upload stands; this looks like a repeated request.", "DOC-010");
        }

        var warnings = new List<string>();

        // Same bytes elsewhere in the room: shown, never silently merged (DOC-011). Different
        // evidentiary contexts may legitimately hold the same scan.
        var sameBytes = await _db.SourceDocuments.AsNoTracking()
            .Where(d => d.Sha256 == sha256 && d.EvidenceRoomId == request.EvidenceRoomId)
            .Select(d => d.Id).ToListAsync(ct);
        if (sameBytes.Count > 0)
        {
            warnings.Add($"A document with identical content already exists in this evidence room (document {string.Join(", ", sameBytes)}). Both are kept; nothing was merged.");
        }

        if (validation.ContainsActiveContent)
        {
            warnings.Add("The PDF contains active content (script, launch or open actions). It is never executed: only server-rendered page images are shown, and the original is available only by audited download.");
        }

        warnings.Add("Page images are rendered by the worker, in a separate process; until then the document has no page images. Refresh the document page for its render status.");

        // Store the original, then the record and its render job in one save - and unwind the
        // blob if the record cannot be written.
        string? writtenKey = null;
        try
        {
            using var content = new MemoryStream(request.Content, writable: false);
            var original = await _store.WriteAsync("documents", content, ct);
            writtenKey = original.StorageKey;

            if (!string.Equals(original.Sha256, sha256, StringComparison.Ordinal) || original.Length != request.Content.LongLength)
            {
                throw new InvalidOperationException("The store did not persist the bytes it was given.");
            }

            var document = new SourceDocument(
                request.EvidenceRoomId, caseIdForDocument, request.VoucherId, request.DocumentType, request.Provenance,
                request.OriginalFilename, original.Length, original.Sha256, original.StorageKey,
                _currentUser.UserId, now, marking, request.ProvenanceNotes);

            _db.SourceDocuments.Add(document);
            _db.DocumentRenderJobs.Add(new DocumentRenderJob(document, _currentUser.UserId, now));
            _audit.Record(
                AuditEventType.AccountabilityActionRecorded, nameof(SourceDocument), voucherIdentifier,
                newValue: $"{document.DocumentType}, {document.Provenance}, {original.Length} bytes, sha256 {sha256[..12]}…; render queued");
            await _db.SaveChangesAsync(ct);

            return OperationResult<int>.Success(document.Id, [.. warnings]);
        }
        catch (DomainRuleViolationException ex)
        {
            await UnwindAsync(writtenKey, ct);
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }
        catch
        {
            await UnwindAsync(writtenKey, ct);
            throw;
        }
    }

    public async Task<OperationResult<int>> RequestRenderAsync(int documentId, CancellationToken ct = default)
    {
        var document = await AuthorizedDocumentAsync(documentId, EmcPermissions.UploadSourceDocument, ct);
        if (document is null)
        {
            return OperationResult<int>.Failure("The document was not found.", "DOC-014");
        }

        var open = await _db.DocumentRenderJobs.AnyAsync(j => j.SourceDocumentId == documentId && (j.Status == RenderJobStatus.Queued || j.Status == RenderJobStatus.Running), ct);
        if (open)
        {
            return OperationResult<int>.Failure("A render is already queued or running for this document.", "DOC-014");
        }

        var job = new DocumentRenderJob(document.Id, document.EvidenceRoomId, _currentUser.UserId, _clock.UtcNow);
        _db.DocumentRenderJobs.Add(job);
        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(DocumentRenderJob), null,
            newValue: $"Render requested for source document {documentId} in room {document.EvidenceRoomId}", reason: "DOC-014");
        await _db.SaveChangesAsync(ct);
        return OperationResult<int>.Success(job.Id, "Render queued. Every earlier attempt stays on record; the newest successful one becomes the current page set.");
    }

    public async Task<SourceDocumentView?> GetAsync(int documentId, CancellationToken ct = default)
    {
        var document = await AuthorizedDocumentAsync(documentId, EmcPermissions.ViewSourceDocument, ct);
        if (document is null)
        {
            return null;
        }

        var name = await _db.Users.AsNoTracking().Where(u => u.Id == document.ReceivedByUserId).Select(u => u.PrintedNameAndGrade).FirstOrDefaultAsync(ct);
        string? voucherIdentifier = null;
        if (document.VoucherId is int vid)
        {
            var voucher = await _db.EvidenceVouchers.AsNoTracking().Include(v => v.DocumentNumberAssignments).FirstOrDefaultAsync(v => v.Id == vid, ct);
            voucherIdentifier = voucher?.DisplayIdentifier;
        }

        var jobs = await _db.DocumentRenderJobs.AsNoTracking().Where(j => j.SourceDocumentId == documentId)
            .OrderByDescending(j => j.RequestedAtUtc).ThenByDescending(j => j.Id)
            .Select(j => new RenderJobRow(j.Id, j.Status, j.Attempts, j.RequestedAtUtc, j.FinishedAtUtc, j.LastFailureCategory))
            .ToListAsync(ct);
        var runs = await _db.DocumentRenderRuns.AsNoTracking().Where(r => r.SourceDocumentId == documentId)
            .OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.Id)
            .Select(r => new RenderRunRow(r.Id, r.RenderJobId, r.CompletedAtUtc, r.Outcome, r.FailureCategory, r.PageCount, r.RendererVersion))
            .ToListAsync(ct);

        var current = await CurrentRenderRunAsync(documentId, ct);
        var state = DeriveState(jobs, current);

        return new SourceDocumentView(
            document.Id, document.EvidenceRoomId, document.CaseId, document.VoucherId, voucherIdentifier,
            document.DocumentType, document.Provenance, document.OriginalFilename, document.ContentLength,
            document.Sha256, name ?? "(unknown user)", document.ReceivedAtUtc,
            document.ClassificationMarking, document.ProvenanceNotes,
            state.Status, state.LastFailure, state.HasOpenJob, current?.Id, current?.PageCount,
            current is null ? [] : current.Pages.OrderBy(p => p.PageNumber).Select(p => new SourceDocumentPageRow(p.PageNumber, p.WidthPx, p.HeightPx, current.RenderDpi)).ToList(),
            jobs, runs);
    }

    public async Task<IReadOnlyList<SourceDocumentListRow>> ListForVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var roomId = await _db.EvidenceVouchers.AsNoTracking().Where(v => v.Id == voucherId).Select(v => (int?)v.EvidenceRoomId).FirstOrDefaultAsync(ct);
        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewSourceDocument, roomId, ct)).IsAllowed)
        {
            return [];
        }

        var documents = await _db.SourceDocuments.AsNoTracking()
            .Where(d => d.VoucherId == voucherId)
            .OrderBy(d => d.ReceivedAtUtc)
            .Select(d => new { d.Id, d.DocumentType, d.Provenance, d.OriginalFilename, d.ReceivedAtUtc })
            .ToListAsync(ct);
        if (documents.Count == 0)
        {
            return [];
        }

        var ids = documents.Select(d => d.Id).ToList();
        var jobs = await _db.DocumentRenderJobs.AsNoTracking().Where(j => ids.Contains(j.SourceDocumentId))
            .Select(j => new { j.SourceDocumentId, Row = new RenderJobRow(j.Id, j.Status, j.Attempts, j.RequestedAtUtc, j.FinishedAtUtc, j.LastFailureCategory) })
            .ToListAsync(ct);
        var successes = await _db.DocumentRenderRuns.AsNoTracking()
            .Where(r => ids.Contains(r.SourceDocumentId) && r.Outcome == RenderRunOutcome.Succeeded)
            .Select(r => new { r.SourceDocumentId, r.Id, r.CompletedAtUtc, r.PageCount })
            .ToListAsync(ct);

        var rows = new List<SourceDocumentListRow>(documents.Count);
        foreach (var d in documents)
        {
            var current = successes.Where(r => r.SourceDocumentId == d.Id).OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.Id).FirstOrDefault();
            var state = DeriveState(jobs.Where(j => j.SourceDocumentId == d.Id).Select(j => j.Row).OrderByDescending(j => j.RequestedAtUtc).ThenByDescending(j => j.JobId).ToList(), current is null ? null : (int?)current.Id);
            rows.Add(new SourceDocumentListRow(d.Id, d.DocumentType, d.Provenance, d.OriginalFilename, current?.PageCount, d.ReceivedAtUtc, state.Status));
        }

        return rows;
    }

    public async Task<Stream?> OpenPageImageAsync(int documentId, int pageNumber, CancellationToken ct = default)
    {
        // Authorization FIRST, on the owning room, before the store is touched at all.
        var document = await AuthorizedDocumentAsync(documentId, EmcPermissions.ViewSourceDocument, ct);
        if (document is null)
        {
            return null;
        }

        var current = await CurrentRenderRunAsync(documentId, ct);
        var page = current?.Pages.FirstOrDefault(p => p.PageNumber == pageNumber);
        if (page is null)
        {
            return null;
        }

        return await _store.OpenReadAsync(page.StorageKey, ct);
    }

    public async Task<Stream?> OpenOriginalForDownloadAsync(int documentId, CancellationToken ct = default)
    {
        var document = await AuthorizedDocumentAsync(documentId, EmcPermissions.DownloadSourceDocument, ct);
        if (document is null)
        {
            return null;
        }

        var stream = await _store.OpenReadAsync(document.StorageKey, ct);
        if (stream is null)
        {
            return null;
        }

        // DOC-009. The audit row names the document and room, not the file's content.
        _audit.Record(AuditEventType.SourceDocumentDownloaded, nameof(SourceDocument), document.Id.ToString(),
            newValue: $"room {document.EvidenceRoomId}, {document.DocumentType}, {document.ContentLength} bytes");
        await _db.SaveChangesAsync(ct);
        return stream;
    }

    /// <summary>The latest SUCCESSFUL render run with its pages: the current page set (DOC-015). Null when none has succeeded.</summary>
    private Task<DocumentRenderRun?> CurrentRenderRunAsync(int documentId, CancellationToken ct)
        => _db.DocumentRenderRuns.AsNoTracking().Include(r => r.Pages)
            .Where(r => r.SourceDocumentId == documentId && r.Outcome == RenderRunOutcome.Succeeded)
            .OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

    private static (DocumentRenderStatus Status, RenderFailureCategory LastFailure, bool HasOpenJob) DeriveState(IReadOnlyList<RenderJobRow> jobsNewestFirst, DocumentRenderRun? current)
        => DeriveState(jobsNewestFirst, current?.Id);

    private static (DocumentRenderStatus Status, RenderFailureCategory LastFailure, bool HasOpenJob) DeriveState(IReadOnlyList<RenderJobRow> jobsNewestFirst, int? currentRunId)
    {
        var open = jobsNewestFirst.FirstOrDefault(j => j.Status is RenderJobStatus.Queued or RenderJobStatus.Running);
        var newest = jobsNewestFirst.FirstOrDefault();
        var lastFailure = newest?.LastFailureCategory ?? RenderFailureCategory.None;

        if (currentRunId is not null)
        {
            return (DocumentRenderStatus.Rendered, lastFailure, open is not null);
        }

        if (open is not null)
        {
            return (open.Status == RenderJobStatus.Running ? DocumentRenderStatus.Rendering : DocumentRenderStatus.Queued, lastFailure, true);
        }

        return (newest is null ? DocumentRenderStatus.NotRequested : DocumentRenderStatus.Failed, lastFailure, false);
    }

    private async Task<SourceDocument?> AuthorizedDocumentAsync(int documentId, string permission, CancellationToken ct)
    {
        // Room first, by id only, so nothing about the document is loaded before the decision.
        var roomId = await _db.SourceDocuments.AsNoTracking()
            .Where(d => d.Id == documentId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);

        if (roomId is null || !(await _authorization.AuthorizeAsync(permission, roomId, ct)).IsAllowed)
        {
            return null;
        }

        return await _db.SourceDocuments.AsNoTracking().FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    private async Task UnwindAsync(string? key, CancellationToken ct)
    {
        if (key is null)
        {
            return;
        }

        try { await _store.TryDeleteAsync(key, ct); } catch { /* best effort; the orphan sweep is the backstop */ }
    }
}
