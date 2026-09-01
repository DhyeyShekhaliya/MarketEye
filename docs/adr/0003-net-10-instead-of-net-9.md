# ADR-0003: Target .NET 10 LTS instead of .NET 9

**Status:** Accepted · **Date:** 2026-09-01 · **Phase:** 0

## Context

`PLAN.md` §3 specified .NET 9. `PLAN.md` §14 requires that infrastructure and platform changes
name what concretely broke, rather than being adopted because they are newer.

## Decision

Target `net10.0` across all eleven projects. §3 amended.

## What actually broke

Warming the NuGet cache against the §2 layout produced a hard `NU1605` **error**, not a warning:

```
OpenAI 2.13.0 → System.ClientModel 1.14.0 → Microsoft.Extensions.Logging.Abstractions >= 10.0.3
```

`MarketEye.Ai` could not resolve on the 9.x `Microsoft.Extensions.*` line. The only ways forward on
.NET 9 were to pin `MarketEye.Ai` alone to the 10.x Extensions packages — leaving one project on a
different dependency line from the other ten — or to hold the AI SDK back at an older version.

## Supporting reason

.NET 9 is STS, in maintenance, EOL **2026-11-10**. `PLAN.md` §10 budgets 3–5 months, so the
project outlives its own runtime's support window. .NET 10 is LTS and supported to 2028-11-14.

*(Correcting the record: this was initially described as already being out of support in May 2026.
That was wrong — the release index shows maintenance until November 2026. The conclusion held for
the dependency reason above, but the support-date premise was inaccurate as first stated.)*

## Cost accepted

A deviation from `PLAN.md` as originally written, recorded in §14. Central Package Management was
adopted at the same time so that a future framework-line move is one edit rather than eleven.
