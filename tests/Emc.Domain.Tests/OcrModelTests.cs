using Emc.Domain.Common;
using Emc.Domain.Ocr;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// OCR data model: bands, high-consequence fields, immutable runs, append-only verification,
/// job leasing. Requirements: OCR-002, OCR-003, OCR-004, OCR-011 .. OCR-014.
/// </summary>
public class OcrModelTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

    private static OcrRun SucceededRun()
        => new(1, 1, 1, "worker-a", "tesseract", "5.3.4", "eng@sha256:00;osd@sha256:00", "prep/1", "test", true, T0, T0.AddSeconds(3), OcrRunOutcome.Succeeded, OcrFailureCategory.None, 1);

    [Theory]
    [InlineData(100, ConfidenceBand.High)]
    [InlineData(90, ConfidenceBand.High)]
    [InlineData(89.99, ConfidenceBand.Medium)]
    [InlineData(60, ConfidenceBand.Medium)]
    [InlineData(59.99, ConfidenceBand.LowOrUnreadable)]
    [InlineData(0, ConfidenceBand.LowOrUnreadable)]
    public void ConfidenceBandsAreThreeAndFixed(double confidence, ConfidenceBand expected)
        => Assert.Equal(expected, ConfidenceBanding.Band((decimal)confidence));

    [Theory]
    [InlineData("Header.DocumentNumber", true)]
    [InlineData("Header.CaseControlNumber", true)]
    [InlineData("Item[1].ItemNumber", true)]
    [InlineData("Item[2].SerialNumber", true)]
    [InlineData("Item[2].UniqueDeviceIdentifier", true)]
    [InlineData("Custody[1].ReleasedByName", true)]
    [InlineData("Custody[1].ReceivedByName", true)]
    [InlineData("Custody[1].Date", true)]
    [InlineData("Header.DateTimeObtained", true)]
    [InlineData("Item[1].CurrencyAmount", true)]
    [InlineData("Disposition.Action", true)]
    [InlineData("Disposition.Anything", true)]
    [InlineData("Item[1].Description", false)]
    [InlineData("Item[1].Quantity", false)]
    [InlineData("Header.ReceivingActivity", false)]
    [InlineData("Custody[1].Purpose", false)]
    [InlineData("Page[1].Line[3]", false)]
    public void HighConsequenceFieldsAreDecidedByName(string key, bool expected)
        => Assert.Equal(expected, OcrFieldCatalog.IsHighConsequence(key));

    [Fact]
    public void AHighConsequenceFieldRequiresVerificationEvenAtFullConfidence()
    {
        // OCR-003. The one rule this whole subsystem exists to keep.
        var run = SucceededRun();
        var serial = run.AddField("Item[1].SerialNumber", 1, "TESTSERIAL000001", "TESTSERIAL000001", 100m, 10, 10, 100, 20);
        var description = run.AddField("Item[1].Description", 1, "One test item", null, 100m, 10, 40, 100, 20);
        var faint = run.AddField("Item[1].Quantity", 1, "1", null, 75m, 10, 70, 20, 20);

        Assert.True(serial.IsHighConsequence);
        Assert.True(serial.RequiresVerification);
        Assert.Equal(ConfidenceBand.High, serial.Band);

        Assert.False(description.IsHighConsequence);
        Assert.False(description.RequiresVerification);

        Assert.False(faint.IsHighConsequence);
        Assert.True(faint.RequiresVerification); // Medium band: flagged
    }

    [Fact]
    public void VerificationNeverRewritesTheRawText_AndIsAppendOnly()
    {
        // OCR-004. Correction keeps the engine's reading; a second look is a second row.
        var run = SucceededRun();
        var field = run.AddField("Header.DocumentNumber", 1, "0OO1-26", "0001-26", 91m, 0, 0, 50, 10);

        Assert.Null(field.VerifiedValue);
        Assert.False(field.IsVerified);

        field.RecordVerification(7, T0.AddMinutes(1), FieldVerificationDecision.CorrectedByVerifier, "0007-26", "The 7 is faint on the scan");
        Assert.Equal("0OO1-26", field.RawText);
        Assert.Equal("0007-26", field.VerifiedValue);
        Assert.Single(field.Verifications);

        field.RecordVerification(8, T0.AddMinutes(2), FieldVerificationDecision.AcceptedAsRead, null, "Second reviewer: the candidate is right");
        Assert.Equal(2, field.Verifications.Count);
        Assert.Equal("0001-26", field.VerifiedValue);
        Assert.Equal(FieldVerificationDecision.AcceptedAsRead, field.CurrentVerification!.Decision);
        Assert.Equal("0OO1-26", field.RawText);
    }

    [Fact]
    public void ALowOrUnreadableFieldCannotBeAcceptedAsRead()
    {
        // OCR-002: no guess is offered in the low band; the value comes from a person.
        var run = SucceededRun();
        var field = run.AddField("Item[1].SerialNumber", 1, "??", null, 12m, 0, 0, 1, 1);

        var ex = Assert.Throws<DomainRuleViolationException>(() => field.RecordVerification(7, T0, FieldVerificationDecision.AcceptedAsRead, null, null));
        Assert.Equal("OCR-014", ex.RequirementId);

        field.RecordVerification(7, T0, FieldVerificationDecision.UnreadableManualEntry, "TESTSERIAL000002", "Read from the physical form");
        Assert.Equal("TESTSERIAL000002", field.VerifiedValue);
    }

    [Fact]
    public void ACorrectionThatMatchesTheReadingIsAnAcceptance_AndAcceptanceTakesNoValue()
    {
        var run = SucceededRun();
        var field = run.AddField("Header.CaseControlNumber", 1, "TEST-CI-2026-0001", null, 95m, 0, 0, 1, 1);

        Assert.Throws<DomainRuleViolationException>(() => field.RecordVerification(7, T0, FieldVerificationDecision.CorrectedByVerifier, "TEST-CI-2026-0001", null));
        Assert.Throws<DomainRuleViolationException>(() => field.RecordVerification(7, T0, FieldVerificationDecision.CorrectedByVerifier, null, null));
        Assert.Throws<DomainRuleViolationException>(() => field.RecordVerification(7, T0, FieldVerificationDecision.AcceptedAsRead, "x", null));
        Assert.Throws<DomainRuleViolationException>(() => field.RecordVerification(7, T0, FieldVerificationDecision.NotApplicable, "x", null));
        Assert.Empty(field.Verifications);
    }

    [Fact]
    public void AFailedRunCarriesACategoryAndNoFields_AndASuccessfulRunCarriesNoCategory()
    {
        var failed = new OcrRun(1, 1, 1, "w", "tesseract", "5", "m", "p", null, false, T0, T0, OcrRunOutcome.Failed, OcrFailureCategory.Timeout, 0);
        Assert.Throws<DomainRuleViolationException>(() => failed.AddField("Page[1].Line[1]", 1, "x", null, 50m, 0, 0, 1, 1));

        Assert.Throws<DomainRuleViolationException>(() => new OcrRun(1, 1, 1, "w", "t", "5", "m", "p", null, false, T0, T0, OcrRunOutcome.Failed, OcrFailureCategory.None, 0));
        Assert.Throws<DomainRuleViolationException>(() => new OcrRun(1, 1, 1, "w", "t", "5", "m", "p", null, false, T0, T0, OcrRunOutcome.Succeeded, OcrFailureCategory.Timeout, 0));
        Assert.Throws<DomainRuleViolationException>(() => new OcrRun(1, 1, 1, "w", "t", "5", "m", "p", null, false, T0, T0.AddSeconds(-1), OcrRunOutcome.Succeeded, OcrFailureCategory.None, 0));
    }

    [Fact]
    public void AFieldKeyFollowsTheGrammar()
    {
        var run = SucceededRun();
        Assert.Throws<DomainRuleViolationException>(() => run.AddField("serial number", 1, "x", null, 50m, 0, 0, 1, 1));
        Assert.Throws<DomainRuleViolationException>(() => run.AddField("Item.SerialNumber.Extra", 1, "x", null, 50m, 0, 0, 1, 1));
        Assert.Throws<DomainRuleViolationException>(() => run.AddField("Item[1].SerialNumber", 0, "x", null, 50m, 0, 0, 1, 1));
        Assert.Throws<DomainRuleViolationException>(() => run.AddField("Item[1].SerialNumber", 1, "x", null, 50m, -1, 0, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => run.AddField("Item[1].SerialNumber", 1, "x", null, 101m, 0, 0, 1, 1));
    }

    [Fact]
    public void AJobIsLeasedOnce_RetriedOnTransientFailure_AndExhausts()
    {
        var job = new OcrJob(1, 1, 1, 5, T0);
        Assert.Equal(OcrJobStatus.Queued, job.Status);

        job.Lease("w1", T0, TimeSpan.FromMinutes(10));
        Assert.Equal(OcrJobStatus.Running, job.Status);
        Assert.Equal(1, job.Attempts);
        Assert.False(job.CanBeLeased(T0.AddMinutes(5)));
        Assert.Throws<DomainRuleViolationException>(() => job.Lease("w2", T0.AddMinutes(5), TimeSpan.FromMinutes(10)));

        // The worker died: the lease expires and another worker may take it.
        Assert.True(job.CanBeLeased(T0.AddMinutes(10)));
        job.Lease("w2", T0.AddMinutes(10), TimeSpan.FromMinutes(10));
        Assert.Equal(2, job.Attempts);

        // Only the lease holder settles it.
        Assert.Throws<DomainRuleViolationException>(() => job.Complete("w1", T0.AddMinutes(11)));

        job.Fail("w2", T0.AddMinutes(11), OcrFailureCategory.Timeout);
        Assert.Equal(OcrJobStatus.Queued, job.Status); // transient, attempts remain

        job.Lease("w2", T0.AddMinutes(12), TimeSpan.FromMinutes(10));
        job.Fail("w2", T0.AddMinutes(13), OcrFailureCategory.Timeout);
        Assert.Equal(OcrJobStatus.Failed, job.Status); // third attempt: final
        Assert.Equal(OcrFailureCategory.Timeout, job.LastFailureCategory);
        Assert.False(job.IsOpen);
    }

    [Fact]
    public void ALeaseIsRenewedOnlyByItsHolder_AndMustBePositive()
    {
        // OCR-011. Renewal pushes the expiry out for the holder alone; a non-holder's renewal is
        // refused; a zero or negative lease is not a lease.
        var job = new OcrJob(1, 1, 1, 5, T0);
        Assert.Throws<DomainRuleViolationException>(() => job.Lease("w1", T0, TimeSpan.Zero));
        job.Lease("w1", T0, TimeSpan.FromMinutes(10));
        var stamp = job.ConcurrencyStamp;

        Assert.Throws<DomainRuleViolationException>(() => job.RenewLease("w2", T0.AddMinutes(1), TimeSpan.FromMinutes(10)));
        Assert.Throws<DomainRuleViolationException>(() => job.RenewLease("w1", T0.AddMinutes(1), TimeSpan.FromSeconds(-1)));

        job.RenewLease("w1", T0.AddMinutes(9), TimeSpan.FromMinutes(10));
        Assert.Equal(T0.AddMinutes(19), job.LeaseExpiresUtc);
        Assert.NotEqual(stamp, job.ConcurrencyStamp);
        Assert.False(job.CanBeLeased(T0.AddMinutes(12)));

        // The same rules on a render job (DOC-014).
        var render = new Emc.Domain.Documents.DocumentRenderJob(1, 1, 5, T0);
        render.Lease("w1", T0, TimeSpan.FromMinutes(10));
        Assert.Throws<DomainRuleViolationException>(() => render.RenewLease("w2", T0.AddMinutes(1), TimeSpan.FromMinutes(10)));
        render.RenewLease("w1", T0.AddMinutes(9), TimeSpan.FromMinutes(10));
        Assert.Equal(T0.AddMinutes(19), render.LeaseExpiresUtc);
    }

    [Fact]
    public void ANonTransientFailureIsFinalOnTheFirstAttempt()
    {
        var job = new OcrJob(1, 1, 1, 5, T0);
        job.Lease("w1", T0, TimeSpan.FromMinutes(10));
        job.Fail("w1", T0.AddSeconds(1), OcrFailureCategory.ModelMissing);
        Assert.Equal(OcrJobStatus.Failed, job.Status);
        Assert.Equal(1, job.Attempts);
    }

    [Fact]
    public void RunFieldAndVerificationAreAppendOnlyRecords_TheJobIsNot()
    {
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(OcrRun)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(ExtractedField)));
        Assert.True(typeof(IAppendOnly).IsAssignableFrom(typeof(FieldVerification)));
        Assert.False(typeof(IAppendOnly).IsAssignableFrom(typeof(OcrJob)));
        Assert.True(typeof(IConcurrencyStamped).IsAssignableFrom(typeof(OcrJob)));
    }

    [Fact]
    public void AFailureCategoryIsAnEnum_NotText()
    {
        // Phase 10: nothing an engine says about an image reaches a record or a log.
        Assert.All(typeof(OcrRun).GetProperties(), p => Assert.DoesNotContain("Message", p.Name, StringComparison.Ordinal));
        Assert.All(typeof(OcrJob).GetProperties(), p => Assert.DoesNotContain("Message", p.Name, StringComparison.Ordinal));
        Assert.Equal(typeof(OcrFailureCategory), typeof(OcrRun).GetProperty(nameof(OcrRun.FailureCategory))!.PropertyType);
    }
}
