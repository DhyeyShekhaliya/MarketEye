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

```bash
SECRET=$(openssl rand -hex 32)

az webapp config connection-string set \
  --name marketeye-api --resource-group marketeye-rg \
  --connection-string-type SQLAzure \
  --settings MarketEye="$AZURE_SQL"

az webapp config appsettings set \
  --name marketeye-api --resource-group marketeye-rg \
  --settings Ingestion__TriggerSecret="$SECRET" \
             ASPNETCORE_ENVIRONMENT=Production \
             Serilog__WriteTo__0__Name=Console

echo "Save this for GitHub secrets: $SECRET"
```

Two things that matter on F1:

- **Console logging only.** The file sink is configured for local dev; F1 has ~1 GB of storage and
  a rolling file sink will fill it. The setting above overrides `WriteTo` to Console.
- **Leave `Ingestion:ArchivePath` unset** in Azure. Unset means the app uses `NseBhavcopyClient`
  and fetches from NSE directly, which is correct for a one-file nightly job.

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
