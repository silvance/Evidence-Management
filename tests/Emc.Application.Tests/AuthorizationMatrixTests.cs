using Emc.Application.Authorization;
using Emc.Domain.Common;
using Emc.Domain.Identity;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// The authorization review, as a matrix: every kind of principal the system can see, against
/// the permissions that matter most, in evidence room A. One table, so a change to any role,
/// permission set or appointment rule shows up as a diff here rather than as a surprise.
///
/// Principals:
///   1. unauthenticated
///   2. a Windows-authenticated principal with no EMC record (IAM-017)
///   3. the Application Administrator, global (IAM-009)
///   4. an Agent in room A
///   5. an Agent in room B only (IAM-016)
///   6. the appointed primary custodian (IAM-005)
///   7. an alternate custodian by ROLE only - appointed, no open assumption (IAM-006)
///   8. the alternate with an open assumption of duties (IAM-006)
///   9. the commander / SAC
///  10. an inspection or inventory participant
///
/// Requirements: IAM-002, IAM-005, IAM-006, IAM-009, IAM-011, IAM-016, IAM-017.
/// </summary>
public class AuthorizationMatrixTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private static readonly string[] Permissions =
    [
        EmcPermissions.ViewCase,
        EmcPermissions.CreateDraftVoucher,
        EmcPermissions.SubmitVoucherForIntake,
        EmcPermissions.AcceptEvidenceIntake,
        EmcPermissions.RecordOfficialDocumentNumber,
        EmcPermissions.ReturnVoucherForCorrection,
        EmcPermissions.RecordCorrection,
        EmcPermissions.ConductInspection,
        EmcPermissions.ParticipateInInventory,
        EmcPermissions.VerifyIntegrity,
        EmcPermissions.ManageUsers
    ];

    // Expected, in the order of Permissions above. Y allowed, . denied.
    private static readonly Dictionary<string, string> Expected = new(StringComparer.Ordinal)
    {
        ["1 unauthenticated"]              = "...........",
        ["2 unregistered principal"]       = "...........",
        ["3 administrator"]                = ".........YY",
        ["4 agent, room A"]                = "YYY........",
        ["5 agent, room B only"]           = "...........",
        // Custodians take part in the 3-1b(2) joint inventory; VerifyIntegrity is administrative
        // and is held by the administrator alone. An appointed alternate who has not assumed
        // duties may still take part in an inventory - that is not a custodian act.
        ["6 primary custodian, appointed"] = "YYYYYYY.Y..",
        ["7 alternate, role only"]         = "YYY.....Y..",
        ["8 alternate, duties assumed"]    = "YYYYYYY.Y..",
        ["9 commander / SAC"]              = "Y......YY..",
        ["10 inspector / inventory"]       = "Y.......Y.."
    };

    private async Task<string> RowAsync()
    {
        var chars = new char[Permissions.Length];
        for (var i = 0; i < Permissions.Length; i++)
        {
            var decision = await _harness.Authorization.AuthorizeAsync(Permissions[i], _harness.EvidenceRoomId);
            chars[i] = decision.IsAllowed ? 'Y' : '.';
        }

        return new string(chars);
    }

    private void SignInAs(string principal)
    {
        switch (principal)
        {
            case "1 unauthenticated":
                _harness.CurrentUser.SignOut();
                break;
            case "2 unregistered principal":
                _harness.CurrentUser.SignInAsUnregisteredWindowsPrincipal();
                break;
            case "3 administrator":
                _harness.SignInAsAdministrator();
                break;
            case "4 agent, room A":
                _harness.SignInAsAgent();
                break;
            case "5 agent, room B only":
            {
                var outsider = new User("S-1-5-21-MATRIX-OUTSIDER", "outsider.matrix@army.mil", "FOX, JAMIE R.");
                outsider.UpdateProfile("FOX, JAMIE R.", "SA", "310th MI Bn");
                _harness.Db.Users.Add(outsider);
                _harness.Db.SaveChanges();
                _harness.GrantRoleInRoom(outsider.Id, EmcRoles.Agent, _harness.OtherEvidenceRoomId);
                _harness.Db.SaveChanges();
                _harness.CurrentUser.SignIn(outsider.Id, "SA FOX, JAMIE R.", _harness.OtherEvidenceRoomId, EmcRoles.Agent);
                break;
            }
            case "6 primary custodian, appointed":
                _harness.SignInAsCustodian();
                break;
            case "7 alternate, role only":
                _harness.AppointAlternate(_harness.AlternateCustodianUserId);
                _harness.SignInAsUnappointedCustodian();
                break;
            case "8 alternate, duties assumed":
            {
                var appointment = _harness.AppointAlternate(_harness.AlternateCustodianUserId);
                _harness.AssumeDuties(appointment, assumedAt: _harness.Clock.UtcNow.AddDays(-2), absenceStart: _harness.Clock.UtcNow.AddDays(-2));
                _harness.SignInAsUnappointedCustodian();
                break;
            }
            case "9 commander / SAC":
                _harness.SignInAsCommander();
                break;
            case "10 inspector / inventory":
            {
                var inspector = new User("S-1-5-21-MATRIX-INSPECTOR", "inspector.matrix@army.mil", "NGUYEN, LINH T.");
                inspector.UpdateProfile("NGUYEN, LINH T.", "CW2", "902d MI Group");
                _harness.Db.Users.Add(inspector);
                _harness.Db.SaveChanges();
                _harness.GrantRoleInRoom(inspector.Id, EmcRoles.InspectorOrInventoryParticipant, _harness.EvidenceRoomId);
                _harness.Db.SaveChanges();
                _harness.CurrentUser.SignIn(inspector.Id, "CW2 NGUYEN, LINH T.", _harness.EvidenceRoomId, EmcRoles.InspectorOrInventoryParticipant);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(principal), principal, null);
        }
    }

    public static IEnumerable<object[]> Principals()
        => Expected.Keys.Select(k => new object[] { k });

    [Theory]
    [MemberData(nameof(Principals))]
    public async Task EachPrincipalGetsExactlyTheExpectedDecisions(string principal)
    {
        SignInAs(principal);

        var actual = await RowAsync();

        Assert.True(
            Expected[principal] == actual,
            $"{principal}: expected {Expected[principal]} but got {actual}\n"
            + $"columns: {string.Join(" ", Permissions)}");
    }

    [Fact]
    public async Task TheAgentInRoomBIsAllowedInRoomB()
    {
        // The complement of row 5: the denial in room A is scoping, not a broken account.
        SignInAs("5 agent, room B only");

        var view = await _harness.Authorization.AuthorizeAsync(EmcPermissions.ViewCase, _harness.OtherEvidenceRoomId);
        var create = await _harness.Authorization.AuthorizeAsync(EmcPermissions.CreateDraftVoucher, _harness.OtherEvidenceRoomId);

        Assert.True(view.IsAllowed, view.Reason);
        Assert.True(create.IsAllowed, create.Reason);
    }

    [Fact]
    public void RecordingACorrectionRequiresAnActiveCustodianAppointment()
    {
        // The gap the matrix found. AR 195-5 1-7c(3) makes correcting an accepted entry the
        // custodian's act; an alternate holding the role with no open assumption of duties must
        // not be able to do it while being denied every other custodian action.
        Assert.Contains(EmcPermissions.RecordCorrection, EmcPermissions.RequireActiveCustodianAppointment);
    }
}
