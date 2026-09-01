# ADR-0001: SQL Server over PostgreSQL

**Status:** Accepted · **Date:** 2026-09-01 · **Phase:** 0

## Context

Sift needs point-in-time correctness on restated fundamentals and fast analytical scans over
millions of daily price bars. PostgreSQL is the more common default and brings pgvector.

## Decision

SQL Server (Developer Edition locally, Azure SQL in production).

## Why

**Temporal tables are built in.** `SYSTEM_VERSIONING = ON` plus `FOR SYSTEM_TIME AS OF` gives
restatement history as a first-class feature. In PostgreSQL this is application-level work —
trigger-maintained history tables, or an extension — and it is the single property that makes
every backtest trustworthy. Correctness machinery is the wrong place to hand-roll.

**Clustered columnstore for the scan path.** `PriceBars` and `Indicators` are wide, append-only
and scanned analytically. PostgreSQL has no native equivalent in core.

**EF Core supports both directly.** `.IsTemporal()` maps the temporal configuration without
raw SQL, so the schema stays in migrations rather than in scripts.

## Cost accepted

Losing pgvector. This costs nothing today: `PLAN.md` §14 rejects vector search and RAG for v1,
and the AI path resolves concepts against a small controlled vocabulary table — a lookup, not a
similarity search. If semantic search over a large concept space is ever needed, this decision
gets revisited rather than worked around.

Also accepted: no arm64 SQL Server image, so local containers run emulated on Apple Silicon.
Fine for correctness work; invalid as a benchmark surface (see `DEPENDENCIES.md`).
