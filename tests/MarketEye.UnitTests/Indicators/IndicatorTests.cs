using FluentAssertions;
using MarketEye.Application.Indicators;
using Xunit;

namespace MarketEye.UnitTests.IndicatorMath;

/// <summary>
/// PLAN.md §8.4: indicator math is tested against published reference values, never against its
/// own output. A test that asserts "the function returns what the function returned" locks in
/// whatever bug shipped first.
///
/// The RSI series below is Wilder's own worked example from *New Concepts in Technical Trading
/// Systems* (1978), the same series reproduced in StockCharts' and Investopedia's RSI articles.
/// Expected values are quoted from those references, not generated here.
/// </summary>
public class IndicatorTests
{
    /// <summary>Wilder's published 14-period RSI example closes.</summary>
    private static readonly decimal[] WilderCloses =
    [
        44.34m, 44.09m, 44.15m, 43.61m, 44.33m, 44.83m, 45.10m, 45.42m,
        45.84m, 46.08m, 45.89m, 46.03m, 45.61m, 46.28m, 46.28m, 46.00m,
        46.03m, 46.41m, 46.22m, 45.64m, 46.21m, 46.25m, 45.71m, 46.45m,
        45.78m, 45.35m, 44.03m, 44.18m, 44.22m, 44.57m, 43.42m, 42.66m, 43.13m,
    ];

    [Fact]
    public void Rsi_matches_Wilders_published_example()
    {
        var rsi = TechnicalIndicators.Rsi(WilderCloses, 14);

        // Published values for this series, to 2dp. Index 14 is the first computable RSI.
        rsi[14].Should().BeApproximately(70.46m, 0.05m);
        rsi[15].Should().BeApproximately(66.25m, 0.05m);
        rsi[16].Should().BeApproximately(66.48m, 0.05m);
        rsi[19].Should().BeApproximately(57.97m, 0.10m);
    }

    [Fact]
    public void Rsi_has_no_value_before_the_period_is_filled()
    {
        var rsi = TechnicalIndicators.Rsi(WilderCloses, 14);
        rsi.Take(14).Should().AllSatisfy(v => v.Should().BeNull());
        rsi[14].Should().NotBeNull();
    }

    [Fact]
    public void Rsi_is_100_on_an_unbroken_run_of_gains()
    {
        // Average loss is zero, so RS is undefined rather than infinite. Convention is 100.
        // The naive implementation divides by zero here and takes the whole ingest down.
        var rising = Enumerable.Range(1, 30).Select(i => (decimal)i).ToList();
        TechnicalIndicators.Rsi(rising, 14)[^1].Should().Be(100m);
    }

    [Fact]
    public void Rsi_uses_Wilder_smoothing_not_a_standard_ema()
    {
        // Guard against the classic substitution of 2/(period+1) for 1/period. With a standard
        // EMA the value at index 19 comes out materially higher than Wilder's published 57.97.
        var rsi = TechnicalIndicators.Rsi(WilderCloses, 14);
        rsi[19]!.Value.Should().BeLessThan(60m,
            "Wilder smoothing gives ~57.97 here; a standard EMA would overshoot");
    }

    [Fact]
    public void Sma_matches_a_hand_computed_window()
    {
        decimal[] v = [1m, 2m, 3m, 4m, 5m, 6m];
        var sma = TechnicalIndicators.Sma(v, 3);

        sma[0].Should().BeNull();
        sma[1].Should().BeNull();
        sma[2].Should().Be(2m);   // (1+2+3)/3
        sma[3].Should().Be(3m);   // (2+3+4)/3
        sma[5].Should().Be(5m);   // (4+5+6)/3
    }

    [Fact]
    public void Sma_of_a_constant_series_is_that_constant()
    {
        var flat = Enumerable.Repeat(42m, 100).ToList();
        TechnicalIndicators.Sma(flat, 50).Skip(49).Should().AllSatisfy(v => v.Should().Be(42m));
    }

    [Fact]
    public void Ema_is_seeded_with_the_sma_of_the_first_period()
    {
        decimal[] v = [1m, 2m, 3m, 4m, 5m];
        var ema = TechnicalIndicators.Ema(v, 3);

        // Seed = SMA(1,2,3) = 2. Seeding with the first value instead would give 1 here and
        // shift every subsequent value, disagreeing with published tables.
        ema[2].Should().Be(2m);

        // multiplier = 2/(3+1) = 0.5; next = (4-2)*0.5 + 2 = 3
        ema[3].Should().Be(3m);
        // next = (5-3)*0.5 + 3 = 4
        ema[4].Should().Be(4m);
    }

    [Fact]
    public void Ema_of_a_constant_series_is_that_constant()
    {
        var flat = Enumerable.Repeat(7m, 50).ToList();
        TechnicalIndicators.Ema(flat, 12).Skip(11).Should().AllSatisfy(v => v.Should().Be(7m));
    }

    [Fact]
    public void Macd_is_the_difference_of_the_two_emas()
    {
        var values = Enumerable.Range(1, 100).Select(i => (decimal)i).ToList();
        var (macd, signal) = TechnicalIndicators.Macd(values, 12, 26, 9);

        var fast = TechnicalIndicators.Ema(values, 12);
        var slow = TechnicalIndicators.Ema(values, 26);

        // Defined only once the SLOW ema exists.
        macd[24].Should().BeNull();
        macd[25].Should().Be(fast[25]!.Value - slow[25]!.Value);
        macd[^1].Should().Be(fast[^1]!.Value - slow[^1]!.Value);

        // Signal is an EMA of the MACD line, so it lags by another (signal-1) points.
        signal[25].Should().BeNull();
        signal[33].Should().NotBeNull();
    }

    [Fact]
    public void Macd_signal_does_not_treat_leading_nulls_as_zero()
    {
        // If the leading nulls were fed in as zeroes, the first signal values would be dragged
        // toward zero and sit far below the MACD line on a steadily trending series.
        var values = Enumerable.Range(1, 100).Select(i => (decimal)i).ToList();
        var (macd, signal) = TechnicalIndicators.Macd(values);

        var firstSignalIndex = Array.FindIndex(signal, v => v.HasValue);
        signal[firstSignalIndex]!.Value.Should().BeGreaterThan(macd[firstSignalIndex]!.Value * 0.5m);
    }

    [Fact]
    public void Atr_accounts_for_gaps_via_the_previous_close()
    {
        // Bar 1 gaps up: high 20, low 18, previous close 10. True range is 20-10 = 10, not the
        // 2-point intraday span. An ATR that ignores gaps understates risk on exactly the days
        // that matter.
        decimal[] high = [12m, 20m, 21m];
        decimal[] low = [8m, 18m, 19m];
        decimal[] close = [10m, 19m, 20m];

        var atr = TechnicalIndicators.Atr(high, low, close, 1);
        atr[1].Should().Be(10m);
    }

    [Fact]
    public void Atr_rejects_mismatched_series_lengths()
    {
        var act = () => TechnicalIndicators.Atr([1m, 2m], [1m], [1m, 2m], 1);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Volatility_of_a_flat_series_is_zero()
    {
        var flat = Enumerable.Repeat(100m, 60).ToList();
        TechnicalIndicators.RealisedVolatility(flat, 30)[^1].Should().Be(0m);
    }

    [Fact]
    public void Volatility_is_annualised()
    {
        // A series alternating +1%/-1% daily has a known daily sigma; annualising multiplies by
        // sqrt(252). Asserting the annualised value is far above the daily one catches a missing
        // sqrt(252) factor, which would otherwise look like a plausible small number.
        var values = new List<decimal> { 100m };
        for (var i = 1; i < 60; i++) values.Add(i % 2 == 0 ? 100m : 101m);

        var vol = TechnicalIndicators.RealisedVolatility(values, 30)![^1];
        vol.Should().NotBeNull();
        vol!.Value.Should().BeGreaterThan(0.10m, "annualised vol of a 1% daily oscillation is ~16%");
    }

    [Fact]
    public void All_indicators_return_a_series_the_same_length_as_their_input()
    {
        // The ingest writes one row per bar and indexes positionally. A length mismatch would
        // silently offset every indicator against its date.
        var values = Enumerable.Range(1, 300).Select(i => (decimal)i).ToList();

        TechnicalIndicators.Sma(values, 50).Should().HaveCount(300);
        TechnicalIndicators.Ema(values, 12).Should().HaveCount(300);
        TechnicalIndicators.Rsi(values, 14).Should().HaveCount(300);
        TechnicalIndicators.Atr(values, values, values, 14).Should().HaveCount(300);
        TechnicalIndicators.RealisedVolatility(values, 30).Should().HaveCount(300);

        var (macd, signal) = TechnicalIndicators.Macd(values);
        macd.Should().HaveCount(300);
        signal.Should().HaveCount(300);
    }
}
