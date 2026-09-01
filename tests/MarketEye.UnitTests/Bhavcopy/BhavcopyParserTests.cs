using FluentAssertions;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using Xunit;

namespace MarketEye.UnitTests.Bhavcopy;

public class BhavcopyParserTests
{
    private readonly BhavcopyParser _parser = new();

    private static TextReader R(string csv) => new StringReader(csv.TrimStart('\n'));

    private const string Legacy = """
        SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
        RELIANCE,EQ,2450.00,2480.50,2440.10,2475.25,2475.00,2445.00,5000000,12000000000,03-JAN-2024,150000,INE002A01018
        TCS,EQ,3600.00,3650.00,3590.00,3640.00,3640.00,3595.00,2000000,7000000000,03-JAN-2024,90000,INE467B01029
        """;

    private const string Udiff = """
        TradDt,TckrSymb,SctySrs,OpnPric,HghPric,LwPric,ClsPric,PrvsClsgPric,TtlTradgVol,ISIN
        2024-08-01,RELIANCE,EQ,2900.00,2950.00,2890.00,2940.00,2895.00,4000000,INE002A01018
        """;

    [Fact]
    public void Parses_the_legacy_layout()
    {
        var rows = _parser.Parse(R(Legacy));

        rows.Should().HaveCount(2);
        var r = rows[0];
        r.Symbol.Should().Be("RELIANCE");
        r.Isin.Should().Be("INE002A01018");
        r.Date.Should().Be(new DateOnly(2024, 1, 3));
        r.Open.Should().Be(2450.00m);
        r.High.Should().Be(2480.50m);
        r.Low.Should().Be(2440.10m);
        r.Close.Should().Be(2475.25m);
        r.PreviousClose.Should().Be(2445.00m);
        r.Volume.Should().Be(5_000_000);
    }

    [Fact]
    public void Parses_the_udiff_layout()
    {
        // A five-year backfill crosses NSE's 2024 format change, so both layouts must parse or the
        // archive has a hole in the middle of it.
        var rows = _parser.Parse(R(Udiff));

        rows.Should().ContainSingle();
        rows[0].Symbol.Should().Be("RELIANCE");
        rows[0].Isin.Should().Be("INE002A01018");
        rows[0].Date.Should().Be(new DateOnly(2024, 8, 1));
        rows[0].Close.Should().Be(2940.00m);
    }

    [Fact]
    public void Both_layouts_yield_the_same_shape_for_the_same_security()
    {
        var legacy = _parser.Parse(R(Legacy)).First(r => r.Symbol == "RELIANCE");
        var udiff = _parser.Parse(R(Udiff)).Single();

        // The ISIN is what ties the two eras together -- symbols can change, ISINs do not.
        udiff.Isin.Should().Be(legacy.Isin);
    }

    [Fact]
    public void Non_equity_series_are_excluded()
    {
        // Government securities, ETFs and rights entitlements all appear in the same file. Letting
        // them through would silently inflate the universe with things that are not screenable
        // equities.
        var csv = """
            SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
            RELIANCE,EQ,100,101,99,100,100,100,1000,100000,03-JAN-2024,10,INE002A01018
            NIFTYBEES,GB,250,252,249,251,251,250,500,125000,03-JAN-2024,5,INF204KB14I2
            SOMEBOND,GS,99,99,99,99,99,99,10,990,03-JAN-2024,1,IN0020200062
            RELIANCE-RE,RE,10,11,9,10,10,10,100,1000,03-JAN-2024,2,INE002A20018
            """;

        var rows = _parser.Parse(R(csv));

        rows.Should().ContainSingle();
        rows[0].Symbol.Should().Be("RELIANCE");
    }

    [Fact]
    public void The_trade_for_trade_series_is_kept()
    {
        // BE is still equity -- delivery-only and often illiquid, but real companies. Dropping it
        // would quietly remove securities under surveillance, which is a survivorship-shaped hole.
        var csv = """
            SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
            SMALLCO,BE,50,52,49,51,51,50,1000,51000,03-JAN-2024,10,INE999A01018
            """;

        _parser.Parse(R(csv)).Should().ContainSingle();
    }

    [Fact]
    public void A_malformed_row_is_skipped_without_losing_the_file()
    {
        // NSE archives carry trailer and blank lines. One bad row must not cost a whole trading day.
        var csv = """
            SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
            GOODCO,EQ,100,101,99,100,100,100,1000,100000,03-JAN-2024,10,INE002A01018
            BADCO,EQ,not-a-number,101,99,100,100,100,1000,100000,03-JAN-2024,10,INE002A01019
            ALSOGOOD,EQ,200,201,199,200,200,200,1000,200000,03-JAN-2024,10,INE002A01020

            """;

        var rows = _parser.Parse(R(csv));

        rows.Should().HaveCount(2);
        rows.Select(r => r.Symbol).Should().BeEquivalentTo(["GOODCO", "ALSOGOOD"]);
    }

    [Fact]
    public void An_unrecognised_layout_throws_rather_than_returning_nothing()
    {
        // Silently returning zero rows on a format change would look identical to a market
        // holiday, and the ingest would seal an empty snapshot without complaint.
        var act = () => _parser.Parse(R("Foo,Bar,Baz\n1,2,3"));
        act.Should().Throw<FormatException>().WithMessage("*Unrecognised bhavcopy layout*");
    }

    [Fact]
    public void An_empty_file_yields_no_rows()
    {
        _parser.Parse(R("")).Should().BeEmpty();
    }

    [Theory]
    [InlineData("03-JAN-2024", 2024, 1, 3)]
    [InlineData("2024-01-03", 2024, 1, 3)]
    [InlineData("03-01-2024", 2024, 1, 3)]
    public void Several_date_formats_are_accepted(string raw, int y, int m, int d)
    {
        // Date formatting has varied across NSE archive revisions; a backfill hits more than one.
        var csv = $"""
            SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
            X,EQ,1,1,1,1,1,1,1,1,{raw},1,INE000A01001
            """;

        _parser.Parse(R(csv)).Single().Date.Should().Be(new DateOnly(y, m, d));
    }
}
