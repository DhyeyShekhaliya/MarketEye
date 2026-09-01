# Azure deployment runbook (Phase 1, item 3)

Deploys to **App Service F1 (free)** and the **Azure SQL free offer**. Constraints and their
consequences are argued in `docs/adr/0006`; this is the procedure.

Everything here needs your subscription, so these are steps for you to run.

## What you are creating

| Resource | SKU | Cost |
|---|---|---|
| App Service plan | F1 (Free) | ₹0 |
| App Service (API) | on that plan | ₹0 |
| Azure SQL Database | Free offer — GP serverless, 100k vCore-sec/mo, 32 GB | ₹0 |

One plan hosts one app. **Deploy the API only.** Blazor Server needs a persistent circuit per
visitor and F1 unloads after ~20 minutes idle; running both on one free plan means they compete for
the same 60 CPU-minutes/day. Run the Web project locally against the deployed API until you outgrow
free tier.

## Step 1 — create the resources

You can do this in the portal (below) or the CLI (further down). The portal is easier to get
right the first time, because the two settings that actually matter — the **F1** tier and the SQL
**free offer** — are both easy to miss and neither can be changed later without recreating.

### 1a. Resource group

1. [portal.azure.com](https://portal.azure.com) → search **Resource groups** → **+ Create**
2. **Subscription:** yours · **Name:** `marketeye-rg`
3. **Region:** **Central India**
4. **Review + create** → **Create**

Region matters a little: the data is Indian and the nightly job fetches from NSE, so keeping
compute near the source shortens that hop.

### 1b. App Service (the API)

1. Search **App Services** → **+ Create** → **Web App**
2. **Basics:**
   - **Resource group:** `marketeye-rg`
   - **Name:** `marketeye-api` — this becomes `marketeye-api.azurewebsites.net` and must be
     globally unique. Add a suffix if it is taken.
   - **Publish:** Code
   - **Runtime stack:** **.NET 10 (LTS)**
   - **Operating System:** Linux
   - **Region:** Central India
3. **Pricing plan — this is the step people miss.** The default is a paid tier. Click
   **Explore pricing plans** (or the "Change size" link), open the **Dev/Test** tab, choose
   **F1 — Free**, and **Select**. Confirm the summary reads **Free F1** before continuing.
4. **Deployment** tab: leave GitHub Actions **disabled**. Deployment is a zip push in step 4;
   wiring CI here creates a second, competing pipeline.
5. **Monitoring** tab: Application Insights **No**. It is optional in the app, and enabling it
   attaches a billable resource for a project that is deliberately free-tier.
6. **Review + create** → **Create**

> **One F1 plan hosts one app.** Do not create a second Web App for `MarketEye.Web` on this plan.
> Blazor Server holds a persistent circuit per visitor, and both apps would compete for the same
> 60 CPU-minutes/day. Run the Web project locally against the deployed API.

### 1c. Azure SQL Database (free offer)

The free grant is applied **at creation only**. A database created without it cannot be converted
later — you would have to delete and recreate.

1. Search **SQL databases** → **+ Create**
2. **Basics:**
   - **Resource group:** `marketeye-rg`
   - **Database name:** `MarketEye`
   - **Server:** **Create new** →
     - **Server name:** `marketeye-sql-<something-unique>`
     - **Location:** Central India
     - **Authentication:** **Use SQL authentication**
     - Set an admin login and password — **write these down now**, you need them for migrations
       in step 2 and they cannot be recovered later.
   - **Want to use SQL elastic pool?** No
   - **Workload environment:** Development
3. **Compute + storage** → **Configure database:**
   - **Service tier:** **General Purpose** → **Serverless**
   - Look for the **Apply free offer** / "free database" option and **tick it**
     (100,000 vCore-seconds/month, 32 GB). If you do not see it, check the banner at the top of
     the blade — Azure surfaces it as a prompt rather than a checkbox in some views.
   - **Auto-pause delay:** 1 hour
   - **Apply**
4. **Backup storage redundancy:** **Locally-redundant** — the cheapest, and this is dev data you
   can re-ingest from the archive at any time.
5. **Networking** tab:
   - **Connectivity method:** Public endpoint
   - **Allow Azure services and resources to access this server:** **Yes** — without this the App
     Service cannot reach the database
   - **Add current client IP address:** **Yes** — without this your migrations in step 2 fail
6. **Review + create** → **Create**. Provisioning takes a few minutes.

### Verify before moving on

On the SQL database **Overview** blade, confirm the tier reads **General Purpose: Serverless** and
that the free-offer banner or "Free" label is present. Two reasons this matters:

- **Columnstore.** §4.2 needs clustered columnstore, which vCore General Purpose supports but the
  DTU **Basic** and **Standard S0–S2** tiers do not. Landing on a DTU tier means the migration in
  step 2 will appear to succeed while `CCI_PriceBars` silently does not exist.
- **The free grant.** If it was not applied at creation, this database is billable from now on.

Then grab the connection string: **Overview → Show database connection strings → ADO.NET**. Paste
your admin password into the `Password=` placeholder — the portal does not fill it in.

### CLI equivalent

If you would rather script it:

```bash
az login
az group create --name marketeye-rg --location centralindia

az appservice plan create \
  --name marketeye-plan --resource-group marketeye-rg \
  --sku F1 --is-linux

az webapp create \
  --name marketeye-api --resource-group marketeye-rg \
  --plan marketeye-plan --runtime "DOTNETCORE:10.0"
```

Pick `centralindia` — the data is Indian and latency to NSE matters for the nightly fetch.
`marketeye-api` must be globally unique; if it is taken, add a suffix.

The SQL database still has to be created in the portal — the free grant is applied by that
creation flow, not by a CLI flag. Follow **1c** above.

## Step 2 — apply migrations from your machine

**Do not migrate on startup in production.** The API only does this in Development, deliberately:
startup migration races across scaled-out instances and offers no rollback path.

```bash
export AZURE_SQL="Server=tcp:<server>.database.windows.net,1433;Database=MarketEye;User ID=<user>;Password=<pw>;Encrypt=True;TrustServerCertificate=False;"

ConnectionStrings__MarketEye="$AZURE_SQL" \
  dotnet ef database update \
  --project src/MarketEye.Infrastructure --startup-project src/MarketEye.Api
```

Then confirm the two things that must survive the trip to Azure SQL:

```sql
SELECT name, temporal_type FROM sys.tables WHERE name LIKE 'Fundamentals%';
-- expect Fundamentals = 2, FundamentalsHistory = 1

SELECT t.name, i.type_desc FROM sys.indexes i
JOIN sys.tables t ON t.object_id = i.object_id
WHERE i.type_desc LIKE '%COLUMNSTORE%';
-- expect CCI_PriceBars and CCI_Indicators
```

If the columnstore indexes are missing, the database is not on vCore General Purpose — the DTU
Basic and Standard S0–S2 tiers exclude columnstore. Recreate it on the right tier.

## Step 3 — configure the app

Everything here is done **in the Azure portal**, in your browser. Nothing runs on your Mac for this
step. You are telling the deployed app three things: where its database is, what secret guards the
ingestion endpoint, and which API key to use for fundamentals.

### 3a. Generate the ingestion secret

This one command runs in **your Mac's Terminal**, because you need a random value and you will
paste it into two places:

```bash
openssl rand -hex 32
```

Copy the 64-character output somewhere temporary. It goes into Azure (3c) and into GitHub (step 5),
and the two must match exactly or the nightly job returns 401.

### 3b. Add the database connection string

1. [portal.azure.com](https://portal.azure.com) → **App Services** → click **marketeye-api**
2. Left sidebar → **Settings** → **Environment variables**
3. Open the **Connection strings** tab (not "App settings" — a different tab)
4. **+ Add**
   - **Name:** `MarketEye` — exactly this, no prefix. The app looks up
     `ConnectionStrings:MarketEye`, and App Service adds the `ConnectionStrings:` part for you.
   - **Value:** the same ADO.NET string you used for migrations, with your real password
   - **Type:** **SQLAzure**
5. **Apply**

> The `Type` matters. App Service prefixes the environment variable differently per type
> (`SQLAZURECONNSTR_` for SQLAzure, `SQLCONNSTR_` for SQLServer), and .NET only resolves
> `ConnectionStrings:MarketEye` from the right one. The wrong type produces a "connection string
> not configured" error at startup that looks like the value is missing entirely.

### 3c. Add the app settings

Same page, **App settings** tab → **+ Add** for each row:

| Name | Value | Why |
|---|---|---|
| `Ingestion__TriggerSecret` | the value from 3a | Guards `/api/ingest/run`. Without it that endpoint refuses to run at all, rather than running unprotected. |
| `Provider__IndianApi__ApiKey` | your indianapi.in key | Fundamentals (§4.1) |
| `ASPNETCORE_ENVIRONMENT` | `Production` | Turns **off** startup migrations. Production migrates out of band — startup migration races across instances. |
| `Serilog__WriteTo__0__Name` | `Console` | F1 has ~1 GB of storage and the default file sink will fill it |

**Double underscores, not colons.** `Ingestion__TriggerSecret` maps to `Ingestion:TriggerSecret`.
Colons do not work as environment variable names on Linux, which is what your F1 plan runs.

Leave `Ingestion__ArchivePath` **unset**. Unset means the app fetches from NSE directly, which is
right for a one-file nightly job. Setting it would point Azure at a local archive directory that
does not exist there.

Click **Apply**, then **Confirm**. The app restarts — expect ~30 seconds of downtime.

### 3d. Verify the settings took

Still in the portal, App Service → **Overview** → note the **Default domain**, then from your Mac:

```bash
curl -sS https://marketeye-api.azurewebsites.net/health
```

Expect `Healthy`. The first request after a restart or idle period takes **10–20 seconds** — F1
cold start plus a serverless database resume. A timeout on the first try is normal; retry once
before concluding anything is wrong.

If it returns `Unhealthy`, the SQL health check is failing. Most likely causes, in order:

| Cause | Fix |
|---|---|
| Connection string type is wrong | 3b — must be **SQLAzure** |
| App Service cannot reach SQL | SQL **server** → Networking → **Allow Azure services** = Yes |
| Password wrong in the string | Re-copy from the portal and replace `{your_password}` |

Then check the secret is wired, without triggering an actual ingest:

```bash
curl -sS -X POST https://marketeye-api.azurewebsites.net/api/ingest/trigger
```

Expect **401 Unauthorized** — that is the correct answer for a request with no secret header. A
**503** means `Ingestion__TriggerSecret` did not get set, and the app is refusing to expose an
unprotected write endpoint. A **200** would mean the guard is not working at all.

### What you have NOT done yet

The app is configured but **no code is deployed** — App Service is still serving its default
placeholder page. `/health` will not answer until step 4. If you want to confirm configuration
before deploying, the portal's **Environment variables** page is the source of truth; the curl
checks above only become meaningful after step 4.

## Step 4 — deploy

```bash
dotnet publish src/MarketEye.Api -c Release -o ./publish
cd publish && zip -r ../api.zip . && cd ..

az webapp deploy \
  --name marketeye-api --resource-group marketeye-rg \
  --src-path api.zip --type zip
```

Verify:

```bash
curl -sS https://marketeye-api.azurewebsites.net/health
curl -sS https://marketeye-api.azurewebsites.net/api/concepts | head -c 300
```

The first request after idle takes **10–20 seconds** — F1 cold start plus a serverless database
resume. That is expected, not a fault.

## Step 5 — wire the cron

GitHub → repo → Settings → Secrets and variables → Actions:

| Secret | Value |
|---|---|
| `MARKETEYE_BASE_URL` | `https://marketeye-api.azurewebsites.net` |
| `MARKETEYE_INGEST_SECRET` | the `$SECRET` from step 3 |

Then trigger `.github/workflows/nightly-ingest.yml` manually once (Actions → Nightly ingest → Run
workflow) rather than waiting for 19:30 UTC. The workflow already retries three times, because a
cold start can exceed the first request's patience.

Expect `{"status":"sealed",...}` on a trading day, or `{"status":"no-data",...}` on a holiday.

## Step 6 — the unattended week

§10's Phase 1 exit needs the nightly job running **unattended for a week**. Once the cron is live,
that is calendar time, not work. Check daily:

```sql
SELECT TOP 10 StartedAt, Status, RecordsWritten, LEFT(Error, 200) AS Error
FROM dbo.IngestionRuns ORDER BY StartedAt DESC;
```

Watch for two things specifically:

**vCore-second burn.** 100,000/month is ~28 hours of 1 vCore. The nightly recompute is the main
consumer, and if it is still O(history) per run it will grow every night — the backfill runbook
flags the same issue. If the monthly grant is being consumed faster than roughly 1/30th per day,
fix incremental recompute before it becomes a surprise bill.

**Days with `status: no-data` that were not holidays.** That means the NSE fetch failed silently
rather than the market being closed — check the NSE archive URL shape has not changed again.

## Rollback

`az webapp deployment slot` is unavailable on F1, so there is no slot swap. Rollback is
redeploying the previous zip. Keep the last known-good `api.zip`.
