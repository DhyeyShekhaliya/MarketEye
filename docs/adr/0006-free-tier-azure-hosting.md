# ADR-0006: Free-tier Azure hosting, and what it costs

**Status:** Accepted, with one forced change · **Date:** 2026-09-01 · **Phase:** 1

## Context

`PLAN.md` §3 specifies "Azure App Service + Azure SQL" and §10 ends each phase deployed. The
budget decision is to use free tiers: **App Service F1** and the **Azure SQL free offer**.

## The database side is fine

The Azure SQL free offer is a General Purpose **serverless** database: 100,000 vCore-seconds of
compute per month (~28 hours at 1 vCore), 32 GB data, 32 GB backup, up to 10 databases per
subscription, for the lifetime of the subscription.

General Purpose supports both features the design depends on:

- **Temporal tables** (§4.1) — available across Azure SQL tiers.
- **Clustered columnstore** (§4.2) — supported on vCore General Purpose. It is the *DTU* Basic and
  Standard S0-S2 tiers that exclude columnstore, which this offer is not.

Serverless auto-pause is a good match for a nightly-batch workload: the database is idle most of
the day and costs nothing while paused. Expect a cold-start delay on the first query after a pause,
which matters for §9 measurement but not for correctness.

## The App Service side forces a change

**App Service F1 cannot run the nightly ingestion job.** This is not a tuning problem:

- **Always On is unavailable on Free and Shared tiers.** The app unloads after roughly 20 minutes
  without traffic, and in-process background work stops when it unloads.
- **60 CPU-minutes per day**, shared across every Free app in the same region and subscription.
  Ingestion plus indicator computation would consume that and trip
  `Error 403 - Web app is stopped (Quota exceeded)` until the quota resets at midnight UTC.

§3 chose `BackgroundService` + `PeriodicTimer` for jobs. On F1 that combination silently does
nothing: the timer exists only while the process is loaded, and nothing keeps it loaded. Phase 1's
exit criterion — "nightly job unattended for a week" — is **unachievable** as specified.

### Decision: an external scheduler pings a protected endpoint

A **scheduled GitHub Actions workflow** calls a protected ingestion endpoint on a cron. The repo
already exists, the runner minutes are free, and it adds **no Azure resource at all** — which fits
a portfolio project better than standing up a Function App for one nightly HTTP call.

An Azure Function on the Consumption plan is the alternative if the trigger ever needs to live
inside Azure. It was the first recommendation here and was downgraded deliberately: it is more
infrastructure than the problem requires.

Note what this does *not* require: no queue, no worker service, no Hangfire. One cron, one HTTP
call, and the ingestion runs inside the request. That works precisely because the workload is
small — see the budget below.

This satisfies §14's rule that infrastructure is added only when something concrete breaks and the
ADR names what broke. What broke: **F1 has no Always On, so an in-process scheduler never fires.**
The ingestion *logic* stays in `MarketEye.Ingestion` and remains a plain class; only the trigger
moves. If the app later runs on B1 or higher, the trigger can move back to `BackgroundService`
without touching the ingestion code.

## Staying inside F1 — the budget this assumes

F1 is workable because the workload is genuinely small. These are the constraints that keep it
that way; breaking one is the signal to reconsider the tier, not to optimise harder.

| Constraint | Budget |
|---|---|
| Universe | ~60-80 securities (NIFTY 50 + delisted members) |
| Price history | ~100k daily bars total for 5 years — trivially inside 32 GB |
| Nightly ingest | One bhavcopy file, a few thousand rows, seconds of CPU |
| Indicators | **Incremental only.** Recomputing all history nightly is what would blow the CPU quota |
| Logging | Console sink only in production. F1 has ~1 GB of storage and a rolling file sink will eat it |
| Concurrency | Single-digit concurrent visitors |

The one that matters most is incremental indicators. Everything else has an order of magnitude of
headroom; a full recompute does not.

## Blazor Server on F1 — accepted, with eyes open

§3 already flags Blazor Server's cost: one persistent circuit per visitor. F1 is the worst case for
it — a 20-minute idle unload drops every live circuit, and reconnection after a cold start is
visible to the user. Acceptable for a portfolio deployment with intermittent traffic. §12 already
records knowing the WASM alternative as the answer if this becomes a real problem.

Expect a visible cold start — roughly 10-20 seconds — on the first request after an idle period.
For a project whose audience arrives one reviewer at a time, that is a wart rather than a defect.

## §9 benchmarks now have no valid surface

This is the sharpest consequence, and it compounds two existing problems.

The non-negotiables require §9's numbers to be **measured**, never estimated. There are now three
candidate surfaces and none of them qualifies:

| Surface | Why it fails |
|---|---|
| Local Docker | SQL Server runs emulated on Apple Silicon (no arm64 image) — see `DEPENDENCIES.md` |
| App Service F1 | Shared infrastructure, 60 CPU-min/day, cold starts — the numbers would be scheduling noise |
| Azure SQL free | Serverless auto-pause and a vCore-second budget make repeated timed runs both slow and costly against the quota |

Compounding it, ADR-0005 already records that a NIFTY 50 universe is ~10% of the 500 securities
§9's definition names, so the benchmark is not measurable *as written* regardless of hardware.

**Consequence:** §9 cannot be honoured on free tiers. The honest options are to restate §9 for the
actual universe and hardware and measure there, to rent a paid tier briefly for one benchmark run
and record the exact SKU alongside the numbers, or to state plainly in the README that benchmarks
are outstanding. What is not acceptable is publishing an estimate, which the project's own
non-negotiables forbid.
