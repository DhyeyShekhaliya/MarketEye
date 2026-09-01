# Backfill runbook (Phase 1, item 2)

Loads ~5 years of NSE daily bars. This is the long pole for §10's Phase 1 exit criteria.

## Why a mirror, not NSE directly

NSE returns **403** to plain HTTP clients and rate-limits aggressively. `NseBhavcopyClient`
handles that — cookie priming, browser headers, 2 req/sec — but a 5-year backfill is roughly
**1,250 sequential requests**. That is the exact pattern that gets an IP blocked partway through,
leaving a half-filled database and no clean resume point.

A public mirror is one clone, resumable, and puts no load on the exchange. Use the mirror for
backfill; the nightly job fetches one file and goes direct.

## Step 1 — clone an archive

```bash
cd ~
git clone --depth 1 https://github.com/tilak999/NSE-Data-bank.git nse-archive
du -sh nse-archive
```

Any archive works. The parser locates files by date across nested directories and accepts
`.csv` or `.csv.zip`, legacy (`cm03JAN2024bhav.csv`) or UDiFF (`*20240103*`) naming.

**Verify coverage before spending an hour ingesting.** A mirror that stops in 2023 will silently
give you a shorter history than you think:

```bash
ls ~/nse-archive | head
find ~/nse-archive -name "*2021*" | head -3    # expect hits
find ~/nse-archive -name "*2026*" | head -3    # expect hits
```

## Step 2 — point the app at it

```bash
cd "~/Documents/skills/Coding/net project"
export Ingestion__ArchivePath="$HOME/nse-archive"
export Ingestion__TriggerSecret="local-dev-secret"
```

With `Ingestion:ArchivePath` set, DI resolves `IBhavcopySource` to `LocalArchiveBhavcopySource`.
Unset it and you get `NseBhavcopyClient`. That is the only switch.

## Step 3 — make sure the database is ready

```bash
docker compose up -d
dotnet ef database update --project src/MarketEye.Infrastructure --startup-project src/MarketEye.Api
```

## Step 4 — run the backfill

Start the API, then walk the date range one trading day at a time:

```bash
dotnet run --project src/MarketEye.Api &
# wait for "Now listening on"

start=2021-09-01
end=$(date +%Y-%m-%d)
d=$start
while [ "$d" != "$end" ]; do
  curl -sS -X POST -H "X-Ingest-Secret: local-dev-secret" \
    "http://localhost:5199/api/ingest/run?date=$d" | tee -a backfill.log
  echo
  d=$(date -j -v+1d -f "%Y-%m-%d" "$d" +%Y-%m-%d)   # macOS date
done
```

Weekends and holidays return `{"status":"no-data"}` and are skipped without sealing anything.

### What to expect

- **Time:** hours, not minutes. The cost is not I/O — it is the indicator recompute, which runs
  over each security's *entire* history on every day ingested.
- **Rows:** ~2,000 equities per file × ~1,250 days. Far more than the NIFTY 50 universe, because
  the bhavcopy carries the whole market. That is fine and it is the point: the delisted members
  you need for §7 are in there.

### The one thing to watch

The current recompute is **O(history) per day ingested**, so a linear backfill is
O(days²) overall. Acceptable once; not acceptable nightly, and `docs/adr/0006` makes incremental
recompute a hard requirement for the F1 CPU budget. If the backfill is unbearably slow, the fix is
to bulk-load all bars first and recompute indicators **once** at the end, rather than per day.

## Step 5 — verify before trusting it

```bash
docker compose exec -T sql /opt/mssql-tools18/bin/sqlcmd \
  -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -C -d MarketEye -Q "
SET NOCOUNT ON;
SELECT COUNT(*) AS Securities FROM dbo.Securities;
SELECT COUNT(*) AS Bars, MIN(Date) AS First, MAX(Date) AS Last FROM dbo.PriceBars;
SELECT COUNT(*) AS SealedSnapshots FROM dbo.DataSnapshots WHERE SealedAt IS NOT NULL;
SELECT COUNT(*) AS Delisted FROM dbo.Securities WHERE IsActive = 0;
SELECT TOP 5 Source, Status, Error FROM dbo.IngestionRuns WHERE Status = 'Failed';"
```

**`Delisted` must not be zero.** If every security is still active after five years of data, the
survivorship-free property you are relying on is not actually there — and every backtest built on
it will be wrong in a flattering direction. Investigate before going further.

## Step 6 — the §12 reconciliation

§12 requires splits and dividends verified against a hand-checked sample of **20 securities**
before Phase 1 is done. Pick 20 with known corporate actions in the period, compare `AdjClose`
against a second source, and record the result. This is a listed exit criterion, not optional
diligence.

Note that corporate actions are **not ingested yet** — the tables and adjustment math exist and are
tested, but nothing populates `CorporateActions`. Until they are, `AdjClose` equals raw `Close` and
this reconciliation will fail by design.
