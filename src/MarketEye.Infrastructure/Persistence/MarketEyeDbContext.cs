using Microsoft.EntityFrameworkCore;
using MarketEye.Domain.Entities;

namespace MarketEye.Infrastructure.Persistence;

/// <summary>
/// EF Core context for CRUD and configuration only.
///
/// PLAN.md §3: the ingest path uses Dapper + SqlBulkCopy, never EF. Price bars are
/// written in the millions per night and EF's change tracker cannot carry that load.
/// </summary>
public class MarketEyeDbContext(DbContextOptions<MarketEyeDbContext> options) : DbContext(options)
{
    public DbSet<Security> Securities => Set<Security>();
    public DbSet<DataSnapshot> DataSnapshots => Set<DataSnapshot>();
    public DbSet<IngestionRun> IngestionRuns => Set<IngestionRun>();
    public DbSet<Fundamentals> Fundamentals => Set<Fundamentals>();
    public DbSet<PriceBar> PriceBars => Set<PriceBar>();
    public DbSet<IndicatorSet> Indicators => Set<IndicatorSet>();
    public DbSet<CorporateAction> CorporateActions => Set<CorporateAction>();
    public DbSet<FundamentalRatios> FundamentalRatios => Set<FundamentalRatios>();
    public DbSet<MetricConceptEntity> MetricConcepts => Set<MetricConceptEntity>();
    public DbSet<ScreenRun> ScreenRuns => Set<ScreenRun>();
    public DbSet<ApiCallBudget> ApiCallBudgets => Set<ApiCallBudget>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Security>(e =>
        {
            e.ToTable("Securities");
            e.HasKey(x => x.Id);
            e.Property(x => x.Ticker).HasMaxLength(20).IsRequired();
            e.Property(x => x.ProviderSecurityId).HasMaxLength(64).IsRequired();
            e.Property(x => x.Name).HasMaxLength(200).IsRequired();
            e.Property(x => x.Exchange).HasMaxLength(20).IsRequired();
            e.Property(x => x.Sector).HasMaxLength(100);
            e.Property(x => x.Industry).HasMaxLength(100);
            e.Property(x => x.DelistingReason).HasConversion<string>().HasMaxLength(32);

            // Identity is the provider's stable id, not the ticker (§4.4): tickers get
            // reassigned, and keying on them would create a second row on a ticker change.
            e.HasIndex(x => x.ProviderSecurityId).IsUnique();

            // Tickers are only unique among active securities — a delisted ticker can be
            // reissued to a different company.
            e.HasIndex(x => x.Ticker).HasFilter("[IsActive] = 1").IsUnique();
        });

        b.Entity<DataSnapshot>(e =>
        {
            e.ToTable("DataSnapshots");
            e.HasKey(x => x.Id);
            e.Property(x => x.ProviderVersion).HasMaxLength(64).IsRequired();
            e.Ignore(x => x.IsSealed);

            // Queries resolve "the newest sealed snapshot" on every screen and backtest
            // (§4.5), so that lookup gets its own filtered index.
            e.HasIndex(x => new { x.AsOfDate, x.SealedAt }).HasFilter("[SealedAt] IS NOT NULL");
        });

        b.Entity<IngestionRun>(e =>
        {
            e.ToTable("IngestionRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.Source).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Snapshot).WithMany().HasForeignKey(x => x.SnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        b.Entity<Fundamentals>(e =>
        {
            // §4.1: system-versioned temporal table. This is the whole point of including
            // Fundamentals in the Phase 0 slice — it proves EF's temporal mapping produces
            // the same shape the SQL in §4.1 specifies.
            e.ToTable("Fundamentals", t => t.IsTemporal(tt =>
            {
                tt.UseHistoryTable("FundamentalsHistory");
                tt.HasPeriodStart("ValidFrom");
                tt.HasPeriodEnd("ValidTo");
            }));

            e.HasKey(x => new { x.SecurityId, x.FiscalPeriodEnd });
            e.Property(x => x.Revenue).HasPrecision(18, 2);
            e.Property(x => x.NetIncome).HasPrecision(18, 2);
            e.Property(x => x.TotalDebt).HasPrecision(18, 2);
            e.Property(x => x.ShareholdersEquity).HasPrecision(18, 2);

            e.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);

            // Every point-in-time read filters on ReportedDate (§4.1).
            e.HasIndex(x => x.ReportedDate);
        });

        b.Entity<PriceBar>(e =>
        {
            e.ToTable("PriceBars");
            e.HasKey(x => new { x.SecurityId, x.Date });

            // §4.4: separate columns, never conflated. Same precision so no silent rounding
            // difference creeps in between the execution price and the return price.
            foreach (var p in new[] { nameof(PriceBar.Open), nameof(PriceBar.High),
                                      nameof(PriceBar.Low), nameof(PriceBar.Close),
                                      nameof(PriceBar.AdjClose) })
            {
                e.Property(p).HasPrecision(18, 4);
            }

            e.HasOne<Security>().WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);

            // Snapshot reads bound on Date across the whole universe (§4.5).
            e.HasIndex(x => x.Date);
        });

        b.Entity<IndicatorSet>(e =>
        {
            e.ToTable("Indicators");
            e.HasKey(x => new { x.SecurityId, x.Date });
            foreach (var p in new[] { nameof(IndicatorSet.Sma50), nameof(IndicatorSet.Sma200),
                                      nameof(IndicatorSet.Rsi14), nameof(IndicatorSet.Macd),
                                      nameof(IndicatorSet.MacdSignal), nameof(IndicatorSet.Atr14),
                                      nameof(IndicatorSet.Vol30) })
            {
                e.Property(p).HasPrecision(18, 6);
            }
            e.HasOne<Security>().WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.Date);
        });

        b.Entity<CorporateAction>(e =>
        {
            e.ToTable("CorporateActions");
            e.HasKey(x => x.Id);
            e.Property(x => x.ActionType).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.AdjustmentFactor).HasPrecision(18, 8);
            e.Property(x => x.DividendAmount).HasPrecision(18, 4);
            e.Property(x => x.NewTicker).HasMaxLength(20);
            e.Property(x => x.RawDescription).HasMaxLength(512);

            e.HasOne(x => x.Security).WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);

            // Re-ingesting the same day's actions must not duplicate them: ingestion is required
            // to be idempotent (§10 Phase 1), and a duplicated split would adjust prices twice.
            e.HasIndex(x => new { x.SecurityId, x.EffectiveDate, x.ActionType }).IsUnique();
        });

        b.Entity<FundamentalRatios>(e =>
        {
            e.ToTable("FundamentalRatios");
            e.HasKey(x => new { x.SecurityId, x.ReportedDate });
            foreach (var p in new[] { "Pe", "Pb", "Ps", "Roe", "Roic", "DebtToEquity",
                                      "GrossMargin", "FcfYield" })
            {
                e.Property(p).HasPrecision(18, 6);
            }
            e.Property(x => x.MarketCap).HasPrecision(20, 2);
            e.Property(x => x.Basis).HasConversion<string>().HasMaxLength(16);
            e.HasOne<Security>().WithMany().HasForeignKey(x => x.SecurityId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ReportedDate);
        });

        b.Entity<MetricConceptEntity>(e =>
        {
            e.ToTable("MetricConcepts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(128).IsRequired();
            e.Property(x => x.Description).HasMaxLength(512);
            e.Property(x => x.ColumnName).HasMaxLength(64).IsRequired();
            e.Property(x => x.AllowedOperatorsCsv).HasMaxLength(256).IsRequired();
            e.Property(x => x.Source).HasConversion<string>().HasMaxLength(24);
            e.Property(x => x.DefaultOperator).HasConversion<string>().HasMaxLength(24);
            // Wide enough for Indian market caps in rupees: a large-cap is ~2e13, and
            // decimal(18,6) leaves only 12 integer digits. Range bounds are compared against
            // values from any concept, so they must span the widest of them.
            e.Property(x => x.MinValue).HasPrecision(28, 6);
            e.Property(x => x.MaxValue).HasPrecision(28, 6);
            e.Property(x => x.DefaultThreshold).HasPrecision(28, 6);

            // The validator matches ordinally; a case-insensitive duplicate would make which row
            // wins depend on collation.
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<ApiCallBudget>(e =>
        {
            e.ToTable("ApiCallBudgets");
            e.HasKey(x => x.Id);
            e.Property(x => x.Provider).HasMaxLength(32).IsRequired();

            // One row per provider per day, enforced by the database. Two concurrent callers
            // creating separate rows would each see half the usage and together double the quota.
            e.HasIndex(x => new { x.Provider, x.Date }).IsUnique();
        });

        b.Entity<ScreenRun>(e =>
        {
            e.ToTable("ScreenRuns");
            e.HasKey(x => x.Id);
            e.Property(x => x.CriteriaJson).IsRequired();
            e.HasOne(x => x.Snapshot).WithMany().HasForeignKey(x => x.SnapshotId)
                .OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.RunAt);
        });
    }
}
