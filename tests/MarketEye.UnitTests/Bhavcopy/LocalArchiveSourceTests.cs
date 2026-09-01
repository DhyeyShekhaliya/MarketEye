using System.IO.Compression;
using FluentAssertions;
using MarketEye.Infrastructure.MarketData.Bhavcopy;
using Xunit;

namespace MarketEye.UnitTests.Bhavcopy;

public class LocalArchiveSourceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "marketeye-archive-" + Guid.NewGuid().ToString("N"));

    public LocalArchiveSourceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private const string Csv = """
        SYMBOL,SERIES,OPEN,HIGH,LOW,CLOSE,LAST,PREVCLOSE,TOTTRDQTY,TOTTRDVAL,TIMESTAMP,TOTALTRADES,ISIN
        RELIANCE,EQ,2450.00,2480.50,2440.10,2475.25,2475.00,2445.00,5000000,12000000000,03-JAN-2024,150000,INE002A01018
        """;

    [Fact]
    public async Task Reads_a_plain_csv_named_with_the_udiff_date()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "BhavCopy_20240103.csv"), Csv, TestContext.Current.CancellationToken);

        var csv = await new LocalArchiveBhavcopySource(_root)
            .GetCsvAsync(new DateOnly(2024, 1, 3), TestContext.Current.CancellationToken);

        csv.Should().NotBeNull();
        csv.Should().Contain("RELIANCE");
    }

    [Fact]
    public async Task Reads_a_legacy_named_csv()
    {
        // Archive mirrors do not agree on a naming convention, so both are accepted.
        await File.WriteAllTextAsync(Path.Combine(_root, "cm03JAN2024bhav.csv"), Csv, TestContext.Current.CancellationToken);

        var csv = await new LocalArchiveBhavcopySource(_root)
            .GetCsvAsync(new DateOnly(2024, 1, 3), TestContext.Current.CancellationToken);

        csv.Should().Contain("RELIANCE");
    }

    [Fact]
    public async Task Reads_a_zipped_csv()
    {
        var zipPath = Path.Combine(_root, "cm03JAN2024bhav.csv.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("cm03JAN2024bhav.csv");
            await using var w = new StreamWriter(entry.Open());
            await w.WriteAsync(Csv);
        }

        var csv = await new LocalArchiveBhavcopySource(_root)
            .GetCsvAsync(new DateOnly(2024, 1, 3), TestContext.Current.CancellationToken);

        csv.Should().Contain("RELIANCE");
    }

    [Fact]
    public async Task Finds_files_in_nested_directories()
    {
        // Mirrors commonly shard by year/month.
        var nested = Path.Combine(_root, "2024", "JAN");
        Directory.CreateDirectory(nested);
        await File.WriteAllTextAsync(Path.Combine(nested, "cm03JAN2024bhav.csv"), Csv, TestContext.Current.CancellationToken);

        var csv = await new LocalArchiveBhavcopySource(_root)
            .GetCsvAsync(new DateOnly(2024, 1, 3), TestContext.Current.CancellationToken);

        csv.Should().Contain("RELIANCE");
    }

    [Fact]
    public async Task A_missing_day_returns_null_rather_than_throwing()
    {
        // Holidays and weekends are normal. Throwing here would make the backfill loop treat an
        // ordinary non-trading day as a failure.
        var csv = await new LocalArchiveBhavcopySource(_root)
            .GetCsvAsync(new DateOnly(2024, 1, 26), TestContext.Current.CancellationToken);

        csv.Should().BeNull();
    }

    [Fact]
    public async Task A_missing_directory_returns_null_rather_than_throwing()
    {
        var csv = await new LocalArchiveBhavcopySource(Path.Combine(_root, "does-not-exist"))
            .GetCsvAsync(new DateOnly(2024, 1, 3), TestContext.Current.CancellationToken);

        csv.Should().BeNull();
    }
}
