using Emc.Application.Abstractions;
using Emc.Domain.Common;
using Emc.Domain.Identity;
using Microsoft.EntityFrameworkCore;

namespace Emc.Application.Authorization;

public sealed record AuthorizationDecision(
    bool IsAllowed,
    string? Reason = null,
    string? RequirementId = null,
    IReadOnlyList<string>? Warnings = null)
{
    public static AuthorizationDecision Allow(params string[] warnings)
        => new(true, Warnings: warnings);

    public static AuthorizationDecision Deny(string reason, string requirementId)
        => new(false, reason, requirementId);
}

public interface IEvidenceAuthorizationService
{
    Task<AuthorizationDecision> AuthorizeAsync(
        string permission, int? evidenceRoomId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Authorization for accountability operations.
///
/// Two checks, in order:
///
///   1. Does one of the user's DATABASE-RESOLVED roles hold the permission? Client-submitted role
///      information is never consulted (IAM-002).
///
///   2. For a permission in <see cref="EmcPermissions.RequireActiveCustodianAppointment"/>, does
///      the user hold an ACTIVE CustodianAppointment for that evidence room right now?
///
/// The second check is the one that matters regulatorily. AR 195-5 does not vest custodial
/// authority in a role; it vests it in a person named in a written appointment (1-4g(1), 1-7b),
/// and the alternate holds it only during the primary's temporary absence — "more than 1 working
/// day and not more than 30 consecutive days" (1-4i). A role flag cannot express that.
/// </summary>
public sealed class EvidenceAuthorizationService : IEvidenceAuthorizationService
{
    /// <summary>
    /// How long before the AR 195-5 1-4i limit an advance advisory appears. LOCAL/DESIGN: the
    /// regulation states the 30-day limit but no warning point.
    /// </summary>
    private static readonly TimeSpan LocalTemporaryAbsenceAdvisoryThreshold = TimeSpan.FromDays(5);

    private readonly IEmcDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IClock _clock;

    public EvidenceAuthorizationService(IEmcDbContext db, ICurrentUser currentUser, IClock clock)
    {
        _db = db;
        _currentUser = currentUser;
        _clock = clock;
    }

    public async Task<AuthorizationDecision> AuthorizeAsync(
        string permission, int? evidenceRoomId, CancellationToken cancellationToken = default)
    {
        if (!_currentUser.IsAuthenticated)
        {
            return AuthorizationDecision.Deny("Not authenticated.", "IAM-002");
        }

        // IAM-016 / IAM-017. Every permission except the global administrative ones is scoped to
        // an evidence room. Refusing when no room is named is what stops an unscoped query from
        // silently reading across rooms.
        if (evidenceRoomId is null && !EmcPermissions.GlobalPermissions.Contains(permission))
        {
            return AuthorizationDecision.Deny(
                "This action is scoped to an evidence room, and no evidence room was identified.",
                "IAM-016");
        }

        // Only grants that apply to THIS evidence room are considered. A grant in another room
        // confers nothing here (IAM-016).
        var roles = _currentUser.RolesFor(evidenceRoomId);

        if (!RolePermissionMap.AnyRoleHasPermission(roles, permission))
        {
            // IAM-009. Called out specifically because "the administrator can do everything" is
            // the assumption this system must not satisfy, and a generic denial message would
            // leave a maintainer wondering whether it was a bug.
            if (roles.Contains(EmcRoles.ApplicationAdministrator)
                && EmcPermissions.AccountabilityPermissions.Contains(permission))
            {
                return AuthorizationDecision.Deny(
                    "The Application Administrator role administers the application. It carries no "
                    + "authority over evidence accountability, and no ability to read evidence "
                    + "content. Performing this action requires an operational role in this "
                    + "evidence room and, for evidence-room actions, a current written custodian "
                    + "appointment under AR 195-5 para 1-4g(1).",
                    "IAM-009");
            }

            return AuthorizationDecision.Deny(
                _currentUser.Grants.Count == 0
                    ? "Your account is not registered in this application, or holds no active role "
                      + "assignments, so no application data is available to you."
                    : "Your role assignments for this evidence room do not include this permission.",
                "IAM-017");
        }

        if (!EmcPermissions.RequireActiveCustodianAppointment.Contains(permission))
        {
            return AuthorizationDecision.Allow();
        }

        if (evidenceRoomId is null)
        {
            return AuthorizationDecision.Deny(
                "An evidence room must be identified for this action.", "IAM-005");
        }

        var now = _clock.UtcNow;

        var appointments = await _db.CustodianAppointments
            .AsNoTracking()
            .Where(a => a.UserId == _currentUser.UserId
                        && a.EvidenceRoomId == evidenceRoomId.Value
                        && a.SupersededByAppointmentId == null
                        && a.EffectiveFrom <= now
                        && (a.EffectiveTo == null || a.EffectiveTo > now))
            .ToListAsync(cancellationToken);

        var active = appointments.Where(a => a.IsActiveAt(now)).ToList();

        if (active.Count == 0)
        {
            // IAM-005, invariant I-11.
            return AuthorizationDecision.Deny(
                "AR 195-5 para 1-4g(1) requires evidence custodians to be appointed in writing. "
                + "You do not hold a current appointment for this evidence room, so you cannot "
                + "perform evidence-room actions here.",
                "IAM-005");
        }

        // A current PRIMARY appointment authorizes custodian actions outright.
        if (active.Any(a => a.AppointmentType == CustodianAppointmentType.Primary))
        {
            return AuthorizationDecision.Allow();
        }

        // IAM-006 / IAM-019. Holding the ALTERNATE appointment is not itself authority to act as
        // the evidence custodian. AR 195-5 para 1-4i: the alternate "will assume the duties and
        // responsibilities of the primary evidence custodian DURING HIS OR HER TEMPORARY
        // ABSENCE". An alternate may hold that appointment for months without the primary ever
        // being absent, so an open duty-assumption period is required.
        var alternateAppointmentIds = active
            .Where(a => a.AppointmentType == CustodianAppointmentType.Alternate)
            .Select(a => a.Id)
            .ToList();

        var assumptions = await _db.CustodianDutyAssumptions
            .AsNoTracking()
            .Where(d => d.EvidenceRoomId == evidenceRoomId.Value
                        && d.AlternateUserId == _currentUser.UserId
                        && alternateAppointmentIds.Contains(d.AlternateAppointmentId)
                        && d.AlternateAssumedDutiesAt <= now
                        && d.PrimaryResumedAt == null)
            .ToListAsync(cancellationToken);

        var assumption = assumptions.FirstOrDefault(d => d.IsActiveAt(now));

        if (assumption is null)
        {
            return AuthorizationDecision.Deny(
                "AR 195-5 para 1-4i: the alternate evidence custodian assumes the primary "
                + "custodian's duties during the primary's temporary absence. No such period is "
                + "open for you in this evidence room, so you are not currently acting as the "
                + "evidence custodian. Record the assumption of duties - para 1-7c(1) also "
                + "requires the prescribed statement to be entered and signed in the evidence "
                + "ledger - before performing custodian actions.",
                "IAM-006");
        }

        // IAM-020. AR 195-5 1-4i caps a temporary absence at "not more than 30 consecutive days",
        // measured from when the PRIMARY'S ABSENCE began. Para 3-2d then requires the alternate to
        // be appointed primary ON ORDERS with a joint inventory conducted.
        //
        // Past that point there is no temporary-absence authority left to exercise, so this is a
        // DENIAL, not a warning. An earlier version allowed the alternate to continue indefinitely
        // on a warning, which let the software extend a window the regulation closes.
        //
        // There is deliberately no commander override. A commander already holds the authority the
        // regulation grants - appointing the person primary on orders and conducting the required
        // inventory - and software must not invent a way around that process.
        if (assumption.ExceedsTemporaryAbsenceLimitAt(now))
        {
            var days = (int)assumption.AbsenceDurationAt(now).TotalDays;

            return AuthorizationDecision.Deny(
                $"The primary evidence custodian has been absent for {days} consecutive days. "
                + "AR 195-5 para 1-4i limits a temporary absence to not more than 30 consecutive "
                + "days, so acting-custodian authority under that paragraph has ended. Para 3-2d "
                + "requires the alternate to be appointed primary evidence custodian on orders and "
                + "a joint inventory of all evidence in the evidence room to be conducted.",
                "IAM-020");
        }

        // An advance advisory before the window closes. The threshold is LOCAL/DESIGN - AR 195-5
        // states no warning point - so it is described as a local notice, not a regulatory one.
        var remaining = assumption.RemainingTemporaryAbsenceAt(now);

        if (remaining <= LocalTemporaryAbsenceAdvisoryThreshold)
        {
            return AuthorizationDecision.Allow(
                $"The primary evidence custodian's absence reaches the AR 195-5 para 1-4i limit of "
                + $"30 consecutive days in {Math.Max(0, (int)remaining.TotalDays)} day(s). Para 3-2d "
                + "requires the alternate to be appointed primary on orders, with a joint "
                + "inventory, if the absence will exceed it. This advance notice is a local "
                + "convenience; the regulation states no warning point, only the limit.");
        }

        return AuthorizationDecision.Allow();
    }
}
