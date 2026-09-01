using FluentAssertions;
using MarketEye.Application.Ratios;
using MarketEye.Domain.Entities;
using Xunit;

namespace MarketEye.UnitTests.Ratios;

/// <summary>
/// Ratios feed the screening vocabulary directly, so a wrong one does not crash — it silently
/// changes which companies a user sees. The refusals matter as much as the arithmetic.
/// </summary>
public class RatioCalculatorTests
{
    private static Fundamentals F(
        decimal? revenue = null, decimal? netIncome = null, decimal? equity = null,
        decimal? debt = null, decimal? shares = null, decimal? cogs = null) => new()
    {
        SecurityId = 1,
        FiscalPeriodEnd = new DateOnly(2024, 3, 31),
        ReportedDate = new DateOnly(2024, 5, 30),
        Revenue = revenue, NetIncome = netIncome, ShareholdersEquity = equity,
        TotalDebt = debt, SharesOutstanding = shares, CostOfRevenue = cogs,
    };

    [Fact]
    public void Market_cap_is_price_times_shares()
    {
        RatioCalculator.MarketCap(price: 100m, shares: 1_000m).Should().Be(100_000m);
    }

    [Fact]
    public void Pe_is_market_cap_over_net_income()
    {
        // 100 x 1000 = 100,000 market cap; 10,000 earnings -> P/E of 10.
        var r = RatioCalculator.From(F(netIncome: 10_000m, shares: 1_000m), price: 100m,
            ReportingBasis.Consolidated);

        r.Pe.Should().Be(10m);
    }

    [Fact]
    public void A_loss_making_company_has_no_PE_rather_than_a_negative_one()
    {
        // The important one. A negative P/E sorts as "cheapest" in an ascending screen, so the
        // worst businesses would come top of a value screen -- §8.3's known-bad strategies would
        // accidentally look good.
        var r = RatioCalculator.From(F(netIncome: -5_000m, shares: 1_000m), price: 100m,
            ReportingBasis.Consolidated);

        r.Pe.Should().BeNull();
    }

    [Fact]
    public void Negative_equity_yields_no_PB_and_no_ROE()
    {
        var r = RatioCalculator.From(F(netIncome: 1_000m, equity: -500m, shares: 100m), price: 10m,
            ReportingBasis.Consolidated);

        r.Pb.Should().BeNull();
        r.Roe.Should().BeNull("return on negative equity is not a meaningful percentage");
    }

    [Fact]
    public void Roe_is_a_percentage()
    {
        var r = RatioCalculator.From(F(netIncome: 150m, equity: 1_000m), price: 10m,
            ReportingBasis.Consolidated);

        r.Roe.Should().Be(15m, "150/1000 = 15%, not 0.15");
    }

    [Fact]
    public void Debt_to_equity_is_a_plain_ratio()
    {
        var r = RatioCalculator.From(F(debt: 500m, equity: 1_000m), price: 10m,
            ReportingBasis.Consolidated);

        r.DebtToEquity.Should().Be(0.5m);
    }

    [Fact]
    public void Gross_margin_is_a_percentage_of_revenue()
    {
        // Revenue 1000, COGS 600 -> 40%.
        RatioCalculator.GrossMarginPercent(1_000m, 600m).Should().Be(40m);
    }

    [Fact]
    public void Gross_margin_can_be_negative_when_cost_exceeds_revenue()
    {
        // Unlike a valuation multiple, a negative margin is meaningful and must survive: it is
        // exactly what a screen for unprofitable businesses should find.
        RatioCalculator.GrossMarginPercent(1_000m, 1_200m).Should().Be(-20m);
    }

    [Fact]
    public void A_missing_price_leaves_price_based_ratios_null_but_keeps_the_others()
    {
        // Periods predating our price history still yield balance-sheet ratios.
        var r = RatioCalculator.From(
            F(netIncome: 100m, equity: 1_000m, debt: 500m, shares: 10m), price: null,
            ReportingBasis.Consolidated);

        r.MarketCap.Should().BeNull();
        r.Pe.Should().BeNull();
        r.Roe.Should().Be(10m);
        r.DebtToEquity.Should().Be(0.5m);
    }

    [Fact]
    public void Division_by_zero_yields_null_rather_than_throwing()
    {
        RatioCalculator.Divide(100m, 0m, allowNegativeDenominator: true).Should().BeNull();
        RatioCalculator.GrossMarginPercent(0m, 0m).Should().BeNull();
    }

    [Fact]
    public void Underivable_ratios_are_null_not_approximated()
    {
        // ROIC needs invested capital and NOPAT; FCF yield needs operating cash flow and capex.
        // Neither is available, and a plausible-looking wrong number would be ranked on.
        var r = RatioCalculator.From(F(netIncome: 100m, equity: 1_000m), price: 10m,
            ReportingBasis.Consolidated);

        r.Roic.Should().BeNull();
        r.FcfYield.Should().BeNull();
    }

    [Fact]
    public void The_reported_date_carries_through_to_the_ratio_row()
    {
        // §4.1: ratios are point-in-time facts keyed on when the market learned them.
        var f = F(netIncome: 100m, equity: 1_000m);
        var r = RatioCalculator.From(f, price: 10m, ReportingBasis.Consolidated);

        r.ReportedDate.Should().Be(f.ReportedDate);
        r.SecurityId.Should().Be(f.SecurityId);
    }
}
