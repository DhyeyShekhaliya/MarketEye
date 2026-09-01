namespace MarketEye.Application.Indicators;

/// <summary>
/// Pure indicator math (PLAN.md §4.3, §8.4).
///
/// Every method takes an ordered series and returns a series of the same length, with nulls where
/// there is not yet enough history. Keeping them pure and allocation-light matters twice over:
/// §8.4 requires testing against published reference values rather than against our own output,
/// and `docs/adr/0006` makes indicator computation the one workload that must stay incremental to
/// fit inside App Service F1's CPU quota.
///
/// All inputs are AdjClose, never raw Close. An unadjusted series steps at every split and bonus,
/// which would inject a false spike into every indicator on that date (§4.4).
/// </summary>
public static class TechnicalIndicators
{
    /// <summary>Simple moving average over <paramref name="period"/> values.</summary>
    public static decimal?[] Sma(IReadOnlyList<decimal> values, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[values.Count];
        decimal sum = 0;

        for (var i = 0; i < values.Count; i++)
        {
            sum += values[i];
            if (i >= period) sum -= values[i - period];
            if (i >= period - 1) result[i] = sum / period;
        }
        return result;
    }

    /// <summary>
    /// Exponential moving average, seeded with the SMA of the first <paramref name="period"/>
    /// values. The seed choice is not cosmetic: seeding with the first value instead shifts every
    /// subsequent EMA and would silently disagree with every published reference table.
    /// </summary>
    public static decimal?[] Ema(IReadOnlyList<decimal> values, int period)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[values.Count];
        if (values.Count < period) return result;

        var multiplier = 2m / (period + 1);

        decimal seed = 0;
        for (var i = 0; i < period; i++) seed += values[i];
        var ema = seed / period;
        result[period - 1] = ema;

        for (var i = period; i < values.Count; i++)
        {
            ema = ((values[i] - ema) * multiplier) + ema;
            result[i] = ema;
        }
        return result;
    }

    /// <summary>
    /// Wilder's RSI. Wilder's smoothing uses 1/period, NOT the 2/(period+1) of a standard EMA —
    /// substituting one for the other is the single most common RSI bug and produces values that
    /// look plausible while disagreeing with every reference implementation.
    /// </summary>
    public static decimal?[] Rsi(IReadOnlyList<decimal> values, int period = 14)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(period, 1);
        var result = new decimal?[values.Count];
        if (values.Count <= period) return result;

        decimal gainSum = 0, lossSum = 0;
        for (var i = 1; i <= period; i++)
        {
            var change = values[i] - values[i - 1];
            if (change > 0) gainSum += change; else lossSum -= change;
        }

        var avgGain = gainSum / period;
        var avgLoss = lossSum / period;
        result[period] = RsiFrom(avgGain, avgLoss);

        for (var i = period + 1; i < values.Count; i++)
        {
            var change = values[i] - values[i - 1];
            var gain = change > 0 ? change : 0m;
            var loss = change < 0 ? -change : 0m;

            // Wilder's smoothing.
            avgGain = ((avgGain * (period - 1)) + gain) / period;
            avgLoss = ((avgLoss * (period - 1)) + loss) / period;
            result[i] = RsiFrom(avgGain, avgLoss);
        }
        return result;
    }

    // An unbroken run of gains gives zero average loss. RS is then undefined rather than infinite,
    // and RSI is 100 by convention -- returning a divide-by-zero here would kill the whole ingest.
    private static decimal RsiFrom(decimal avgGain, decimal avgLoss) =>
        avgLoss == 0 ? 100m : 100m - (100m / (1m + (avgGain / avgLoss)));

    /// <summary>MACD line and its signal line.</summary>
    public static (decimal?[] Macd, decimal?[] Signal) Macd(
        IReadOnlyList<decimal> values, int fast = 12, int slow = 26, int signal = 9)
    {
        var fastEma = Ema(values, fast);
        var slowEma = Ema(values, slow);

        var macd = new decimal?[values.Count];
        for (var i = 0; i < values.Count; i++)
        {
            if (fastEma[i] is { } f && slowEma[i] is { } sl) macd[i] = f - sl;
        }

        // The signal line is an EMA of the MACD line, which only exists from index slow-1 onward.
        // Feeding the leading nulls in as zeroes would drag the signal toward zero for its first
        // several values.
        var defined = macd.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        var signalOnDefined = Ema(defined, signal);

        var signalLine = new decimal?[values.Count];
        var firstDefined = Array.FindIndex(macd, v => v.HasValue);
        if (firstDefined >= 0)
        {
            for (var i = 0; i < signalOnDefined.Length; i++) signalLine[firstDefined + i] = signalOnDefined[i];
        }
        return (macd, signalLine);
    }

    /// <summary>
    /// Average True Range (Wilder). True range accounts for gaps, which is why it uses the
    /// previous close rather than just the current bar's high-low span.
    /// </summary>
    public static decimal?[] Atr(
        IReadOnlyList<decimal> high, IReadOnlyList<decimal> low, IReadOnlyList<decimal> close,
        int period = 14)
    {
        if (high.Count != low.Count || low.Count != close.Count)
            throw new ArgumentException("High, low and close series must be the same length.");

        var count = close.Count;
        var result = new decimal?[count];
        if (count <= period) return result;

        var tr = new decimal[count];
        tr[0] = high[0] - low[0];
        for (var i = 1; i < count; i++)
        {
            tr[i] = Math.Max(high[i] - low[i],
                    Math.Max(Math.Abs(high[i] - close[i - 1]), Math.Abs(low[i] - close[i - 1])));
        }

        decimal sum = 0;
        for (var i = 1; i <= period; i++) sum += tr[i];
        var atr = sum / period;
        result[period] = atr;

        for (var i = period + 1; i < count; i++)
        {
            atr = ((atr * (period - 1)) + tr[i]) / period;
            result[i] = atr;
        }
        return result;
    }

    /// <summary>
    /// Annualised realised volatility from daily log returns, using 252 trading days.
    /// Population standard deviation, matching the usual realised-vol convention.
    /// </summary>
    public static decimal?[] RealisedVolatility(
        IReadOnlyList<decimal> values, int period = 30, int tradingDays = 252)
    {
        var result = new decimal?[values.Count];
        if (values.Count <= period) return result;

        var logReturns = new double[values.Count];
        for (var i = 1; i < values.Count; i++)
        {
            if (values[i - 1] <= 0 || values[i] <= 0) continue;
            logReturns[i] = Math.Log((double)(values[i] / values[i - 1]));
        }

        for (var i = period; i < values.Count; i++)
        {
            double sum = 0;
            for (var j = i - period + 1; j <= i; j++) sum += logReturns[j];
            var mean = sum / period;

            double variance = 0;
            for (var j = i - period + 1; j <= i; j++)
            {
                var d = logReturns[j] - mean;
                variance += d * d;
            }
            variance /= period;

            result[i] = (decimal)(Math.Sqrt(variance) * Math.Sqrt(tradingDays));
        }
        return result;
    }
}
