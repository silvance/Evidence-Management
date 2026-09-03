using Emc.Domain.Cases;
using Emc.Domain.Identity;

namespace Emc.Domain.Tests;

internal static class TestData
{
    public static readonly DateTimeOffset Now = new(2026, 9, 3, 9, 15, 0, TimeSpan.Zero);

    public static User NewUser(string name = "SMITH, JOHN A.", string grade = "SA")
    {
        var user = new User($"S-1-5-21-{Guid.NewGuid():N}", $"{name}@army.mil", name);
        user.UpdateProfile(name, grade, "902d MI Group");
        return user;
    }

    public static EvidenceVoucher NewDraftVoucher(int caseId = 1, int evidenceRoomId = 1)
        => new(
            caseId: caseId,
            evidenceRoomId: evidenceRoomId,
            temporaryIdentifier: TemporaryEvidenceIdentifier.Create(new DateOnly(2026, 9, 3), 14),
            preparedByUserId: 1,
            receivingActivity: "902d MI Group Evidence Room",
            receivingActivityLocation: "Fort Meade, MD",
            receivedFrom: "SUBJECT residence, 123 Elm Street",
            acquiredAtUtc: Now,
            acquiredAtLocal: Now,
            createdByUserId: 1,
            createdAtUtc: Now);

    public static EvidenceItem AddSimpleItem(this EvidenceVoucher voucher, string? description = null)
        => voucher.AddItem(
            description: description ?? "One Samsung SM-S921U cellular telephone, black",
            quantity: "1",
            serialNumber: "R58N30XXXXX",
            uniqueDeviceIdentifier: "356938035643809",
            isPossibleBiohazard: false,
            isFungible: false,
            isSealed: false,
            sealDescription: null);
}
