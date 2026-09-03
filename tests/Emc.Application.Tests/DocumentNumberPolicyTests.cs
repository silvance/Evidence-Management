using Emc.Application.Cases;
using Emc.Application.Abstractions;
using Emc.Domain.Storage;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests;

/// <summary>
/// Per-room document-number layout, canonical identity, and explicit calendar years, through
/// the intake service. Requirements: VCH-004, VCH-011, VCH-022, VCH-023.
/// </summary>
public class DocumentNumberPolicyTests : IDisposable
{
    private readonly SliceTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private async Task<int> SubmittedVoucherAsync()
    {
        _harness.SignInAsAgent();

        var caseResult = await _harness.Cases.CreateAsync(new CreateCaseRequest(
            $"CASE-{Guid.NewGuid():N}"[..20], "Numbering test", null, _harness.EvidenceRoomId));

        var voucherResult = await _harness.Vouchers.CreateDraftAsync(new CreateVoucherRequest(
            caseResult.Value, "902d MI Group Evidence Room", "Fort Meade, MD",
            "SUBJECT residence", _harness.Clock.UtcNow, false, null));

        await _harness.Vouchers.AddItemAsync(new AddItemRequest(
            voucherResult.Value, "One item", "1", null, null, false, false, false, null));

        await _harness.Vouchers.SubmitForCustodianIntakeAsync(voucherResult.Value);
        _harness.SignInAsCustodian();
        return voucherResult.Value;
    }

    private Task<OperationResult> RecordAsync(int voucherId, string number, int? confirmedYear = null)
        => _harness.Intake.RecordOfficialDocumentNumberAsync(new RecordDocumentNumberRequest(
            voucherId, number, true, _harness.Clock.UtcNow, ConfirmedCalendarYear: confirmedYear));

    [Fact]
    public async Task WithNoPolicyRecordedTheRegulationsLayoutApplies()
    {
        var voucherId = await SubmittedVoucherAsync();

        var local = await RecordAsync(voucherId, "26-01");
        Assert.False(local.Succeeded);
        Assert.Equal("VCH-004", local.RequirementId);
        Assert.Contains("037-26", local.Error!, StringComparison.Ordinal);

        var regulatory = await RecordAsync(voucherId, "001-26");
        Assert.True(regulatory.Succeeded, regulatory.Error);

        var stored = await _harness.Db.DocumentNumberAssignments.AsNoTracking().SingleAsync(a => a.VoucherId == voucherId);
        Assert.Equal("001-26", stored.DocumentNumber);
        Assert.Equal(1, stored.Sequence);
        Assert.Equal(2026, stored.CalendarYear);
        Assert.Null(stored.NumberingPolicyId);
    }

    [Fact]
    public async Task ALocalLayoutIsAcceptedAsWrittenAndStoredCanonically()
    {
        // VCH-023. The room writes "26-01". EMC records exactly that, and knows it is the first
        // form of 2026.
        var policy = _harness.UseNumberingPolicy(
            DocumentNumberLayout.YearThenSequence, 2, NumberingPolicyBasis.LocalAuthorized,
            "902d MI Group Evidence Room SOP 26-1, para 4.");

        var voucherId = await SubmittedVoucherAsync();

        var regulatory = await RecordAsync(voucherId, "001-26");
        Assert.False(regulatory.Succeeded);
        Assert.Equal("VCH-004", regulatory.RequirementId);
        Assert.Contains("26-37", regulatory.Error!, StringComparison.Ordinal);
        Assert.Contains("AR 195-5 para 2-4c prescribes 001-26", regulatory.Error!, StringComparison.Ordinal);

        var local = await RecordAsync(voucherId, "26-01");
        Assert.True(local.Succeeded, local.Error);
        Assert.Empty(local.Warnings);

        var stored = await _harness.Db.DocumentNumberAssignments.AsNoTracking().SingleAsync(a => a.VoucherId == voucherId);
        Assert.Equal("26-01", stored.DocumentNumber);
        Assert.Equal(1, stored.Sequence);
        Assert.Equal(2026, stored.CalendarYear);
        Assert.Equal(policy.Id, stored.NumberingPolicyId);

        var view = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.Equal("26-01", view!.DisplayIdentifier);
        Assert.False(view.DocumentNumberLayoutIsRegulatory);
        Assert.Equal("26-37", view.DocumentNumberExample);
    }

    [Fact]
    public async Task ALegacyLayoutIsAcceptedButFlaggedEveryTime()
    {
        _harness.UseNumberingPolicy(
            DocumentNumberLayout.YearThenSequence, 2, NumberingPolicyBasis.LegacyObserved, null);

        var voucherId = await SubmittedVoucherAsync();

        // Sequence 1, so the VCH-009 gap warning cannot fire and the only warning is the flag.
        var result = await RecordAsync(voucherId, "26-01");

        Assert.True(result.Succeeded, result.Error);
        var warning = Assert.Single(result.Warnings);
        Assert.Contains("awaiting validation", warning, StringComparison.Ordinal);
        Assert.Contains("AR 195-5 para 2-4c", warning, StringComparison.Ordinal);
        Assert.Contains("calendar year 2026", warning, StringComparison.Ordinal);

        var view = await _harness.Reads.GetVoucherAsync(voucherId);
        Assert.True(view!.DocumentNumberLayoutAwaitsValidation);
    }

    [Fact]
    public async Task TheSameNumberUnderTwoLayoutsIsOneNumber_AndIsNeverReused()
    {
        // VCH-011 across a policy change. "001-26" was recorded under the regulation's layout;
        // the room then adopts "26-01". They are the same canonical number, so the second is
        // refused - identity is (room, year, sequence), not text.
        var first = await SubmittedVoucherAsync();
        Assert.True((await RecordAsync(first, "001-26")).Succeeded);

        _harness.Clock.Advance(TimeSpan.FromDays(1));
        _harness.UseNumberingPolicy(
            DocumentNumberLayout.YearThenSequence, 2, NumberingPolicyBasis.LocalAuthorized,
            "SOP 26-1", effectiveFrom: _harness.Clock.UtcNow);

        var second = await SubmittedVoucherAsync();
        var collision = await RecordAsync(second, "26-01");

        Assert.False(collision.Succeeded);
        Assert.Equal("VCH-011", collision.RequirementId);

        var next = await RecordAsync(second, "26-02");
        Assert.True(next.Succeeded, next.Error);
    }

    [Fact]
    public async Task AYearThatDisagreesWithTheDateReceivedMustBeConfirmed()
    {
        // VCH-022. Received in 2026; the custodian enters "003-25". Not guessed.
        var voucherId = await SubmittedVoucherAsync();

        var unconfirmed = await RecordAsync(voucherId, "003-25");
        Assert.False(unconfirmed.Succeeded);
        Assert.Equal("VCH-022", unconfirmed.RequirementId);
        Assert.Contains("confirm", unconfirmed.Error!, StringComparison.OrdinalIgnoreCase);

        var wrongConfirmation = await RecordAsync(voucherId, "003-25", confirmedYear: 2024);
        Assert.False(wrongConfirmation.Succeeded);
        Assert.Equal("VCH-022", wrongConfirmation.RequirementId);

        var confirmed = await RecordAsync(voucherId, "003-25", confirmedYear: 2025);
        Assert.True(confirmed.Succeeded, confirmed.Error);

        var stored = await _harness.Db.DocumentNumberAssignments.AsNoTracking().SingleAsync(a => a.VoucherId == voucherId);
        Assert.Equal(2025, stored.CalendarYear);
        Assert.Equal("003-25", stored.DocumentNumber);
    }

    [Fact]
    public async Task TheStoredCalendarYearIsAFactOfTheRecord_NotOfTheClock()
    {
        // The regression for the moving century. Record a number, then move the clock far into
        // the future: the stored year is unchanged and the number is still what it was.
        var voucherId = await SubmittedVoucherAsync();
        Assert.True((await RecordAsync(voucherId, "007-26")).Succeeded);

        _harness.Clock.Advance(TimeSpan.FromDays(365 * 60));

        var stored = await _harness.Db.DocumentNumberAssignments.AsNoTracking().SingleAsync(a => a.VoucherId == voucherId);
        Assert.Equal(2026, stored.CalendarYear);
        Assert.Equal("007-26", stored.DocumentNumber);
    }
}
