using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Documents;
using Emc.Domain.Ocr;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Ocr;

public sealed record OcrJobRow(int JobId, OcrJobStatus Status, int Attempts, DateTimeOffset RequestedAtUtc, DateTimeOffset? FinishedAtUtc, OcrFailureCategory LastFailureCategory, string? LeasedByWorkerId);

public sealed record FieldVerificationRow(int Id, int VerifiedByUserId, string VerifiedByName, DateTimeOffset VerifiedAtUtc, FieldVerificationDecision Decision, string? EnteredValue, string? Note);

public sealed record ExtractedFieldRow(
    int FieldId, string FieldKey, int PageNumber, string RawText, string? NormalizedCandidate, decimal Confidence, ConfidenceBand Band,
    int Left, int Top, int Width, int Height, bool IsHighConsequence, bool RequiresVerification,
    FieldVerificationRow? Current, string? VerifiedValue, IReadOnlyList<FieldVerificationRow> History);

public sealed record OcrRunPageRow(int PageNumber, int WidthPx, int HeightPx, int RotationAppliedDegrees, double DeskewAppliedDegrees, int Dpi);

public sealed record OcrRunView(
    int RunId, int SourceDocumentId, string EngineName, string EngineVersion, string ModelIdentifiers, string PreprocessingVersion,
    string? TemplateId, bool TemplateIdentified, DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc,
    OcrRunOutcome Outcome, OcrFailureCategory FailureCategory, int PagesProcessed, IReadOnlyList<ExtractedFieldRow> Fields,
    IReadOnlyList<OcrRunPageRow> Pages)
{
    public int FieldsRequiringVerification => Fields.Count(f => f.RequiresVerification);
    public int FieldsVerified => Fields.Count(f => f.Current is not null);
    public int MandatoryOutstanding => Fields.Count(f => f.RequiresVerification && f.Current is null);
    public bool VerificationComplete => MandatoryOutstanding == 0;

    /// <summary>The same field read on more than one page with different values (a document number that differs between front and back). Shown first; never resolved by software.</summary>
    public IReadOnlyList<(string FieldKey, IReadOnlyList<ExtractedFieldRow> Values)> Conflicts
        => Fields.Where(f => f.RawText.Length > 0)
            .GroupBy(f => f.FieldKey, StringComparer.Ordinal)
            .Where(g => g.Select(f => (f.NormalizedCandidate ?? f.RawText).ToUpperInvariant().Replace(" ", string.Empty)).Distinct().Count() > 1)
            .Select(g => (g.Key, (IReadOnlyList<ExtractedFieldRow>)g.OrderBy(f => f.PageNumber).ToList()))
            .ToList();
}

public sealed record OcrStatusView(int SourceDocumentId, int EvidenceRoomId, IReadOnlyList<OcrJobRow> Jobs, OcrRunView? LatestRun)
{
    public bool HasOpenJob => Jobs.Any(j => j.Status is OcrJobStatus.Queued or OcrJobStatus.Running);
}

public sealed record VerifyFieldRequest(int FieldId, FieldVerificationDecision Decision, string? EnteredValue, string? Note);

/// <summary>
/// The web side of OCR: request a job, see status and the latest run, verify fields. The engine
/// is never touched here; the worker does that. Authorization is on the document's owning room.
/// </summary>
public interface IOcrJobService
{
    Task<OperationResult<int>> RequestAsync(int sourceDocumentId, CancellationToken ct = default);

    /// <summary>Null when the document is absent or unauthorized (indistinguishable, as for the document itself).</summary>
    Task<OcrStatusView?> GetStatusAsync(int sourceDocumentId, CancellationToken ct = default);

    Task<OperationResult<int>> VerifyFieldAsync(VerifyFieldRequest request, CancellationToken ct = default);

    /// <summary>The image the engine read for one page of a run. Authorizes on the owning room BEFORE any bytes are read; null when absent or unauthorized.</summary>
    Task<Stream?> OpenRunPageImageAsync(int runId, int pageNumber, CancellationToken ct = default);
}

public sealed class OcrJobService : IOcrJobService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IClock _clock;
    private readonly Emc.Application.Documents.ISourceDocumentStore _store;

    public OcrJobService(IEmcDbContext db, IEvidenceAuthorizationService authorization, ICurrentUser currentUser, IAuditRecorder audit, IClock clock, Emc.Application.Documents.ISourceDocumentStore store)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _clock = clock;
        _store = store;
    }

    public async Task<Stream?> OpenRunPageImageAsync(int runId, int pageNumber, CancellationToken ct = default)
    {
        var roomId = await _db.OcrRuns.AsNoTracking().Where(r => r.Id == runId)
            .Join(_db.SourceDocuments.AsNoTracking(), r => r.SourceDocumentId, d => d.Id, (r, d) => (int?)d.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);
        if (roomId is null || !(await _authorization.AuthorizeAsync(EmcPermissions.ViewSourceDocument, roomId, ct)).IsAllowed)
        {
            return null;
        }

        var key = await _db.OcrRuns.AsNoTracking().Where(r => r.Id == runId).SelectMany(r => r.Pages)
            .Where(p => p.PageNumber == pageNumber).Select(p => p.StorageKey).FirstOrDefaultAsync(ct);
        return key is null ? null : await _store.OpenReadAsync(key, ct);
    }

    public async Task<OperationResult<int>> RequestAsync(int sourceDocumentId, CancellationToken ct = default)
    {
        var document = await AuthorizedAsync(sourceDocumentId, EmcPermissions.RequestOcr, ct);
        if (document is null)
        {
            return OperationResult<int>.Failure("The document was not found.", "OCR-010");
        }

        if (document.ImportStatus != SourceDocumentImportStatus.Rendered)
        {
            return OperationResult<int>.Failure("The document has no rendered pages; OCR reads the rendered pages.", "OCR-010");
        }

        var open = await _db.OcrJobs.AnyAsync(j => j.SourceDocumentId == sourceDocumentId && (j.Status == OcrJobStatus.Queued || j.Status == OcrJobStatus.Running), ct);
        if (open)
        {
            return OperationResult<int>.Failure("OCR is already queued or running for this document.", "OCR-010");
        }

        var job = new OcrJob(sourceDocumentId, document.EvidenceRoomId, _currentUser.UserId, _clock.UtcNow);
        _db.OcrJobs.Add(job);
        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(OcrJob), null,
            newValue: $"OCR requested for source document {sourceDocumentId} in room {document.EvidenceRoomId}", reason: "OCR-010");
        await _db.SaveChangesAsync(ct);
        return OperationResult<int>.Success(job.Id,
            "OCR output is a proposal for a person to verify. The physical original DA Form 4137 remains authoritative (AR 195-5 2-5c).");
    }

    public async Task<OcrStatusView?> GetStatusAsync(int sourceDocumentId, CancellationToken ct = default)
    {
        var document = await AuthorizedAsync(sourceDocumentId, EmcPermissions.ViewSourceDocument, ct);
        if (document is null)
        {
            return null;
        }

        var jobs = await _db.OcrJobs.AsNoTracking().Where(j => j.SourceDocumentId == sourceDocumentId)
            .OrderByDescending(j => j.RequestedAtUtc).ThenByDescending(j => j.Id)
            .Select(j => new OcrJobRow(j.Id, j.Status, j.Attempts, j.RequestedAtUtc, j.FinishedAtUtc, j.LastFailureCategory, j.LeasedByWorkerId))
            .ToListAsync(ct);

        var run = await _db.OcrRuns.AsNoTracking()
            .Include(r => r.Fields).ThenInclude(f => f.Verifications)
            .Include(r => r.Pages)
            .Where(r => r.SourceDocumentId == sourceDocumentId)
            .OrderByDescending(r => r.CompletedAtUtc).ThenByDescending(r => r.Id)
            .FirstOrDefaultAsync(ct);

        OcrRunView? view = null;
        if (run is not null)
        {
            var userIds = run.Fields.SelectMany(f => f.Verifications).Select(v => v.VerifiedByUserId).Distinct().ToList();
            var names = await _db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.PrintedNameAndGrade, ct);
            view = ToView(run, names);
        }

        return new OcrStatusView(document.Id, document.EvidenceRoomId, jobs, view);
    }

    public async Task<OperationResult<int>> VerifyFieldAsync(VerifyFieldRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Room first, from the field's document, before the field's content is loaded.
        var roomId = await _db.OcrRuns.AsNoTracking()
            .Where(r => r.Fields.Any(f => f.Id == request.FieldId))
            .Join(_db.SourceDocuments.AsNoTracking(), r => r.SourceDocumentId, d => d.Id, (r, d) => (int?)d.EvidenceRoomId)
            .FirstOrDefaultAsync(ct);
        if (roomId is null)
        {
            return OperationResult<int>.Failure("The field was not found.", "OCR-014");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.VerifyOcr, roomId, ct);
        if (!decision.IsAllowed)
        {
            return OperationResult<int>.Failure("The field was not found.", "OCR-014");
        }

        var field = await _db.OcrRuns.SelectMany(r => r.Fields).Include(f => f.Verifications).FirstAsync(f => f.Id == request.FieldId, ct);
        try
        {
            var verification = field.RecordVerification(_currentUser.UserId, _clock.UtcNow, request.Decision, request.EnteredValue, request.Note);
            await _db.SaveChangesAsync(ct);
            return OperationResult<int>.Success(verification.Id);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }
    }

    private async Task<SourceDocument?> AuthorizedAsync(int documentId, string permission, CancellationToken ct)
    {
        var roomId = await _db.SourceDocuments.AsNoTracking().Where(d => d.Id == documentId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);
        if (roomId is null)
        {
            return null;
        }

        var decision = await _authorization.AuthorizeAsync(permission, roomId, ct);
        if (!decision.IsAllowed)
        {
            return null;
        }

        return await _db.SourceDocuments.AsNoTracking().FirstAsync(d => d.Id == documentId, ct);
    }

    internal static OcrRunView ToView(OcrRun run, IReadOnlyDictionary<int, string> names)
    {
        var fields = run.Fields.OrderBy(f => f.PageNumber).ThenBy(f => f.Top).ThenBy(f => f.Left).ThenBy(f => f.Id).Select(f =>
        {
            var history = f.Verifications.OrderBy(v => v.VerifiedAtUtc).ThenBy(v => v.Id)
                .Select(v => new FieldVerificationRow(v.Id, v.VerifiedByUserId, names.TryGetValue(v.VerifiedByUserId, out var n) ? n : "(unknown user)", v.VerifiedAtUtc, v.Decision, v.EnteredValue, v.Note))
                .ToList();
            return new ExtractedFieldRow(f.Id, f.FieldKey, f.PageNumber, f.RawText, f.NormalizedCandidate, f.Confidence, f.Band,
                f.Left, f.Top, f.Width, f.Height, f.IsHighConsequence, f.RequiresVerification,
                history.Count == 0 ? null : history[^1], f.VerifiedValue, history);
        }).ToList();

        var pages = run.Pages.OrderBy(p => p.PageNumber).Select(p => new OcrRunPageRow(p.PageNumber, p.WidthPx, p.HeightPx, p.RotationAppliedDegrees, p.DeskewAppliedDegrees, p.Dpi)).ToList();
        return new OcrRunView(run.Id, run.SourceDocumentId, run.EngineName, run.EngineVersion, run.ModelIdentifiers, run.PreprocessingVersion,
            run.TemplateId, run.TemplateIdentified, run.StartedAtUtc, run.CompletedAtUtc, run.Outcome, run.FailureCategory, run.PagesProcessed, fields, pages);
    }
}
