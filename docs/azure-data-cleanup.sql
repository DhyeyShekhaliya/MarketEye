-- Azure data cleanup: remove the ragged pre-2025-09 bars left by the crashed five-year attempts.
--
-- Why this matters. PLAN.md §10 now states the deployed environment holds ONE year. Azure actually
-- holds a complete 2025-09 -> 2026-09 window plus a partial, uneven 2021-2025 remnant from
-- backfills that failed on the duplicate-ticker bug.
--
-- That ragged tail is not merely untidy: DelistingDetector marks a security inactive when its last
-- bar is more than 60 sessions before the dataset end. A security whose ONLY bars come from a
-- half-finished 2022 range therefore looks like it stopped trading in 2022 and gets a fabricated
-- DelistedDate -- while it is still trading today.
--
-- §7 prices a delisting exit off that date. A fabricated one puts a fake exit into every backtest
-- touching that security, and nothing in the results looks wrong.
--
-- Run in: Azure portal -> SQL database -> Query editor.

SET QUOTED_IDENTIFIER ON;
SET NOCOUNT ON;

-- 1. What is actually there.
SELECT MIN(Date) AS FirstBar, MAX(Date) AS LastBar, COUNT(*) AS TotalBars FROM dbo.PriceBars;
SELECT COUNT(*) AS BarsBeforeWindow FROM dbo.PriceBars WHERE Date < '2025-09-01';
SELECT COUNT(*) AS CurrentlyMarkedDelisted FROM dbo.Securities WHERE IsActive = 0;

-- 2. Drop everything outside the stated one-year window.
--    Indicators first: they are derived from bars and would otherwise be orphaned.
DELETE FROM dbo.Indicators WHERE Date < '2025-09-01';
DELETE FROM dbo.PriceBars  WHERE Date < '2025-09-01';

-- 3. Clear delistings, all of which were inferred from the ragged data.
--    Re-running the backfill recomputes them against the clean window.
UPDATE dbo.Securities
SET IsActive = 1, DelistedDate = NULL, DelistingReason = NULL
WHERE IsActive = 0;

-- 4. Securities with no bars left at all -- created by a partial run and never traded in the
--    retained window. Delete only if nothing references them.
DELETE s FROM dbo.Securities s
WHERE NOT EXISTS (SELECT 1 FROM dbo.PriceBars pb WHERE pb.SecurityId = s.Id)
  AND NOT EXISTS (SELECT 1 FROM dbo.Fundamentals f WHERE f.SecurityId = s.Id)
  AND NOT EXISTS (SELECT 1 FROM dbo.CorporateActions ca WHERE ca.SecurityId = s.Id);

-- 5. Confirm.
SELECT MIN(Date) AS FirstBar, MAX(Date) AS LastBar, COUNT(*) AS TotalBars FROM dbo.PriceBars;
SELECT COUNT(*) AS Securities FROM dbo.Securities;

-- 6. Then re-run the backfill for the same window so pass 3 recomputes delistings honestly:
--
--    curl -sS -X POST -H "X-Ingest-Secret: local-dev-secret" \
--      "http://localhost:5292/api/ingest/backfill?from=2025-09-01&to=$(date +%Y-%m-%d)"
--
-- It will be fast: the MERGE finds the bars already present and only the derivation passes do work.
--
-- EXPECT delistedDetected TO FALL, probably a long way. Over a one-year window a company that
-- delisted in 2022 has no bars at all and is simply absent, rather than detectably delisted. That
-- is the honest number for this dataset, and §7's survivorship guarantee is correspondingly weaker
-- on Azure than on the full five years held locally.
