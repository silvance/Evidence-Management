using Emc.Domain.Common;
using Emc.Domain.Identity;

namespace Emc.Domain.Events;

/// <summary>
/// The record that the responsible CI supervisor was informed of an incorrect entry.
///
/// AR 195-5 1-7c(3): a primary or alternate custodian who finds an incorrect entry "will
/// immediately inform the responsible ... CI supervisor". The regulation identifies a PERSON
/// and a MOMENT. It does not presume that person holds an account in an evidence-management
/// application - and in practice the responsible supervisor frequently does not: EMC is used by
/// the agents and custodians of an evidence room, while the supervisor informed may sit in the
/// operations section, the battalion, or a higher headquarters.
///
/// So the notification names the supervisor the way a DA Form 4137 or an MFR does - printed
/// name, grade or title, organization - and links an EMC user only when there is one. When a
/// user IS linked, the printed particulars are read from the user record by the server
/// (<see cref="OfUser"/>), not accepted from the caller, so the two cannot disagree.
///
/// Immutable: this is part of an accountability record.
/// </summary>
public sealed class SupervisorNotification
{
    private SupervisorNotification(
        int? userId,
        string printedName,
        string? gradeOrTitle,
        string? organization,
        DateTimeOffset notifiedAtUtc)
    {
        UserId = userId;
        PrintedName = printedName;
        GradeOrTitle = gradeOrTitle;
        Organization = organization;
        NotifiedAtUtc = AccountabilityTime.Normalize(notifiedAtUtc);
    }

    /// <summary>The supervisor's EMC user, when they have one. Optional by design.</summary>
    public int? UserId { get; }

    /// <summary>Printed name, as it would appear on the MFR. Always present.</summary>
    public string PrintedName { get; }

    public string? GradeOrTitle { get; }

    public string? Organization { get; }

    /// <summary>When the supervisor was informed. 1-7c(3) says immediately; this records when.</summary>
    public DateTimeOffset NotifiedAtUtc { get; }

    /// <summary>Name and grade in the form AR 195-5 3-1b(2) prescribes for printed particulars.</summary>
    public string PrintedNameAndGrade
        => string.IsNullOrWhiteSpace(GradeOrTitle) ? PrintedName : $"{GradeOrTitle} {PrintedName}";

    /// <summary>
    /// A supervisor who holds an EMC account. The particulars come from the user record, not
    /// from the caller (AUD-018).
    /// </summary>
    public static SupervisorNotification OfUser(User user, DateTimeOffset notifiedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(user);

        return new SupervisorNotification(
            user.Id, user.DisplayName, user.RankOrGrade, user.OrganizationOrUnit, notifiedAtUtc);
    }

    /// <summary>A supervisor with no EMC account, identified as an MFR would identify them.</summary>
    public static SupervisorNotification OfPerson(
        string printedName,
        string? gradeOrTitle,
        string? organization,
        DateTimeOffset notifiedAtUtc)
        => new(
            null,
            Guard.NotBlank(printedName, "AUD-018", "Supervisor's printed name"),
            Guard.TrimToNull(gradeOrTitle),
            Guard.TrimToNull(organization),
            notifiedAtUtc);

    /// <summary>Rehydrates from stored columns. Not for use outside persistence and the event itself.</summary>
    internal static SupervisorNotification? FromStored(
        int? userId,
        string? printedName,
        string? gradeOrTitle,
        string? organization,
        DateTimeOffset? notifiedAtUtc)
        => printedName is null || notifiedAtUtc is null
            ? null
            : new SupervisorNotification(userId, printedName, gradeOrTitle, organization, notifiedAtUtc.Value);
}
