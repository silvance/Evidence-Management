using Emc.Domain.Cases;
using Emc.Domain.Common;
using Xunit;

namespace Emc.Domain.Tests;

/// <summary>
/// AR 195-5 paras 2-3d, 2-3c, 2-3l, 2-1b, 2-2f - evidence item rules.
/// Requirements: ITEM-001 .. ITEM-012, VCH-010, VCH-011.
/// </summary>
public class EvidenceItemTests
{
    [Fact]
    public void ItemNumbers_AreContiguousFromOne()
    {
        // Invariant I-01. AR 195-5 2-3d numbers items on the DA Form 4137 and 2-5b(1)(c) records
        // the item number in the ledger, so the numbering must be predictable.
        var voucher = TestData.NewDraftVoucher();

        var first = voucher.AddSimpleItem("Item one");
        var second = voucher.AddSimpleItem("Item two");
        var third = voucher.AddSimpleItem("Item three");

        Assert.Equal(1, first.ItemNumber);
        Assert.Equal(2, second.ItemNumber);
        Assert.Equal(3, third.ItemNumber);
    }

    [Fact]
    public void RemovingAnItem_RenumbersTheRemainder()
    {
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem("Item one");
        var second = voucher.AddSimpleItem("Item two");
        voucher.AddSimpleItem("Item three");

        voucher.RemoveItem(second);

        Assert.Equal([1, 2], voucher.Items.Select(i => i.ItemNumber).OrderBy(n => n));
    }

    [Fact]
    public void PossibleBiohazard_IsAnnotatedInAllCapitals()
    {
        // AR 195-5 2-3l: the Description of Articles section "will reflect POSSIBLE BIOHAZARD in
        // all capital letters after each item containing suspected blood or bodily fluids"
        // (ITEM-007). Derived on render so the annotation can never drift from the flag.
        var voucher = TestData.NewDraftVoucher();

        var item = voucher.AddItem(
            description: "One white cotton t-shirt with reddish-brown staining",
            quantity: "1",
            serialNumber: null,
            uniqueDeviceIdentifier: null,
            isPossibleBiohazard: true,
            isFungible: false,
            isSealed: false,
            sealDescription: null);

        Assert.EndsWith("POSSIBLE BIOHAZARD", item.DescriptionForForm, StringComparison.Ordinal);
    }

    [Fact]
    public void SealedItem_RequiresTheSealingAnnotation()
    {
        // AR 195-5 2-3c: "When evidence is sealed in an evidence container, the Description of
        // Articles section of the DA Form 4137 should be annotated to reflect the sealing"
        // (ITEM-009).
        var voucher = TestData.NewDraftVoucher();

        var ex = Assert.Throws<DomainRuleViolationException>(() => voucher.AddItem(
            description: "Approximately 100 tablets, white, round",
            quantity: "approximately 100",
            serialNumber: null,
            uniqueDeviceIdentifier: null,
            isPossibleBiohazard: false,
            isFungible: true,
            isSealed: true,
            sealDescription: null));

        Assert.Equal("ITEM-009", ex.RequirementId);
        Assert.Contains("2-3c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SuppositionPhrases_AreDetectedButNotBlocked()
    {
        // AR 195-5 2-3d prohibits descriptions "based on supposition or suspicions", giving
        // "suspected to be marijuana" as its own example. EMC warns rather than blocks: a word
        // list cannot reliably tell a prohibited inference from a legitimate description
        // (ITEM-003).
        var voucher = TestData.NewDraftVoucher();
        var item = voucher.AddSimpleItem("A green leafy substance suspected to be marijuana");

        Assert.Contains("suspected", item.DetectSuppositionPhrases());
        Assert.NotNull(item);
    }

    [Fact]
    public void LastItem_IsDerivedFromPosition()
    {
        // AR 195-5 2-3d: "The words LAST ITEM will be placed in capital letters after the last
        // listed item on the next line below that item." Derived, never a stored flag, so adding
        // an item cannot leave a stale marker behind (ITEM-008).
        var voucher = TestData.NewDraftVoucher();
        var first = voucher.AddSimpleItem("Item one");

        Assert.True(first.IsLastItem);

        var second = voucher.AddSimpleItem("Item two");

        Assert.False(first.IsLastItem);
        Assert.True(second.IsLastItem);
    }

    [Fact]
    public void Currency_RecordsTheExactAmountByDenomination()
    {
        // AR 195-5 2-3d: "When funds are seized as evidence or kept for safekeeping, the exact
        // amount, by denomination, will be recorded on the DA Form 4137" (ITEM-006).
        var voucher = TestData.NewDraftVoucher();
        var item = voucher.AddSimpleItem("United States currency");

        item.RecordAsCurrency("12 x $100, 4 x $20, 3 x $5", 1295.00m);

        Assert.True(item.IsCurrency);
        Assert.Equal(1295.00m, item.CurrencyTotalAmount);
        Assert.Throws<DomainRuleViolationException>(() => item.RecordAsCurrency("none", 0m));
    }

    [Fact]
    public void ItemsCannotBeEditedOnceTheVoucherIsSubmitted()
    {
        // VCH-010, invariant I-10. AR 195-5 2-3g has the custodian make the submitting agent
        // "correct and initial all errors" - so drafts are editable, and everything afterwards
        // goes through a correction that leaves the original readable (2-5b(5)).
        var voucher = TestData.NewDraftVoucher();
        voucher.AddSimpleItem();
        voucher.SubmitForCustodianIntake(1, TestData.Now);

        var ex = Assert.Throws<DomainRuleViolationException>(() => voucher.AddSimpleItem("Late addition"));

        Assert.Equal("VCH-010", ex.RequirementId);
    }

    [Fact]
    public void AVoucherCannotBeSubmittedWithNoItems()
    {
        // VCH-011, invariant I-02. AR 195-5 2-3a: "all physical evidence will be inventoried and
        // accounted for on DA Form 4137" - a form accounting for nothing is not a custody
        // document.
        var voucher = TestData.NewDraftVoucher();

        var ex = Assert.Throws<DomainRuleViolationException>(
            () => voucher.SubmitForCustodianIntake(1, TestData.Now));

        Assert.Equal("VCH-011", ex.RequirementId);
        Assert.Contains("2-3a", ex.Message, StringComparison.Ordinal);
    }
}
