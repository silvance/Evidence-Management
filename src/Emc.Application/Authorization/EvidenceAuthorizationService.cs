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

        var roles = _currentUser.Roles;

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
                    + "authority over evidence accountability. Performing this action requires a "
                    + "role and, for evidence-room actions, a current written custodian "
                    + "appointment under AR 195-5 para 1-4g(1).",
                    "IAM-009");
            }

            return AuthorizationDecision.Deny(
                "Your assigned roles do not include this permission.", "IAM-002");
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

        var active = appointments.FirstOrDefault(a => a.IsActiveAt(now));

        if (active is null)
        {
            // IAM-005, invariant I-11.
            return AuthorizationDecision.Deny(
                "AR 195-5 para 1-4g(1) requires evidence custodians to be appointed in writing. "
                + "You do not hold a current appointment for this evidence room, so you cannot "
                + "perform evidence-room actions here.",
                "IAM-005");
        }

        // AR 195-5 1-4i — an alternate acts during a temporary absence of "not more than 30
        // consecutive days"; beyond that, 3-2d requires appointment as primary on orders and a
        // joint inventory. Whether EMC blocks or warns at the boundary is open decision DEC-05.
        // Until that decision is made EMC WARNS and permits the action, so that a late set of
        // orders cannot halt evidence intake, and the warning is visible to the next inspection.
        if (active.ExceedsAlternateWindowAt(now))
        {
            var days = active.ConsecutiveDaysActiveAt(now);

            return AuthorizationDecision.Allow(
                $"This alternate evidence custodian appointment has been active for {days} days. "
                + "AR 195-5 para 1-4i defines a temporary absence as not more than 30 consecutive "
                + "days, and para 3-2d requires that if the primary custodian's absence is known "
                + "to exceed 30 days the alternate be appointed primary on orders and a joint "
                + "inventory conducted. Open decision DEC-05 governs whether this becomes a hard "
                + "block.");
        }

        return AuthorizationDecision.Allow();
    }
}
