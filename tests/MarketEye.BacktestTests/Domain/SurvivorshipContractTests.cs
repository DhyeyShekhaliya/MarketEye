using FluentAssertions;
using MarketEye.Domain.Entities;
using Xunit;

namespace MarketEye.BacktestTests.Domain;

/// <summary>
/// The backtester is the consumer of the delisting contract, so the guard lives here.
///
/// PLAN.md §7: delisted securities stay in the universe and exit at their last price, or at
/// ZERO for bankruptcy. Removing them is survivorship bias. That behaviour is only expressible
/// if the domain keeps the delisting fields and distinguishes bankruptcy from an acquisition —
/// so this fails loudly if someone "tidies up" the model before Phase 3 exists to catch it.
/// </summary>
public class SurvivorshipContractTests
{
    [Fact]
    public void A_delisted_security_keeps_its_row_and_records_why()
    {
        var delisted = new Security
        {
            Ticker = "CCC",
            ProviderSecurityId = "FIX-0003",
            Name = "Gamma Retail",
            Exchange = "NYSE",
            IsActive = false,
            DelistedDate = new DateOnly(2024, 6, 28),
            DelistingReason = DelistingReason.Bankruptcy,
        };

        delisted.IsActive.Should().BeFalse();
        delisted.DelistedDate.Should().NotBeNull(
            "the backtest needs the exit date to close the position at the right bar");
        delisted.DelistingReason.Should().Be(DelistingReason.Bankruptcy);
    }

    [Fact]
    public void Bankruptcy_is_distinguishable_from_other_delistings()
    {
        // §7 prices a bankruptcy exit at zero and every other exit at the last traded price.
        // Collapsing these into a single "delisted" flag would silently overstate returns.
        Enum.GetValues<DelistingReason>().Should().Contain(DelistingReason.Bankruptcy);
        Enum.GetValues<DelistingReason>().Should().Contain(DelistingReason.Acquisition);
        DelistingReason.Bankruptcy.Should().NotBe(DelistingReason.Acquisition);
    }
}
