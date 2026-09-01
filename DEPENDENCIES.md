# Dependencies

Toolchain and NuGet packages downloaded ahead of Phase 0. Nothing here is scaffolding — no
projects were created in this repo. These are the versions a Phase 0 scaffold should pin.

Resolved 2026-09-01 on macOS 15.7.9 / arm64.

## Toolchain

| Item | Version | Location |
|---|---|---|
| .NET SDK | 9.0.317 | `~/.dotnet` (via `dotnet-install.sh`, no sudo) |
| `dotnet-ef` | 9.0.19 | `~/.dotnet/tools` — pinned to 9.x; the default 10.x needs a .NET 10 runtime |
| Docker Desktop | 4.89.0 (engine 29.7.2) | `/Applications/Docker.app`, CLI via `~/.docker/bin` |
| Docker Compose | v5.5.0 | bundled cli-plugin |
| `mcr.microsoft.com/mssql/server` | 2022-latest (CU26, 16.0.4265.3) | pulled `linux/amd64`, 2.34 GB on disk |

`~/.zshrc` now exports `DOTNET_ROOT="$HOME/.dotnet"` and prepends it plus `tools` to `PATH`.
`DOTNET_ROOT` is not optional: without it the `dotnet ef` apphost fails with
`Failed to resolve libhostfxr.dylib`. Previous file saved as `~/.zshrc.bak.pre-dotnet`.

## Packages (all restored and build-verified)

**Sift.Domain** — none. Zero dependencies, per §2.

**Sift.Application**
| Package | Version |
|---|---|
| Microsoft.Extensions.Logging.Abstractions | 9.0.19 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 9.0.19 |

**Sift.Infrastructure**
| Package | Version | Why |
|---|---|---|
| Microsoft.EntityFrameworkCore.SqlServer | 9.0.19 | CRUD/config path (§3) |
| Microsoft.EntityFrameworkCore.Relational | 9.0.19 | temporal-table mapping (§4.1) |
| Microsoft.EntityFrameworkCore.Design | 9.0.19 | migrations |
| Microsoft.Data.SqlClient | 6.1.6 | `SqlBulkCopy` ingest path (§3) |
| Dapper | 2.1.79 | hot-path reads (§3) |
| Microsoft.Extensions.Http.Resilience | 9.10.0 | provider backoff + retry (Phase 1) |
| System.Threading.RateLimiting | 9.0.19 | provider rate limiting (Phase 1) |
| Microsoft.Extensions.Options.ConfigurationExtensions | 9.0.19 | |

**Sift.Ai**
| Package | Version | Why |
|---|---|---|
| Azure.AI.OpenAI | 2.1.0 | Azure hosting path |
| OpenAI | 2.13.0 | structured outputs (§5.1) |
| Microsoft.Extensions.Caching.Hybrid | 9.10.0 | two-level cache (§5.5) |
| System.Threading.RateLimiting | 9.0.19 | §5.4 |
| Microsoft.Extensions.Logging.Abstractions | **10.0.11** | forced — see note below |

**Sift.Ingestion**
| Package | Version |
|---|---|
| Microsoft.Extensions.Hosting | 9.0.19 |
| Microsoft.Extensions.Http.Resilience | 9.10.0 |

**Sift.Api**
| Package | Version |
|---|---|
| Microsoft.AspNetCore.OpenApi | 9.0.19 |
| Serilog.AspNetCore | 9.0.0 |
| Serilog.Sinks.Console | 6.1.1 |
| Serilog.Sinks.File | 6.0.0 |
| Serilog.Settings.Configuration | 9.0.0 |
| Microsoft.ApplicationInsights.AspNetCore | 2.23.0 |
| AspNetCore.HealthChecks.SqlServer | 9.0.0 |
| Microsoft.AspNetCore.Diagnostics.HealthChecks | 2.2.0 |
| Microsoft.EntityFrameworkCore.Design | 9.0.19 |

**Sift.Web** — Serilog.AspNetCore 9.0.0. Blazor Server itself is a framework reference.

**All four test projects**
| Package | Version |
|---|---|
| Microsoft.NET.Test.Sdk | 17.14.1 |
| xunit.v3 | 4.0.0 |
| xunit.runner.visualstudio | 3.1.5 |
| FluentAssertions | 7.2.2 |
| NSubstitute | 5.3.0 |
| coverlet.collector | 6.0.4 |

**Sift.IntegrationTests and Sift.BacktestTests** additionally: Testcontainers.MsSql 4.14.0,
Microsoft.EntityFrameworkCore.SqlServer 9.0.19, Dapper 2.1.79.

## Things to know before Phase 0

**Logging.Abstractions is split 9.x / 10.x.** `OpenAI` 2.13.0 → `System.ClientModel` 1.14.0 →
`Microsoft.Extensions.Logging.Abstractions >= 10.0.3`. Pinning `Sift.Ai` to 9.x is a hard NU1605
downgrade error, not a warning. Either let `Sift.Ai` take 10.x (done here) or move the whole
solution to the 10.x Extensions line. Central Package Management would make this one decision
instead of nine.

**FluentAssertions is pinned to 7.x deliberately.** Version 8+ moved to a paid Xceed licence for
commercial use. 7.2.2 is the last Apache-2.0 release. If that licence is unacceptable even at 7.x,
AwesomeAssertions (a 7.x fork) and Shouldly are drop-in-ish alternatives.

**.NET 9 is past end of support** (STS, ended May 2026). PLAN.md §3 specifies it, so that is what
is installed. Moving to .NET 10 LTS is a §3 amendment, not a scaffolding decision — but the
`Sift.Ai` conflict above is the first sign of the ecosystem moving on.

**The default Docker socket is enabled** (`EnableDefaultDockerSocket: true`), so
`/var/run/docker.sock` exists as a symlink to `~/.docker/run/docker.sock`. Testcontainers finds it
without configuration — verified with `DOCKER_HOST` unset. No `DOCKER_HOST` override is needed on
this machine, and none is set; if you ever see "Docker is not available" from Testcontainers, check
that this setting is still on before adding one back.

**SQL Server runs emulated.** The engine is `aarch64`; there is no arm64 SQL Server image, so the
container reports `x86_64` and runs under emulation. This is fine for correctness work — the §8
synthetic market, the bias guards, the integration suite — but it is **not a valid surface for the
§9 benchmark numbers**, which the non-negotiables require to be measured. Those must come off the
Azure SQL target.

**Docker does not auto-start** (`AutoStart: false`). Launch Docker.app before any test run or
`docker compose up`.

## Local dev database

A persistent SQL Server container is running. Testcontainers does **not** use this — it starts its
own throwaway containers — so this is for the app, EF migrations, and manual inspection.

| | |
|---|---|
| Container | `sift-sql` |
| Image | `mcr.microsoft.com/mssql/server:2022-latest`, `linux/amd64` |
| Edition | Developer (§3) |
| Port | `localhost,1433` |
| Volume | `sift-sqldata` → `/var/opt/mssql` (survives restarts) |
| Restart policy | `unless-stopped` |

```
Server=localhost,1433;User Id=sa;Password=Sift_Dev_Local_2026!;TrustServerCertificate=True;Encrypt=True
```

Verified from the macOS host: Developer Edition confirmed, a system-versioned temporal table created
and reporting `temporal_type = 2`, round trip in 331 ms.

This password is local-dev-only and deliberately disposable. When Phase 0 writes the compose file it
should read the password from a `.env` file or user-secrets rather than inlining it, so the same
compose file works for anyone. `TrustServerCertificate=True` is required because the container uses
a self-signed cert — do not carry that flag into the Azure SQL connection string.

Recreate it with:

```bash
docker run -d --name sift-sql --platform linux/amd64 \
  -e ACCEPT_EULA=Y -e 'MSSQL_SA_PASSWORD=Sift_Dev_Local_2026!' -e MSSQL_PID=Developer \
  -p 1433:1433 -v sift-sqldata:/var/opt/mssql --restart unless-stopped \
  mcr.microsoft.com/mssql/server:2022-latest
```

## Verified end to end

A Testcontainers smoke test was run against the real image — 5 consecutive passes — covering the
three things Phase 0 and Phase 1 actually depend on:

- SQL Server 2022 container starts and answers a query through Dapper
- a `SYSTEM_VERSIONING = ON` temporal table is created and reports `temporal_type = 2` (§4.1)
- `FOR SYSTEM_TIME AS OF` combined with `ReportedDate <=` executes (§4.1)
- `SqlBulkCopy` writes 1,000 rows to that table (§3)

Container start to first query is roughly 10–12 s under emulation. Budget for that in §8 — a suite
that spins up a container per fixture will be slow.

Two transient `Login failed for user 'sa'` failures occurred on the first runs after the image
pull, then never recurred across five clean runs. SQL Server can accept TCP connections shortly
before the `sa` login is ready, and emulated startup widens that window. If it resurfaces in CI,
the fix is a wait strategy on a successful `sa` query, not a retry count.

### API notes for §8

- `new MsSqlBuilder()` is **obsolete** in Testcontainers 4.14 — pass the image to the constructor:
  `new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest")`.
- xUnit v3 removed the `Xunit.Abstractions` namespace. `ITestOutputHelper` now lives in `Xunit`.
- xUnit v3's `xUnit1051` analyzer wants `TestContext.Current.CancellationToken` passed to every
  async call that accepts one. Worth adopting from the first test rather than retrofitting.

### The verified test

```csharp
var ct = TestContext.Current.CancellationToken;

await using var sql = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
await sql.StartAsync(ct);

await using var conn = new SqlConnection(sql.GetConnectionString());
await conn.OpenAsync(ct);

await conn.ExecuteAsync(@"
    CREATE TABLE dbo.TemporalProbe(
        Id INT NOT NULL PRIMARY KEY,
        ReportedDate DATE NOT NULL,
        ValidFrom DATETIME2 GENERATED ALWAYS AS ROW START NOT NULL,
        ValidTo   DATETIME2 GENERATED ALWAYS AS ROW END   NOT NULL,
        PERIOD FOR SYSTEM_TIME (ValidFrom, ValidTo)
    ) WITH (SYSTEM_VERSIONING = ON (HISTORY_TABLE = dbo.TemporalProbeHistory));");

var temporalType = await conn.QuerySingleAsync<int>(
    "SELECT temporal_type FROM sys.tables WHERE object_id = OBJECT_ID('dbo.TemporalProbe')");
// 2 = SYSTEM_VERSIONED_TEMPORAL_TABLE
```

## Reproducing

Everything is in the machine-wide caches (`~/.nuget/packages`, 194 packages, 1.1 GB), so a Phase 0
scaffold restores offline. To re-verify:

```bash
dotnet nuget locals global-packages --list
dotnet restore Sift.sln
docker images                 # mssql/server:2022-latest should be present
docker info                   # daemon reachable; launch Docker.app first
```
