using System.Globalization;
using MarketEye.Application.MarketData;

namespace MarketEye.Infrastructure.MarketData.Bhavcopy;

/// <summary>
/// Parses an NSE daily bhavcopy CSV (PLAN.md §4.3, `docs/adr/0005`).
///
/// The bhavcopy is the exchange's own end-of-day record of every security that traded. Because it
/// was never survivor-filtered, an archive of past files reconstructs the universe as it stood on
/// any date — including companies that have since delisted. That is what makes §7's
/// survivorship requirement satisfiable without a paid "delisted data" product.
///
/// Two formats are supported. NSE moved to the UDiFF layout in 2024 and the legacy layout is what
/// every pre-2024 archive file uses, so a five-year backfill crosses the boundary and must read
/// both. Columns are located by header name rather than position, since ordering has changed
/// between revisions.
/// </summary>
public sealed class BhavcopyParser
{
    /// <summary>
    /// Series codes that represent ordinary equity. Everything else in the file — bonds, ETFs,
    /// government securities, rights entitlements — is not a screenable equity and would otherwise
    /// silently inflate the universe.
    ///
    /// EQ is the rolling-settlement equity series. BE is the trade-for-trade segment: still
    /// equity, but delivery-only and often illiquid or under surveillance.
    /// </summary>
    private static readonly HashSet<string> EquitySeries =
        new(StringComparer.OrdinalIgnoreCase) { "EQ", "BE" };

    private static readonly string[] LegacyHeaders =
        ["SYMBOL", "SERIES", "OPEN", "HIGH", "LOW", "CLOSE", "PREVCLOSE", "TOTTRDQTY", "TIMESTAMP", "ISIN"];

    private static readonly string[] UdiffHeaders =
        ["TckrSymb", "SctySrs", "OpnPric", "HghPric", "LwPric", "ClsPric", "PrvsClsgPric", "TtlTradgVol", "TradDt", "ISIN"];

    /// <summary>
    /// NSE's "security-wise full bhavdata" layout, which is what public archives carry for recent
    /// years. It is the only layout available from mid-2021 onward in most mirrors.
    ///
    /// Critically it carries NO ISIN column, so securities ingested from it cannot be keyed on a
    /// provider-stable identifier the way §4.4 wants. See IsinResolver for how that is handled.
    /// Fields are also space-padded after each comma.
    /// </summary>
    private static readonly string[] FullBhavHeaders =
        ["SYMBOL", "SERIES", "OPEN_PRICE", "HIGH_PRICE", "LOW_PRICE", "CLOSE_PRICE", "PREV_CLOSE", "TTL_TRD_QNTY", "DATE1"];

    /// <summary>Parses the whole file, skipping non-equity series and unparseable rows.</summary>
    public IReadOnlyList<BhavcopyRow> Parse(TextReader reader)
    {
        var headerLine = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(headerLine)) return [];

        var headers = SplitCsv(headerLine).Select(h => h.Trim()).ToArray();
        var map = BuildColumnMap(headers)
                  ?? throw new FormatException(
                      "Unrecognised bhavcopy layout. Expected either the legacy (SYMBOL/SERIES/...) " +
                      "or UDiFF (TckrSymb/SctySrs/...) header set. Got: " + string.Join(",", headers));

        var rows = new List<BhavcopyRow>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var f = SplitCsv(line);
            if (f.Length < headers.Length) continue;

            var series = Get(f, map.Series).Trim();
            if (!EquitySeries.Contains(series)) continue;

            if (!TryDecimal(Get(f, map.Open), out var open) ||
                !TryDecimal(Get(f, map.High), out var high) ||
                !TryDecimal(Get(f, map.Low), out var low) ||
                !TryDecimal(Get(f, map.Close), out var close) ||
                !TryDate(Get(f, map.Date), out var date))
            {
                // A malformed row is skipped rather than failing the file: NSE archives contain
                // occasional trailer and blank lines, and one bad row must not lose a trading day.
                continue;
            }

            TryDecimal(Get(f, map.PrevClose), out var prevClose);
            _ = long.TryParse(Get(f, map.Volume), NumberStyles.Any, CultureInfo.InvariantCulture, out var volume);

            rows.Add(new BhavcopyRow(
                Symbol: Get(f, map.Symbol).Trim(),
                Isin: Get(f, map.Isin).Trim(),
                Series: series.ToUpperInvariant(),
                Date: date,
                Open: open, High: high, Low: low, Close: close,
                PreviousClose: prevClose == 0 ? null : prevClose,
                Volume: volume));
        }
        return rows;
    }

    private sealed record ColumnMap(
        int Symbol, int Series, int Open, int High, int Low, int Close,
        int PrevClose, int Volume, int Date, int Isin);

    private static ColumnMap? BuildColumnMap(string[] headers)
    {
        var idx = headers
            .Select((h, i) => (h, i))
            .ToDictionary(x => x.h, x => x.i, StringComparer.OrdinalIgnoreCase);

        foreach (var set in new[] { LegacyHeaders, UdiffHeaders })
        {
            if (set.All(idx.ContainsKey))
            {
                return new ColumnMap(
                    idx[set[0]], idx[set[1]], idx[set[2]], idx[set[3]], idx[set[4]],
                    idx[set[5]], idx[set[6]], idx[set[7]], idx[set[8]], idx[set[9]]);
            }
        }

        // sec_bhavdata_full: same fields, no ISIN. Isin is mapped to -1 so Get() yields empty
        // rather than throwing, and the caller resolves identity another way.
        if (FullBhavHeaders.All(idx.ContainsKey))
        {
            return new ColumnMap(
                idx["SYMBOL"], idx["SERIES"], idx["OPEN_PRICE"], idx["HIGH_PRICE"],
                idx["LOW_PRICE"], idx["CLOSE_PRICE"], idx["PREV_CLOSE"], idx["TTL_TRD_QNTY"],
                idx["DATE1"], Isin: -1);
        }
        return null;
    }

    private static string Get(string[] fields, int index) =>
        index >= 0 && index < fields.Length ? fields[index] : string.Empty;

    private static bool TryDecimal(string s, out decimal value) =>
        decimal.TryParse(s.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);

    private static readonly string[] DateFormats =
        ["dd-MMM-yyyy", "yyyy-MM-dd", "dd-MM-yyyy", "dd/MM/yyyy", "yyyyMMdd", "dd-MMM-yy"];

    private static bool TryDate(string s, out DateOnly date)
    {
        s = s.Trim();
        foreach (var f in DateFormats)
        {
            if (DateOnly.TryParseExact(s, f, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
                return true;
        }
        return DateOnly.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>Minimal CSV split. Bhavcopy fields are unquoted, but company names can carry commas.</summary>
    private static string[] SplitCsv(string line)
    {
        var result = new List<string>();
        var start = 0;
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') inQuotes = !inQuotes;
            else if (line[i] == ',' && !inQuotes)
            {
                result.Add(line[start..i].Trim('"'));
                start = i + 1;
            }
        }
        result.Add(line[start..].Trim('"'));
        return [.. result];
    }
}

/// <summary>
/// One equity row from a bhavcopy.
///
/// <paramref name="Isin"/> is the identity key, not <paramref name="Symbol"/>. §4.4 requires
/// reconciliation on a provider-stable id so a ticker change does not create a second Security
/// row; ISIN survives ticker changes, and NSE puts it in the file.
/// </summary>
public sealed record BhavcopyRow(
    string Symbol, string Isin, string Series, DateOnly Date,
    decimal Open, decimal High, decimal Low, decimal Close,
    decimal? PreviousClose, long Volume);
