using Emc.Application.Abstractions;
using Emc.Application.Audit;
using Emc.Application.Authorization;
using Emc.Domain.Cases;
using Emc.Domain.Common;
using Emc.Domain.Events;
using Emc.Domain.Reconciliation;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Items;

/// <summary>A custody party named by a person (AR 195-5 2-7b, 2-7e, 3-2g(5)). Resolved to a CustodyParty row by the server; an internal user by id.</summary>
public sealed record CustodyPartyInput(
    CustodyPartyKind Kind,
    string? Name = null,
    int? UserId = null,
    string? TitleOrGrade = null,
    string? OrganizationOrAgency = null,
    bool IdentificationVerified = false,
    string? AccountableMailNumber = null,
    string? Carrier = null);

/// <summary>
/// A change of custody the PAPER shows and the companion record lacks, recorded by a person
/// (REC-010, COC-002). <paramref name="OccurredAtLocal"/> is when the paper says it happened;
/// the record notes when it was entered. <paramref name="ReconciliationFindingId"/> is the
/// "record missing historical event" finding that led here; the scan is the provenance.
/// </summary>
public sealed record RecordHistoricalCustodyEventRequest(
    int ItemId,
    CustodyPartyInput ReleasedBy,
    CustodyPartyInput ReceivedBy,
    string PurposeOfChangeOfCustody,
    DateTimeOffset OccurredAtLocal,
    bool IsScrcni,
    string? Destination,
    string? Agency,
    string? Notes,
    int? SourceDocumentId,
    int? ReconciliationFindingId);

public interface ICustodyEventService
{
    /// <summary>Appends the custody event to the item's chain. Changes no status; creates no release. Returns the event id.</summary>
    Task<OperationResult<int>> RecordHistoricalCustodyEventAsync(RecordHistoricalCustodyEventRequest request, CancellationToken ct = default);
}

/// <summary>
/// The custody workflow's entry for chain-of-custody rows that exist on paper and not in the
/// companion (REC-010). AR 195-5 2-3f: "Any change in custody ... will be recorded in the Change
/// of Custody section of the DA Form 4137". When a verified scan shows such a row and the
/// companion lacks it, the row is not an "incorrect entry" in the accountability record - the
/// paper is right and the companion is incomplete - so no 1-7c(3) supervisor notification or MFR
/// is demanded merely to bring the companion up to the paper; the event is recorded with the
/// scan as its provenance and the finding as its correlation, by an appointed custodian, as an
/// explicit act. It never creates a temporary release and never changes an item's status: a
/// release the paper shows is recorded through the release workflow, which also writes the
/// custody event.
/// </summary>
public sealed class CustodyEventService : ICustodyEventService
{
    private readonly IEmcDbContext _db;
    private readonly IEvidenceAuthorizationService _authorization;
    private readonly ICurrentUser _currentUser;
    private readonly IAuditRecorder _audit;
    private readonly IItemEventRecorder _events;
    private readonly IClock _clock;

    public CustodyEventService(IEmcDbContext db, IEvidenceAuthorizationService authorization, ICurrentUser currentUser, IAuditRecorder audit, IItemEventRecorder events, IClock clock)
    {
        _db = db;
        _authorization = authorization;
        _currentUser = currentUser;
        _audit = audit;
        _events = events;
        _clock = clock;
    }

    public async Task<OperationResult<int>> RecordHistoricalCustodyEventAsync(RecordHistoricalCustodyEventRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.ReleasedBy);
        ArgumentNullException.ThrowIfNull(request.ReceivedBy);

        var item = await _db.EvidenceItems.Include(i => i.Voucher).FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);
        if (item?.Voucher is null)
        {
            return OperationResult<int>.Failure("Item not found.", "ITEM-001");
        }

        var decision = await _authorization.AuthorizeAsync(EmcPermissions.RecordCustodyEvent, item.Voucher.EvidenceRoomId, ct);
        if (!decision.IsAllowed)
        {
            _audit.Record(AuditEventType.PermissionDenied, nameof(CustodyEvent), $"{item.Voucher.DisplayIdentifier}/{item.ItemNumber}", reason: decision.Reason, succeeded: false);
            await _db.SaveChangesAsync(ct);
            return OperationResult<int>.Failure(decision.Reason!, decision.RequirementId);
        }

        if (item.Voucher.ReviewStage == VoucherReviewStage.Draft)
        {
            return OperationResult<int>.Failure("AR 195-5 2-3f: a change of custody is recorded after the first agent acquires the evidence; this voucher is still a draft that has not been submitted.", "COC-002");
        }

        var now = _clock.UtcNow;
        if (AccountabilityTime.Normalize(request.OccurredAtLocal).ToUniversalTime() > now)
        {
            return OperationResult<int>.Failure("A change of custody is recorded as of when it happened, which cannot be in the future.", "COC-003");
        }

        // The scan and the finding, when named, must be this room's and this item's.
        if (request.SourceDocumentId is int docId)
        {
            var docRoom = await _db.SourceDocuments.AsNoTracking().Where(d => d.Id == docId).Select(d => (int?)d.EvidenceRoomId).FirstOrDefaultAsync(ct);
            if (docRoom is null || docRoom != item.Voucher.EvidenceRoomId)
            {
                return OperationResult<int>.Failure("The source document named is not in this evidence room.", "REC-006");
            }
        }

        ReconciliationFinding? finding = null;
        if (request.ReconciliationFindingId is int findingId)
        {
            finding = await _db.ReconciliationFindings.AsNoTracking().FirstOrDefaultAsync(f => f.Id == findingId, ct);
            if (finding is null || finding.VoucherId != item.VoucherId || finding.Kind != ReconciliationDifferenceKind.CustodyRow
                || finding.Decision != ReconciliationDecision.RecordMissingHistoricalEvent)
            {
                return OperationResult<int>.Failure("The reconciliation finding named is not a \"record missing historical event\" decision on a custody row of this voucher.", "REC-010");
            }

            if (request.SourceDocumentId is not null && request.SourceDocumentId != finding.SourceDocumentId)
            {
                return OperationResult<int>.Failure("The finding was decided on a different source document than the one named.", "REC-010");
            }

            var alreadyRecorded = await _db.ItemEvents.OfType<CustodyEvent>().AsNoTracking().AnyAsync(e => e.ReconciliationFindingId == findingId, ct);
            if (alreadyRecorded)
            {
                return OperationResult<int>.Failure("A custody event has already been recorded from this finding.", "REC-010");
            }
        }

        CustodyParty releasedBy, receivedBy;
        try
        {
            releasedBy = await ResolveAsync(request.ReleasedBy, ct);
            receivedBy = await ResolveAsync(request.ReceivedBy, ct);
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        var notes = Guard.TrimToNull(request.Notes);
        if (finding is not null)
        {
            notes = $"{(notes is null ? string.Empty : notes + " ")}Recorded from the paper DA Form 4137 (reconciliation finding {finding.Id}, source document {finding.SourceDocumentId}); the companion record lacked this row.".Trim();
        }

        CustodyEvent custody;
        try
        {
            custody = new CustodyEvent(releasedBy, receivedBy, request.PurposeOfChangeOfCustody, request.OccurredAtLocal, now, _currentUser.UserId,
                request.IsScrcni, request.Destination, request.Agency, notes);
            if ((request.SourceDocumentId ?? finding?.SourceDocumentId) is int provenance)
            {
                custody.AttachSourceDocument(provenance);
            }

            _db.CustodyParties.Add(releasedBy);
            _db.CustodyParties.Add(receivedBy);
            await _events.AppendAsync(item, custody, ct);
            if (finding is not null)
            {
                custody.LinkReconciliationFinding(finding.Id);
            }
        }
        catch (DomainRuleViolationException ex)
        {
            return OperationResult<int>.Failure(ex.Message, ex.RequirementId);
        }

        _audit.Record(AuditEventType.AccountabilityActionRecorded, nameof(CustodyEvent), $"{item.Voucher.DisplayIdentifier}/{item.ItemNumber}",
            newValue: $"historical custody event as of {custody.OccurredAtUtc:u}; from finding {finding?.Id.ToString() ?? "none"}", reason: "REC-010 / COC-002");
        await _db.SaveChangesAsync(ct);

        var warnings = new List<string>
        {
            "The item's status was not changed and no temporary release was created: this records a change of custody the paper shows. A release the paper shows is recorded through the release workflow."
        };
        return OperationResult<int>.Success(custody.Id, [.. warnings]);
    }

    private async Task<CustodyParty> ResolveAsync(CustodyPartyInput input, CancellationToken ct)
    {
        switch (input.Kind)
        {
            case CustodyPartyKind.InternalUser:
            {
                if (input.UserId is not int userId)
                {
                    throw new DomainRuleViolationException("COC-004", "An internal user party names the user.");
                }

                var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
                    ?? throw new DomainRuleViolationException("COC-004", "The user named was not found.");
                return CustodyParty.ForUser(user, input.IdentificationVerified);
            }

            case CustodyPartyKind.ExternalPerson:
                return CustodyParty.ForExternalPerson(input.Name ?? string.Empty, input.TitleOrGrade, input.OrganizationOrAgency, input.IdentificationVerified);
            case CustodyPartyKind.Organization:
                return CustodyParty.ForOrganization(input.Name ?? string.Empty);
            case CustodyPartyKind.AccountableMailNumber:
                return CustodyParty.ForAccountableMailNumber(input.AccountableMailNumber ?? input.Name ?? string.Empty, input.Carrier);
            case CustodyPartyKind.CustodianUnableToSign:
                return CustodyParty.CustodianUnableToSign();
            default:
                throw new DomainRuleViolationException("COC-004", "Unknown custody party kind.");
        }
    }
}
