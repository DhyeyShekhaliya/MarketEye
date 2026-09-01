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

### Decision: move the scheduled job out of the web app

The ingestion trigger moves to a **timer-triggered Azure Function on the Consumption plan** (free
monthly grant of 1M executions and 400,000 GB-seconds), or equivalently a scheduled GitHub Actions
workflow calling a protected ingestion endpoint.

This satisfies §14's rule that infrastructure is added only when something concrete breaks and the
ADR names what broke. What broke: **F1 has no Always On, so an in-process scheduler never fires.**
The ingestion *logic* stays in `MarketEye.Ingestion` and remains a plain class; only the trigger
moves. If the app later runs on B1 or higher, the trigger can move back to `BackgroundService`
without touching the ingestion code.

## Blazor Server on F1 — accepted, with eyes open

§3 already flags Blazor Server's cost: one persistent circuit per visitor. F1 is the worst case for
it — a 20-minute idle unload drops every live circuit, and reconnection after a cold start is
visible to the user. Acceptable for a portfolio deployment with intermittent traffic. §12 already
records knowing the WASM alternative as the answer if this becomes a real problem.

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
