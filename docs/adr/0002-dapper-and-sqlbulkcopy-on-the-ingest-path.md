# ADR-0002: Dapper + SqlBulkCopy on the ingest path, EF Core everywhere else

**Status:** Accepted · **Date:** 2026-09-01 · **Phase:** 0

## Context

Nightly ingestion writes on the order of millions of price bars. The rest of the application —
strategies, snapshots, configuration, ingestion history — is ordinary low-volume CRUD.

## Decision

EF Core for CRUD and configuration. Dapper for hot-path reads, `SqlBulkCopy` for bulk inserts.
Never bulk-insert price bars through EF.

## Why

EF Core's change tracker allocates and tracks per entity. That is the right trade at hundreds of
rows and the wrong one at millions: ingestion would spend its time in the tracker rather than in
the network round trip. `SqlBulkCopy` streams rows to the server without materialising entities.

Using one tool everywhere would mean either accepting an ingestion job too slow to finish
overnight, or abandoning EF for the 95% of the codebase where its productivity is real.

## Cost accepted

Two persistence idioms in one codebase, and the boundary has to be understood rather than
guessed. It is drawn on volume: if a write is per-security-per-day, it goes through Dapper or
`SqlBulkCopy`; anything else uses EF.

Bulk-inserted rows also bypass EF's validation and change tracking, so ingestion validates
explicitly before writing rather than relying on the ORM.
