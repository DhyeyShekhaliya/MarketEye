# Azure deployment runbook — MarketEye.Web (Phase 4 follow-up)

Deploys the Blazor Server frontend to its **own App Service F1 (free)** plan, separate from
`marketeye-api`. `docs/azure-deployment-runbook.md` already explains why the two apps cannot share
a plan — Blazor Server holds a persistent circuit per visitor, and both apps would compete for the
same 60 CPU-minutes/day. This is the procedure for the second plan that runbook already told you
to create when you outgrow "run the Web project locally against the deployed API."

Everything here needs your subscription, so these are steps for you to run. It assumes
`marketeye-api` is already deployed and healthy per the other runbook — this one only adds the
frontend in front of it.

## What you are creating

| Resource | SKU | Cost |
|---|---|---|
| App Service plan (Web) | F1 (Free) | ₹0 |
| App Service (Web) | on that plan | ₹0 |

No new database, no new SQL server. `MarketEye.Web`'s only `ProjectReference` is
`MarketEye.Application` — it never touches EF Core, Dapper, or a connection string directly. Every
page's data comes from an `HttpClient` calling the already-deployed API over plain HTTP
(`Program.cs`: `builder.Services.AddHttpClient("api", ...)` reads `Api:BaseUrl` from config). That
is also why there is no CORS section below: the browser only ever talks to the Blazor Server app;
server-to-server calls to the API are not subject to a browser's same-origin policy at all.

## Step 1 — create the App Service

### 1a. App Service plan

1. [portal.azure.com](https://portal.azure.com) → **App Service plans** → **+ Create**
2. **Resource group:** `marketeye-rg` (the same one `marketeye-api` lives in)
3. **Name:** `marketeye-web-plan` — a **new** plan, not `marketeye-api`'s. Sharing a plan is the
   one thing the other runbook explicitly warns against for this exact pair of apps.
4. **Operating System:** Linux · **Region:** Central India (same region as the API — cross-region
   calls from Web to API would add latency for no reason)
5. **Pricing plan:** **Explore pricing plans** → **Dev/Test** tab → **F1 — Free** → **Select**.
   Confirm the summary reads **Free F1** before continuing — this is the step people miss, same as
   in the API runbook.
6. **Review + create** → **Create**

### 1b. App Service (the Web frontend)

1. Search **App Services** → **+ Create** → **Web App**
2. **Basics:**
   - **Resource group:** `marketeye-rg`
   - **Name:** `marketeye-web` — this becomes `https://marketeye-web-<suffix>.<region>.azurewebsites.net`
     and must be globally unique. Add a suffix if it is taken, and if you change it, update
     `app-name` in `.github/workflows/main_marketeye-web.yml` to match.
   - **Publish:** Code · **Runtime stack:** **.NET 10 (LTS)** · **Operating System:** Linux
   - **Region:** Central India
3. **Pricing plan:** select **Existing plan** → `marketeye-web-plan` (the one from 1a) rather than
   letting it create another new plan for you.
4. **Deployment** tab: leave GitHub Actions **disabled** here too — wiring it happens in Step 4,
   same reasoning as the API runbook (avoid a second, competing pipeline).
5. **Monitoring** tab: Application Insights **No**, same reasoning as the API — optional in the
   app, and it attaches a billable resource to a deliberately free-tier project.
6. **Review + create** → **Create**

### CLI equivalent

```bash
az appservice plan create \
  --name marketeye-web-plan --resource-group marketeye-rg \
  --sku F1 --is-linux

az webapp create \
  --name marketeye-web --resource-group marketeye-rg \
  --plan marketeye-web-plan --runtime "DOTNETCORE:10.0"
```

## Step 2 — no migrations to run

Nothing here. `MarketEye.Web` has no `ConnectionStrings` entry and no `MarketEye.Infrastructure`
reference — it is a pure HTTP client of the API, so there is no database step analogous to the API
runbook's Step 2, and none is needed.

## Step 3 — configure the app

All in the **Azure portal**, same as the API runbook's Step 3 — you are telling the deployed app
one thing that actually matters (where the API is) and two things that keep it well-behaved on F1.

### 3a. Point it at the API

1. **App Services** → **marketeye-web** → **Settings** → **Environment variables** → **App
   settings** tab → **+ Add**
2. **Name:** `Api__BaseUrl` — double underscore, not a colon, same reason as
   `Ingestion__TriggerSecret` in the API runbook: colons do not work as environment variable names
   on Linux.
3. **Value:** the API's real URL, e.g. `https://marketeye-api-<suffix>.<region>.azurewebsites.net`
   — no trailing slash (the app builds `new Uri(baseUrl)` and appends relative paths like
   `/api/strategies` onto it).
4. **Apply**

This is the only setting that changes behavior. Everything else below keeps the app well-behaved
on a free tier, mirroring the API runbook's own settings for the same reasons.

### 3b. The other two settings

| Name | Value | Why |
|---|---|---|
| `ASPNETCORE_ENVIRONMENT` | `Production` | Same reasoning as the API: production behavior (HSTS, generic error pages) rather than the Development pipeline |
| `Serilog__WriteTo__0__Name` | `Console` | F1 has ~1 GB of storage; the default file sink fills it, same as the API |

Click **Apply**, then **Confirm**. The app restarts — expect ~30 seconds of downtime.

### 3c. Forwarded-headers middleware — already fixed, here's why it was needed

App Service terminates TLS in front of the app and forwards the original request over plain HTTP,
tagging the real scheme in an `X-Forwarded-Proto: https` header. `MarketEye.Web`'s `Program.cs`
calls `app.UseHttpsRedirection()` (and `UseHsts()` in non-Development) — without forwarded-headers
middleware telling ASP.NET Core to trust that header, the app would see every request as plain HTTP
and redirect it to HTTPS, which Azure's frontend would then forward again as plain HTTP, forever.
The API project never hit this because it has no `UseHttpsRedirection()`/`UseHsts()` calls at all.

`Program.cs` already has this, right before `UseHttpsRedirection()`:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
});
```

Nothing to do here — just push whatever commit you have as usual before Step 4's deploy.

## Step 4 — deploy (Deployment Center + the workflow already in this repo)

Unlike the API runbook, you do not need to hand-fix a generated workflow here — a correct one,
`​.github/workflows/main_marketeye-web.yml`, is already committed, built on the exact pattern
Section 4c of the API runbook had to retrofit onto Azure's template (explicit project path, pinned
SDK version, no duplicated test run). Deployment Center still needs to run once, though, because
that is what creates the OIDC federated credential and the `AZUREAPPSERVICE_*` secrets the workflow
references — there is no way to get those without it (or the equivalent `az ad app
federated-credential create` call, which is out of scope for a portal-first runbook).

### 4a. Push your outstanding commits first

```bash
cd "/Users/SONY/Documents/skills/Coding/net project"
git status                 # confirm nothing uncommitted
git push origin main
```

### 4b. Connect Deployment Center

1. **App Services** → **marketeye-web** → **Deployment** → **Deployment Center**
2. **Source:** GitHub → authorize if prompted
3. **Organization / Repository / Branch:** your account, this repo, `main`
4. **Authentication type:** **User-assigned identity** (the default — avoids a publish-profile
   password as a repo secret, same choice the API runbook makes)
5. **Save**

This commits a second workflow file to the repo (something like
`.github/workflows/marketeye-web_<random>.yml` or a variant of `main_marketeye-web.yml` with a
different suffix) and adds three `AZUREAPPSERVICE_*` secrets, typically GUID-suffixed.

### 4c. Reconcile the two workflow files — keep exactly one

```bash
git pull origin main
```

Open whatever new workflow file Azure just committed and copy its three secret names (the
`AZUREAPPSERVICE_CLIENTID_...`/`TENANTID_...`/`SUBSCRIPTIONID_...` lines under `azure/login@v2`).
Then either:

- **paste those exact secret names into `main_marketeye-web.yml`'s `azure/login` step**, replacing
  the placeholder `AZUREAPPSERVICE_CLIENTID_WEB` / `_TENANTID_WEB` / `_SUBSCRIPTIONID_WEB` names, or
- **rename the three GitHub secrets** (Settings → Secrets and variables → Actions) to
  `AZUREAPPSERVICE_CLIENTID_WEB` / `_TENANTID_WEB` / `_SUBSCRIPTIONID_WEB` so the existing
  `main_marketeye-web.yml` needs no edit.

Either way, **delete Azure's auto-generated workflow file** once the secrets line up — two workflows
both deploying to `marketeye-web` on every push race each other and double the Actions minutes,
same reasoning the API runbook gives for not running tests twice.

```bash
git add -A
git commit -m "Reconcile the auto-generated Web deploy workflow with the committed one"
git push origin main
```

### 4d. Watch the build

GitHub → repo → **Actions** tab → **Build and deploy ASP.Net Core app to Azure Web App -
marketeye-web**. Two jobs, **build** then **deploy**, same shape as the API's. First run takes
3-5 minutes.

## Step 5 — verify

```bash
BASE=https://marketeye-web-<suffix>.<region>.azurewebsites.net   # your real hostname from the Overview blade

curl -sS "$BASE/" | grep -o '<title>[^<]*</title>'
```

**Before deploying, this returns Azure's placeholder page's title.** After a successful deploy it
should show `<title>Home</title>` — `Home.razor` still carries the default Blazor scaffold content
("Hello, world! Welcome to your new app."), so seeing that exact text is not a bug, just something
nobody has replaced yet.

Then confirm the API wiring specifically, not just that the app boots:

```bash
curl -sS "$BASE/strategies" | grep -o 'Saved Strategies'
```

`Strategies.razor` renders `<h1>Saved Strategies</h1>` regardless of whether `/api/strategies`
succeeds — the meaningful check is opening `$BASE/strategies` in an actual browser and confirming
the table loads (or shows "No saved strategies yet," not a red network-error banner). A visible
`Could not load saved strategies: ...` message means `Api__BaseUrl` (Step 3a) is wrong, unreachable,
or missing the deployed API's exact hostname.

Give the first request 10-20 seconds — F1 cold start, same as the API.

If the browser instead shows an endless redirect or `ERR_TOO_MANY_REDIRECTS`, the deployed build
predates Step 3c's forwarded-headers fix — pull `main`, confirm `Program.cs` has the
`UseForwardedHeaders` block, and push to trigger a fresh deploy.

## Nothing to wire for the nightly cron

Step 5 of the API runbook (`MARKETEYE_BASE_URL`/`MARKETEYE_INGEST_SECRET`, `nightly-ingest.yml`) is
API-only. `MarketEye.Web` has no scheduled job, no ingestion secret, and nothing analogous to wire
here.

## Rollback

F1 has no deployment slots, same constraint as the API. Rollback is either reverting the offending
commit and letting the next push redeploy, or re-running a previous successful job from the
GitHub Actions run history (**Actions** tab → the last known-good run → **Re-run all jobs**) —
there is no zip artifact kept locally to fall back to the way the API runbook's zip-based note
implies, since this workflow builds fresh from source each time.
