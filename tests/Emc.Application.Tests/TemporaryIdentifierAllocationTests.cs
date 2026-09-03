using Emc.Application.Cases;
using Emc.Domain.Cases;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Temporary-identifier allocation is database-backed and collision-safe. The identifier is not
/// a regulatory number (VCH-003); what matters is that two drafts never share one.
/// Requirements: VCH-003, VCH-024.
/// </summary>
public class TemporaryIdentifierAllocationTests : IDisposable
{
    private static readonly DateOnly Date = new(2026, 9, 3);
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    [Fact]
    public async Task SequentialAllocationsAreGaplessAndDistinct()
    {
        var allocator = new TemporaryIdentifierAllocator(_harness.Db);

        var issued = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            issued.Add((await allocator.AllocateAsync(_harness.EvidenceRoomId, Date)).ToString());
        }

        Assert.Equal(
            ["TMP-20260903-A001", "TMP-20260903-A002", "TMP-20260903-A003", "TMP-20260903-A004", "TMP-20260903-A005"],
            issued);
    }

    [Fact]
    public async Task AStaleContextDoesNotReissueANumberAnotherRequestTook()
    {
        // VCH-024. THE race. Request A reads the counter; request B reads it too and commits
        // first; A's save must fail on the concurrency stamp, reload, and take the NEXT number -
        // not the one B already has. Reproduced with two contexts on one database: after its
        // first allocation context A keeps a tracked copy of the counter, which is stale the
        // moment context B commits.
        var contextA = _harness.Db;
        using var contextB = _harness.CreateSecondContext();

        var allocatorA = new TemporaryIdentifierAllocator(contextA);
        var allocatorB = new TemporaryIdentifierAllocator(contextB);

        var a1 = await allocatorA.AllocateAsync(_harness.EvidenceRoomId, Date);
        var b1 = await allocatorB.AllocateAsync(_harness.EvidenceRoomId, Date);

        // Context A still tracks LastOrdinal = 1 with the stamp from its own save. Without the
        // retry it would compute 2 - B's number - and the unique index would reject the voucher.
        var a2 = await allocatorA.AllocateAsync(_harness.EvidenceRoomId, Date);
        var b2 = await allocatorB.AllocateAsync(_harness.EvidenceRoomId, Date);

        var all = new[] { a1, b1, a2, b2 }.Select(i => i.ToString()).ToList();

        Assert.Equal(4, all.Distinct().Count());
        Assert.Equal("TMP-20260903-A001", a1.ToString());
        Assert.Equal("TMP-20260903-A002", b1.ToString());
        Assert.Equal("TMP-20260903-A003", a2.ToString());
        Assert.Equal("TMP-20260903-A004", b2.ToString());

        var counter = await contextB.TemporaryIdentifierCounters.AsNoTracking()
            .SingleAsync(c => c.EvidenceRoomId == _harness.EvidenceRoomId && c.Date == Date);
        Assert.Equal(4, counter.LastOrdinal);
    }

    [Fact]
    public async Task AllocationIsNotACountOfExistingVouchers()
    {
        // The earlier allocator computed COUNT(vouchers for the date) + 1, which reissued a
        // number whenever a draft had been discarded, and collided under concurrency. Two
        // numbers allocated and never used must still be consumed.
        var allocator = new TemporaryIdentifierAllocator(_harness.Db);
        await allocator.AllocateAsync(_harness.EvidenceRoomId, Date);
        await allocator.AllocateAsync(_harness.EvidenceRoomId, Date);

        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0400-2026-CID902-XXXXX", "Allocator test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        Assert.True(voucherResult.Succeeded, voucherResult.Error);

        var voucher = await _harness.Db.EvidenceVouchers.AsNoTracking().SingleAsync(v => v.Id == voucherResult.Value);
        Assert.Equal("TMP-20260903-A003", voucher.TemporaryIdentifier);
    }

    [Fact]
    public async Task CountersAreScopedToRoomAndDate()
    {
        var allocator = new TemporaryIdentifierAllocator(_harness.Db);

        var roomADay1 = await allocator.AllocateAsync(_harness.EvidenceRoomId, Date);
        var roomBDay1 = await allocator.AllocateAsync(_harness.OtherEvidenceRoomId, Date);
        var roomADay2 = await allocator.AllocateAsync(_harness.EvidenceRoomId, Date.AddDays(1));

        Assert.Equal("TMP-20260903-A001", roomADay1.ToString());
        Assert.Equal("TMP-20260903-A001", roomBDay1.ToString());
        Assert.Equal("TMP-20260904-A001", roomADay2.ToString());
    }

    [Fact]
    public async Task DraftsCreatedThroughTheServiceCarryDistinctIdentifiers()
    {
        _harness.SignInAsAgent();
        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            "0401-2026-CID902-XXXXX", "Allocator test", null, _harness.EvidenceRoomId));

        var ids = new List<string>();
        for (var i = 0; i < 4; i++)
        {
            var result = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
                caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
                "SUBJECT residence", _harness.Clock.UtcNow, false, null));
            Assert.True(result.Succeeded, result.Error);

            ids.Add((await _harness.Db.EvidenceVouchers.AsNoTracking().SingleAsync(v => v.Id == result.Value)).TemporaryIdentifier);
        }

        Assert.Equal(4, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(TemporaryEvidenceIdentifier.TryParse(id, out _)));
    }
}
