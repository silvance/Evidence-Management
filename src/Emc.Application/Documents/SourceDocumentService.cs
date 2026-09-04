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

public sealed record SourceDocumentPageRow(int PageNumber, int WidthPx, int HeightPx, int RenderDpi);

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
    int PageCount,
    string ReceivedByName,
    DateTimeOffset ReceivedAtUtc,
    string ClassificationMarking,
    SourceDocumentImportStatus ImportStatus,
    string? ProvenanceNotes,
    IReadOnlyList<SourceDocumentPageRow> Pages);

public sealed record SourceDocumentListRow(
    int Id, SourceDocumentType DocumentType, ScanProvenance Provenance, string OriginalFilename,
    int PageCount, DateTimeOffset ReceivedAtUtc, SourceDocumentImportStatus ImportStatus);

public interface ISourceDocumentService
{
    Task<OperationResult<int>> UploadAsync(UploadSourceDocumentRequest request, CancellationToken ct = default);

    /// <summary>Metadata and page list, or null when absent OR unauthorized (indistinguishable).</summary>
    Task<SourceDocumentView?> GetAsync(int documentId, CancellationToken ct = default);

    Task<IReadOnlyList<SourceDocumentListRow>> ListForVoucherAsync(int voucherId, CancellationToken ct = default);

    /// <summary>A rendered page image. Authorizes on the owning room BEFORE any bytes are read; null when absent or unauthorized.</summary>
    Task<Stream?> OpenPageImageAsync(int documentId, int pageNumber, CancellationToken ct = default);

    /// <summary>The original PDF bytes. Requires the download permission; every success is audit logged (DOC-009).</summary>
    Task<Stream?> OpenOriginalForDownloadAsync(int documentId, CancellationToken ct = default);
}

/// <summary>
/// Source-document ingestion, viewing and download. Every operation authorizes on the owning
/// evidence room before touching the store; an unauthorized document reads as absent (IAM-018).
///
/// Ingestion treats the upload as hostile: content validation (DOC-003), size limits (DOC-004)
/// at this layer as well as the request layer, page-count and page-dimension limits before any
/// rendering, a render timeout, and a rasterizer that executes nothing (DOC-005). Bytes are
/// stored under a generated key (DOC-006) and hashed from what was written (AUD-022). If the
/// database write fails, the blobs it would have referenced are removed.
/// </summary>
public sealed class SourceDocumentService : ISourceDocumentService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly ISourceDocumentStore _store;
    private readonly IPdfRasterizer _rasterizer;
    private readonly SourceDocumentOptions _options;

    public SourceDocumentService(
        IEmcDbContext db,
        IEvidenceAuthorizationService authorization,
        ICurrentUser currentUser,
        IAuditRecorder audit,
        IClock clock,
        ISourceDocumentStore store,
        IPdfRasterizer rasterizer,
        IOptions<SourceDocumentOptions> options)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _clock = clock;
        _store = store;
        _rasterizer = rasterizer;
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

        // DOC-003 / DOC-004 - content, not extension; size at this layer as well as the request layer.
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

        // The second gate: can the rasterizer even open it, and is it within limits, BEFORE any
        // rendering or storage.
        int pageCount;
        IReadOnlyList<PdfPageDimensions> dimensions;
        try
        {
            pageCount = _rasterizer.GetPageCount(request.Content);
            dimensions = _rasterizer.GetPageDimensions(request.Content);
        }
        catch (MalformedPdfException ex)
        {
            return OperationResult<int>.Failure($"The upload could not be opened as a PDF: {ex.Message}", "DOC-003");
        }

        if (pageCount < 1 || pageCount > _options.MaxPageCount)
        {
            return OperationResult<int>.Failure(
                $"The PDF has {pageCount} pages; the limit is {_options.MaxPageCount}.", "DOC-004");
        }

        foreach (var page in dimensions)
        {
            var pixels = (long)Math.Ceiling(page.WidthPoints / 72.0 * _options.RenderDpi) * (long)Math.Ceiling(page.HeightPoints / 72.0 * _options.RenderDpi);
            if (page.WidthPoints <= 0 || page.HeightPoints <= 0 || pixels > _options.MaxPixelsPerPage)
            {
                return OperationResult<int>.Failure(
                    $"Page {page.PageNumber} is {page.WidthPoints / 72.0:F1} by {page.HeightPoints / 72.0:F1} inches, beyond what can be rendered.", "DOC-004");
            }
        }

        // Store the original, then render pages, then the record - and unwind the blobs if the
        // record cannot be written.
        var written = new List<string>();
        try
        {
            using var content = new MemoryStream(request.Content, writable: false);
            var original = await _store.WriteAsync("documents", content, ct);
            written.Add(original.StorageKey);

            if (!string.Equals(original.Sha256, sha256, StringComparison.Ordinal) || original.Length != request.Content.LongLength)
            {
                throw new InvalidOperationException("The store did not persist the bytes it was given.");
            }

            var rendered = new List<(RenderedPage Page, StoredBlob Blob)>();
            var status = SourceDocumentImportStatus.Rendered;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.RenderTimeoutSeconds));

                for (var p = 1; p <= pageCount; p++)
                {
                    var page = _rasterizer.Render(request.Content, p, _options.RenderDpi, timeout.Token);
                    using var png = new MemoryStream(page.Png, writable: false);
                    var blob = await _store.WriteAsync("pages", png, ct);
                    written.Add(blob.StorageKey);
                    rendered.Add((page, blob));
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or MalformedPdfException && !ct.IsCancellationRequested)
            {
                // Rendering failed or ran out of time. The original is kept and hashed; no
                // page images. Nothing about the document is logged.
                status = SourceDocumentImportStatus.RenderFailed;
                warnings.Add("Page images could not be rendered within limits. The document is stored and hashed; it can be downloaded by an authorized user, and rendering can be retried later.");
            }

            var document = new SourceDocument(
                request.EvidenceRoomId, caseIdForDocument, request.VoucherId, request.DocumentType, request.Provenance,
                request.OriginalFilename, original.Length, original.Sha256, pageCount, original.StorageKey,
                _currentUser.UserId, now, marking, status, request.ProvenanceNotes);

            foreach (var (page, blob) in rendered)
            {
                document.AddRenderedPage(page.PageNumber, page.WidthPx, page.HeightPx, page.Dpi, blob.StorageKey, blob.Sha256, blob.Length, _rasterizer.RendererVersion, now);
            }

            _db.SourceDocuments.Add(document);
            _audit.Record(
                AuditEventType.AccountabilityActionRecorded, nameof(SourceDocument), voucherIdentifier,
                newValue: $"{document.DocumentType}, {document.Provenance}, {pageCount} pages, sha256 {sha256[..12]}…");
            await _db.SaveChangesAsync(ct);

            return OperationResult<int>.Success(document.Id, [.. warnings]);
        }
        catch (DomainRuleViolationException ex)
        {
            await UnwindAsync(written, ct);
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }
        catch
        {
            await UnwindAsync(written, ct);
            throw;
        }
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

        return new SourceDocumentView(
            document.Id, document.EvidenceRoomId, document.CaseId, document.VoucherId, voucherIdentifier,
            document.DocumentType, document.Provenance, document.OriginalFilename, document.ContentLength,
            document.Sha256, document.PageCount, name ?? "(unknown user)", document.ReceivedAtUtc,
            document.ClassificationMarking, document.ImportStatus, document.ProvenanceNotes,
            document.Pages.OrderBy(p => p.PageNumber).Select(p => new SourceDocumentPageRow(p.PageNumber, p.WidthPx, p.HeightPx, p.RenderDpi)).ToList());
    }

    public async Task<IReadOnlyList<SourceDocumentListRow>> ListForVoucherAsync(int voucherId, CancellationToken ct = default)
    {
        var roomId = await _db.EvidenceVouchers.AsNoTracking().Where(v => v.Id == voucherId).Select(v => (int?)v.EvidenceRoomId).FirstOrDefaultAsync(ct);
        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewSourceDocument, roomId, ct)).IsAllowed)
        {
            return [];
        }

        return await _db.SourceDocuments.AsNoTracking()
            .Where(d => d.VoucherId == voucherId)
            .OrderBy(d => d.ReceivedAtUtc)
            .Select(d => new SourceDocumentListRow(d.Id, d.DocumentType, d.Provenance, d.OriginalFilename, d.PageCount, d.ReceivedAtUtc, d.ImportStatus))
            .ToListAsync(ct);
    }

    public async Task<Stream?> OpenPageImageAsync(int documentId, int pageNumber, CancellationToken ct = default)
    {
        // Authorization FIRST, on the owning room, before the store is touched at all.
        var document = await AuthorizedDocumentAsync(documentId, EmcPermissions.ViewSourceDocument, ct);
        var page = document?.Pages.FirstOrDefault(p => p.PageNumber == pageNumber);
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

    private async Task<SourceDocument?> AuthorizedDocumentAsync(int documentId, string permission, CancellationToken ct)
    {
        // Room first, by id only, so nothing about the document is loaded before the decision.
        var roomId = await _db.SourceDocuments.AsNoTracking()
            .Where(d => d.Id == documentId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);

        if (roomId is null || !(await _authorization.AuthorizeAsync(permission, roomId, ct)).IsAllowed)
        {
            return null;
        }

        return await _db.SourceDocuments.AsNoTracking().Include(d => d.Pages).FirstOrDefaultAsync(d => d.Id == documentId, ct);
    }

    private async Task UnwindAsync(List<string> keys, CancellationToken ct)
    {
        foreach (var key in keys)
        {
            try { await _store.TryDeleteAsync(key, ct); } catch { /* best effort; the orphan sweep is the backstop */ }
        }
    }
}
