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
    }
}
