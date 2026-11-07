# Analytics Platform — Project Context for Claude Code

This is a personal learning project: a self-hosted web analytics platform that
combines **the best of Google Analytics (GA4) and the best of Rybbit Analytics**
— GA's breadth of always-on auto-tracked events, Rybbit's lightweight
self-hostable simplicity. The primary goal is building depth in **C# / .NET**
and **ClickHouse** specifically. There's an eventual path toward a self-hostable SaaS
product, but that's secondary to the learning goal right now.

I (the developer) have hands-on experience running a self-hosted Rybbit instance,
so I know the target feature set from the consumer side. I don't need the product
concept explained — I need help building it well.

## Stack

- **C# / ASP.NET Core** — event ingestion API, query/aggregation API. Switched
  from Go on 2026-11-06, before any Go code existed, because the developer is now
  learning C#. ClickHouse remains the core learning target either way.
- **ClickHouse** — primary event store (columnar OLAP). This is the core learning
  target, not an implementation detail — don't casually suggest swapping it for
  Postgres/Timescale/etc.
- **React + TypeScript** — dashboard frontend in `dashboard/` (Vite, TanStack
  Query, Recharts). See "Dashboard" under Current state.
- **Postgres** — app metadata only (users, sites, API keys). Explicitly separate
  from the ClickHouse event store — event data never goes in Postgres.
- **Python** — deferred. Reserved for a clearly separable adjacent service later
  (e.g. anomaly detection, scheduled reports). Not part of the v1 core loop. Don't
  introduce Python for something C# could reasonably do.
- **Docker Compose** for local dev; Terraform for a cloud deploy path later
  (not started).

## Standard (auto-tracked) events

Decided 2026-08-30: like GA4's "Enhanced Measurement," a fixed set of interaction
events is always tracked by the snippet and can be toggled on/off **per site**,
distinct from user-defined custom events.

- **`page_view`** stays its own `event_type = 'pageview'` (unchanged) — it's core
  tracking, always on, not part of the toggle group. Matches GA: page_view sits
  outside the Enhanced Measurement toggle set too.
- **`event_type = 'standard'`** is the new category for the toggleable set. v1
  reference list (`event_name` values): `scroll`, `outbound_click`,
  `file_download`, `form_start`, `form_submit`, `video_start`, `video_progress`,
  `video_complete`. These reuse `event_name` + `properties` exactly like custom
  events (e.g. `properties['depth'] = '90'` for scroll, `properties['url']` for
  outbound_click) — no dedicated columns, per the existing Map-based design.
- **Don't add to this list speculatively.** Site search and other GA Enhanced
  Measurement categories are deliberately left out of v1 — add only when there's
  a real reason (e.g. Rybbit doesn't track site search either).
- **Custom event names must not collide with the reserved standard names above.**
  This is an ingestion-API concern (Phase 2), not a DB-level constraint —
  ClickHouse CHECK constraints add per-insert overhead we don't want at ingest
  volume.
- **Per-site toggle config lives in Postgres**, not ClickHouse — see `sites` /
  `site_tracked_events` under "Current state" below. This was pulled forward
  from Phase 7 specifically because the tracking snippet needs to read "which
  standard events does this site want" before it can decide what to instrument.
  Auth, users, and API keys are still Phase 7 — only these two tables exist
  early, and only for this reason.

## Visitor identification

Decided 2026-11-06: **each site chooses** (`sites.visitor_id_mode`, Postgres,
`postgres/init/002_visitor_id.sql`). Both modes ship in Phase 2.

- **`hash` (default)** — cookie-less, Plausible-style. Server computes
  `SHA-256(daily salt + site_id + IP + user agent)`; stored as `h:<hex>`.
  Nothing stored on the visitor's device. Same person on two days = two visitors.
- **`persistent`** — the snippet keeps a random UUID in `localStorage` and sends
  it; stored as `p:<uuid>`. Accurate returning visitors. Consent is the site
  owner's responsibility. **If no valid UUID arrives, fall back to the hash** —
  this lets a site run hash-mode until the visitor consents. Never trust the
  client value beyond validating it parses as a UUID.
- **Daily salt** — random 32 bytes per UTC day in Postgres `daily_salts`, created
  on first use, older days deleted by the API. Deleting it is what makes old
  hashes unlinkable. Stored in Postgres (not memory) so an API restart doesn't
  change everyone's ID mid-day. `ip_hash` uses the same salt.
- **`session_id`** (decided 2026-11-06): server-side, never from the client.
  Same `(site_id, visitor_id)` with no event for **30 minutes** = new session.
  Implemented with `IMemoryCache` sliding expiration (`Visitors/SessionTracker.cs`),
  IDs are `Guid.CreateVersion7()`. Known v1 limits: API restart starts new
  sessions; single instance only; hash-mode sessions split at UTC midnight
  (visitor_id changes with the salt). Revisit with Phase 5.

### Enrichment (Phase 6) — `src/AnalyticsPlatform.Api/Enrichment/`
- **User agent → `browser`, `browser_version`, `os`, `os_version`,
  `device_type`, `is_bot`** via **DeviceDetector.NET 6.5.x** (port of Matomo's
  device detector; chosen 2026-11-07 over UAParser, abandoned since 2021, and
  MyCSharp.HttpUserAgentParser, no tablet detection). `UserAgentParser`
  caches results per UA string (own `MemoryCache`, SizeLimit 10k), parses at
  most 512 chars, versions truncated to major ("140").
- `device_type` ∈ desktop | mobile (smartphone/phablet/feature phone) | tablet
  | other | "".
- **Bots:** `is_bot = 1` for DeviceDetector bots, HTTP libraries (curl,
  python-requests…) and empty UAs. Bots are **stored, not rejected** (still
  202), and every stats query filters `is_bot = 0`.
- **UTM:** `utm_source/medium/campaign/term/content` from the page URL's query
  string (`UtmTags.FromUrl`): names case-insensitive, values URL-decoded,
  trimmed, case kept, capped at 100 chars, first value wins.
- Known limits: Chrome freezes the OS version in the UA (Windows 11 reports
  "10") — fixing needs User-Agent Client Hints (`Sec-CH-UA-*`), not done.
  Search-engine/social classification of referrers not done.
- **GeoIP → `country_code`, `region` (most specific subdivision, English),
  `city`** via **MaxMind.GeoIP2 6.1** reading `GeoLite2-City.mmdb`.
  - Download: the `geoipupdate` container (`ghcr.io/maxmind/geoipupdate:v8.0.0`)
    in docker-compose, credentials `MAXMIND_ACCOUNT_ID` / `MAXMIND_LICENSE_KEY`
    from `.env` (not user secrets — the API never needs the key), refreshes
    every 72 h into `./data/geoip` (gitignored bind mount so the VS-run API sees it).
  - API config: `GeoIp:DatabasePath` (Development: `../../data/geoip/...`,
    resolved against the content root). Unset/missing file → geo is empty,
    never an error.
  - `MaxMindGeoLocator` loads the file with `FileAccessMode.Memory` (~60 MB;
    memory-mapping would lock the file on Windows so geoipupdate couldn't
    replace it) and re-checks the file timestamp every 5 min, swapping readers.
  - Privacy: the raw IP is used for the lookup only; an integration test renders
    every stored column (`formatRowNoNewline('TSV', *)`) and asserts the IP is absent.
  - Behind a reverse proxy, `RemoteIpAddress` will be the proxy — configure
    `ForwardedHeaders` at deploy time (not done; local dev is direct).
  - Real-database unit tests `Assert.SkipUnless` the file exists.
- GeoLite2 credentials verified working 2026-11-07 (first attempts were a typo
  / a not-yet-activated key — new keys can take ~5 min). With the file present,
  the real-DB tests run instead of skipping.

### Phase 7 — secret API keys (step 4, done 2026-11-07) — `Auth/ApiKeys.cs`
- `api_keys` table (`postgres/init/005_api_keys.sql`): `apk_` + 32 random
  bytes base64url; stored as SHA-256 (fast hash is fine for 256-bit random
  secrets) plus a 12-char `prefix` for display; `last_used_at`; cascade on user.
- `GET|POST /api/keys`, `DELETE /api/keys/{id}` (own keys only, 404 otherwise),
  all on the Session policy — a key can't create keys. The key is returned once.
- Auth: default scheme is a **policy scheme** `CookieOrApiKey` that forwards
  to `ApiKeyAuthenticationHandler` when `Authorization: Bearer apk_…` is
  present, else to the cookie. Key principals carry only the user id +
  `auth_method=api_key` — **never admin**, so an admin's key sees only sites
  the admin owns.
- Keys are **read-only**: `GET /api/sites` and `/stats/*` use the default
  policy (either scheme); site management, `/api/keys`, `/api/auth/me` and
  invites need the cookie (Session policy) → 401 with a key.
- Dashboard: "API keys" card in Settings (create, shown-once key + curl
  example, list by prefix with last-used, revoke with inline confirm).

### Phase 7 — site management + invites UI (step 3, done 2026-11-07) — `Sites/SiteStore.cs`, `Sites/SiteManagementEndpoints.cs`
- `POST /api/sites` {name, domain, visitorIdMode?} (creator = owner, all 8
  standard events on, 409 if the domain is taken), `GET|PATCH|DELETE
  /api/sites/{id}`, `POST /api/sites/{id}/tracking-id` (new random ID; old one
  dies at once). PATCH only changes fields sent. All need the **Session**
  policy (cookie login — API keys can't manage sites) + the shared
  `SiteAccess` endpoint filter (404 unless owner/admin).
- Domain input is normalized by `SiteDomain.TryNormalizeForRegistration`
  (accepts pasted URLs, strips scheme/port/path/`www.`, lowercases, unicode →
  punycode, needs a dot unless `localhost`).
- Every change calls `SiteConfigStore.Forget(id, trackingId)` so the tracker
  sees it immediately instead of after the 1-minute cache.
- Delete removes ClickHouse rows first (lightweight `DELETE FROM`, tables in
  `ClickHouseTables.PerSite`), then the Postgres row (cascades).
- Dashboard: header toggle Dashboard / **Settings** (`?view=settings`).
  Settings = tracking code (copy, regenerate with inline confirm), name /
  visitor-ID mode / standard-event toggles (PATCH of changed fields only —
  `changedSettings()` in `src/sites.ts`), delete (type the domain to confirm),
  add a site, and for admins an invite-link generator. No browser `confirm()`
  dialogs. Vite also proxies `/tracker.js` so the shown snippet works in dev.

### Phase 7 — accounts + login (step 2, done 2026-11-07) — `src/AnalyticsPlatform.Api/Auth/`
- **Decision:** self-hosting is a first-class option. `Auth:OpenRegistration`
  false (default) = invite-only after the first account; true = open sign-up (SaaS).
  The **first account becomes admin** and claims all sites with no owner.
- Tables (`postgres/init/004_users.sql`): `users` (unique on `lower(email)`),
  `sites.owner_user_id` (nullable, ON DELETE CASCADE), `invites` (SHA-256 of a
  32-byte random token; one-time; `Auth:InviteLifetime` 7 days).
- **No EF Core / full Identity**: hand-written `UserStore` (Npgsql) +
  `PasswordHasher<AppUser>` from Microsoft.Extensions.Identity.Core (PBKDF2,
  hashes start `AQAAAA`). Registration runs in one transaction with
  `pg_advisory_xact_lock` so two first sign-ups can't both become admin.
- **Cookie auth** (`ap_session`): HttpOnly, SameSite=Strict (the CSRF defence;
  dashboard is same-origin via the Vite proxy), Secure when HTTPS, 14 days
  sliding. 401/403 instead of redirects. Claims: id, email, `admin`. Note: claims
  live in the cookie until it expires — demoting/deleting a user doesn't end
  their session yet.
- Endpoints: `GET /api/auth/status` (anon), `POST register|login|logout`,
  `GET /api/auth/me`, `POST /api/admin/invites` (admin policy). Login checks a
  dummy hash for unknown emails (timing) and gives one message for both cases.
  Rate limit `Auth:AttemptsPerMinute` (10) per client IP on login+register → 429.
- **Access:** `/api/sites` and `/api/sites/{id}/stats/*` require auth; an
  endpoint filter on the stats group returns **404** (not 403) unless you own
  the site or are admin. Tracking endpoints, `/health`, `/demo` stay anonymous.
- Dashboard: `src/auth.ts` (screen choice + login/register/logout mutations
  that `resetQueries()`), `AuthScreen` (setup / login / `?invite=` sign-up),
  header shows the user + Sign out. Invites are created in Settings (step 3).
- Deploy notes: cookie encryption keys (ASP.NET Data Protection) are stored per
  machine user by default; containers need a persisted key ring. Behind HTTPS the
  cookie gets `Secure` automatically.
- Tests: fixture signs `Http` in as the admin; `CreateFreshPostgresDatabaseAsync`
  gives first-run tests an empty DB; `CreateUserClientAsync` = invited 2nd user.

### Phase 7 — tracking IDs + domain check (step 1, done 2026-11-07)
- **Decision (2026-11-07):** the developer wanted tracking keys like GA / Rybbit.
  Implemented as a **public tracking ID** per site (`sites.tracking_id`,
  `ap_` + 16 hex, 64 random bits, Postgres default via `gen_random_uuid()`;
  `postgres/init/003_tracking_ids.sql`). It identifies the site in the
  snippet; it is NOT a secret (it's in page source) — same as GA's measurement
  ID. Replaces the enumerable numeric id in the snippet; numeric id stays internal.
- **Domain check** (`Ingestion/SiteDomain.cs`): event `url` host must be the
  site's domain, its `www.` form, or a subdomain (dot-boundary, case-insensitive)
  → else 400 on `url`. This is what stops snippet copying; not bulletproof
  (scripts can lie), standard trade-off.
- `POST /api/events` takes `trackingId` (numeric `siteId` no longer accepted);
  tracker config moved to `GET /api/tracking/{trackingId}/config`;
  `/api/sites` now returns `trackingId` (for the snippet in step 3).
  `SiteConfigStore.GetByTrackingIdAsync` (cached 1 min like by-id).
- Dev: `/demo` injects sites from Postgres and picks the one whose domain is
  the page's host — open `http://persistent.localhost:5115/demo` to test site 2
  (Chromium resolves `*.localhost`). Don't bind Kestrel to `*.localhost` names
  as well as `localhost` on the same port — non-localhost names bind all
  interfaces and the two clash ("address already in use").
- Noticed: DeviceDetector reports "Headless Chrome" as a normal browser, not a
  bot. Consider flagging it (backlog).

### Breakdowns (Phase 6, done)
- `GET /api/sites/{id}/stats/breakdown?dimension=browser|os|device|country`
  (+ from/to/limit). `dimension` is required and mapped through the
  `BreakdownDimension` enum to a fixed column — request text never reaches SQL.
  Items `{ value (null = unknown), visitors, pageviews }`, ranked by visitors.
- Dashboard: "Locations" (country names via `Intl.DisplayNames`, code shown
  muted; Windows can.t render flag emoji, so no flags) and "Devices" (Browser /
  OS / Device tabs in the card header — they pick the view; the filter row
  still scopes the data). Convention in all lists: the right-most bold column
  is the ranking metric and drives the bar.
- `scripts/dev_events.sql` now also generates browser / os / device / country.

### Batched ingestion (Phase 5, done)
- Endpoint → `EventQueue` (bounded `Channel<EventRow>`, single reader) →
  `EventBatchWriter` (`BackgroundService`) → `IEventStore`
  (`ClickHouseEventStore`, one `InsertBinaryAsync` per batch).
- `IngestionOptions` from config section `Ingestion` (defaults in
  appsettings.json): `QueueCapacity` 100k, `MaxBatchSize` 5k, `FlushInterval`
  1s, retry backoff 0.5s → 30s. Validated on start.
- Guarantees, each pinned by a test: batch ≤ MaxBatchSize; partial batch
  flushed after FlushInterval; failed inserts retried with backoff, same batch,
  in order (no loss while ClickHouse is briefly down); shutdown drains the queue;
  full queue → 503. **At-least-once**: a retry after a lost reply can duplicate
  a batch (ClickHouse insert dedup would need `non_replicated_deduplication_window`
  or a ReplicatedMergeTree — not done).
- If the host's shutdown deadline passes while ClickHouse is down, the writer
  gives up and logs how many events were lost. `StopAsync` must not rely on a
  `using` token registration to cancel `_giveUp` (callbacks run newest-first
  and the registration gets disposed before firing — this hung shutdown; a test
  catches it).
- `timestamp` is set when the event arrives, not when it's written.
- Still single-instance (in-memory queue + session cache). Durable queueing
  (e.g. a disk buffer or Kafka/Redpanda) is a later decision, not v1.

### Ingestion API (Phase 2, done)
- `POST /api/events` — JSON `EventRequest` (`trackingId`, `type`, `name`, `url`,
  `referrer`, `visitorId`, `screenWidth`, `screenHeight`, `properties`).
  Validates, resolves the site (cached 1 min), computes visitor/session/ip_hash,
  **queues** the row and returns **202 Accepted** (see "Batched ingestion"
  below). Errors are 400 ValidationProblem; full queue / shutting down is 503
  with `Retry-After: 5`.
  Body is read manually (not `[FromBody]`) so it accepts `Content-Type:
  text/plain` — the tracker sends text/plain so cross-origin posts are CORS
  "simple requests" with no preflight. Body capped at 16 KB (413).
- `GET /api/tracking/{trackingId}/config` → `{ visitorIdMode, standardEvents[] }` (enabled
  ones only), `Cache-Control: max-age=60`, 404 for unknown sites.
- CORS policy `Tracker` (any origin, GET/POST, no credentials) applies only to
  the tracker route group, not `/health` or future dashboard endpoints.
- **`wwwroot/tracker.js`** — dependency-free ES5-ish snippet served by the API:
  `<script defer src="./tracker.js" data-tracking-id="ap_…">`. Pageview on load +
  SPA `pushState`/`popstate` (hash-only changes ignored), `fetch` with
  `keepalive` + text/plain, queues events until config loads,
  `analyticsPlatform.track/consent/revokeConsent`, optional
  `data-wait-for-consent`. Persistent ID in `localStorage` key `ap_visitor_id`.
- **Standard events in tracker.js** — listeners always attached; `send()`
  drops any standard event not in the site's `standardEvents`. Property
  conventions (all values strings, clipped to 500 chars):
  `scroll {depth: 25|50|75|90}` (once each per page, reset on SPA pageview),
  `outbound_click {url, domain}` (different hostname, http(s), left/middle click),
  `file_download {url, extension}` (extension list or `download` attr; takes
  precedence over outbound), `form_start`/`form_submit {form_id, form_name,
  form_action}` (form_start = first `input` event per form; **field values are
  never read**), `video_start`/`video_progress {percent: 25|50|75}`/
  `video_complete {percent: 100}` with `{video_url (query stripped), video_title}`
  — native `<video>` only; YouTube/Vimeo iframes need their player APIs (not v1).
- `/demo` (Development only) — `DevPages/demo.html` exercises the tracker.
- `scripts/dev_seed.sql` creates site 1 (`hash`) and site 2 (`persistent`).
- Deferred: check that event hostname matches the site's domain (anti-spam;
  probably Phase 7 with API keys), tracker.js caching/minification, automated
  tests for tracker.js (it was verified with an ad-hoc Node fake-DOM harness).

### Dashboard (Phase 4, done) — `dashboard/`
- `npm run dev` with the API on 5115; Vite proxies `/api` (override with
  `API_URL`). No CORS on the stats endpoints; `GET /api/sites` added for the
  site picker (lists all sites until Phase 7 auth).
- Filter row (site + 24h/7d/30d/90d presets, all UTC, kept in the URL as
  `?site=&range=`) scopes every view. 24h → hourly buckets, else daily.
- Charts follow the dataviz skill: 2-series line (pageviews `--series-1` blue,
  visitors `--series-2` orange; validated light + dark incl. CVD), legend,
  crosshair tooltip, Table toggle, round y-ticks via `niceTicks()`. Top lists are
  tables with a one-hue thin bar. Light/dark via `prefers-color-scheme`.
- Verified by headless-Chrome screenshots (light, dark, 375px via iframe —
  headless Chrome's minimum viewport is 512px, so `--window-size` below that
  just crops). Hover tooltip and Table toggle not yet clicked through.
- Bundle ~186 KB gzip (mostly Recharts) — fine for the dashboard; unrelated
  to `tracker.js`, which stays dependency-free.

### Query API (Phase 3, done)
- `GET /api/sites/{id}/stats/pageviews?from&to&interval=hour|day`,
  `/top-pages?from&to&limit`, `/top-referrers?from&to&limit` (`Stats/`).
  Dates parsed as UTC (`AssumeUniversal`); default range last 7 days; max 366
  days, hourly max 31 days; limit 1–100 (default 10). 404 unknown site, 400
  ValidationProblem otherwise. **No CORS and no auth** — local only until Phase 7.
- Every query filters `site_id, event_type, timestamp` (sort-key order);
  `EXPLAIN indexes = 1` confirmed partition + primary-key pruning.
- `uniq()` (approximate) for visitors. Time series uses `WITH FILL` for empty
  buckets; `toStartOfDay/Hour` return `DateTime`, so the `TO` bound needs
  `toDateTime(...)` or WITH FILL errors on mismatched types.
- **Top referrers are per session**: `argMin(referrer_domain, timestamp)` per
  `session_id` = entry referrer; self-referrals count as direct (`domain: null`).
- **`referrer_domain` is now populated at ingestion** (host, lowercased, `www.`
  stripped) — pulled forward from Phase 6 because top referrers needs it. UTM
  and search-engine parsing remain Phase 6. Rows ingested before 2026-11-07
  have it empty.
- Gotchas: a ClickHouse query parameter named `limit` collides with the
  built-in `limit` setting — use `top_n`. `AddParameter` is an extension method
  in `ClickHouse.Driver.Utility`.
- Known caveat: hash-mode `visitor_id` changes daily, so multi-day "visitors"
  totals overcount for hash sites (per-bucket daily numbers are fine).
- Timezone: all buckets are UTC. Per-site timezone is a later decision.
- `scripts/dev_events.sql` generates 25k pageviews over 30 days for the
  `demo.localhost` site (`--param_site=<id>`).

## Hard requirement: verify current library versions

Package knowledge from training data goes stale, and this ecosystem moves fast
(ClickHouse .NET clients, ASP.NET Core / .NET releases, React tooling). Before recommending or
using any library or version:

1. Search for the current stable version and recent release notes — don't rely on
   remembered defaults.
2. Flag if a library has a known successor, has been deprecated, or had a recent
   breaking major version change.
3. When scaffolding a new service, check current best-practice project layout for
   that language/ecosystem rather than defaulting to older conventions.
4. If uncertain whether something is current, say so explicitly rather than
   presenting a remembered version as fact.

## Scope discipline — this matters more than usual

v1 is intentionally narrow:

**tracking snippet → C# ingestion → ClickHouse → basic dashboard**

Basic dashboard = pageviews over time, top pages, top referrers, geo/device
breakdown. That's it.

**Explicitly deferred, do not build until told:** funnels, retention/cohorts,
goal tracking, the Python service, multi-user auth (beyond basic API keys),
shareable dashboards, CSV/API export.

If a request seems to reach beyond the current phase (see below), say so and ask
whether to proceed or defer it, rather than silently building it.

## Implementation phases

1. **ClickHouse schema + local infra** — ✅ done. See "Current state" below.
2. **C# ingestion API (minimal)** — ✅ done (see "Ingestion API" under Current state) — smallest possible HTTP endpoint accepting a
   pageview event, writing synchronously to ClickHouse. No batching, no auth yet.
   Paired with a JS tracking snippet that does the beacon on page load **and**
   auto-instruments the standard-event set (scroll/click/form/video/download
   listeners), reading each site's on/off config from `site_tracked_events`
   (Postgres) at load time. This is more scope than "barebones beacon" was
   originally — grew when the GA+Rybbit standard-events decision landed (see
   "Standard (auto-tracked) events" above). Reject/ignore custom event names
   that collide with the reserved standard-event names at this layer.
3. **Query API + raw dashboard reads** — ✅ done (see "Query API" under Current
   state). Read endpoints: pageviews over time, top pages, top referrers.
4. **React dashboard (v1 views only)** — ✅ done (see "Dashboard" under Current
   state). Time series chart, top pages table, top referrers table, geo/device
   breakdown (filled in Phase 6: "Locations" + "Devices" cards). Exactly these
   views. Resist adding more.
5. **Batching + async ingestion** — ✅ done 2026-11-07 (see "Batched ingestion"
   under Current state).
6. **Enrichment at ingestion** — ✅ done 2026-11-07: user-agent parsing + bot
   flag, UTM tags, IP geolocation (GeoLite2), breakdown endpoint + dashboard
   cards. See "Enrichment" and "Breakdowns" under Current state.
7. **Auth, multi-site, API keys** — ✅ done 2026-11-07 (see "Phase 7" under Current
   state). Steps: ✅ 1 public tracking IDs + domain check · ✅ 2 users + login +
   locking the dashboard API · ✅ 3 site management in the dashboard (incl. an
   invite UI) · ✅ 4 secret API keys for server-to-server stats reads.
8. **Rollups, retention, remaining backlog** — hourly/daily rollup tables, real
   retention policy (current TTL is a placeholder), then whatever's next from the
   backlog. Also the point to evaluate whether the Python service earns its place.

We are currently **in Phase 8**.
The v1 core loop (snippet → C# ingestion → ClickHouse → dashboard) works end to end.

## Current state

### Infra (`docker-compose.yml`)
- ClickHouse `26.3` (current LTS branch), exposed on 8123 (HTTP) and 9000 (native).
  Init SQL auto-runs from `./clickhouse/init/` (that's where `001_events.sql`
  actually lives now — it previously sat at repo root, unmounted, so it never
  actually ran on container start).
- Postgres `18`, exposed on host port **5433** (not 5432 — developer already runs
  another Postgres project locally on 5432; container-internal port is still 5432,
  and other containers on this compose network reach it as `postgres:5432`). Init
  SQL auto-runs from `./postgres/init/` (`001_sites.sql`).
- Dev credentials live in a gitignored `.env` (copy `.env.example` to `.env`
  before `docker compose up`). Never commit `.env`.
- ClickHouse healthcheck uses `wget`; if it reports unhealthy despite the server
  working, swap to `clickhouse-client --query "SELECT 1"` — `wget` availability in
  the image wasn't independently confirmed.

### ClickHouse schema (`clickhouse/init/001_events.sql`)
Single `events` table, `MergeTree` engine, `PARTITION BY toYYYYMM(timestamp)`,
`ORDER BY (site_id, event_type, timestamp)`.

Key design decisions (don't relitigate these without a real reason — the sort key
especially is expensive to change post-hoc):

- **Sort key** `(site_id, event_type, timestamp)` — nearly every query filters by
  site_id first, then time range; event_type next since pageviews vastly outnumber
  custom events.
- **`properties Map(String, String)`** for custom event payloads — chosen over
  ClickHouse's native `JSON` type because current payloads are flat key-value
  pairs used for filtering/grouping, not nested structures. Revisit if custom
  event schemas become nested — the native JSON type is production-ready as of
  25.3 and would be the right call for that case.
- **Browser/OS/device and UTM columns are populated** since Phase 6 (see
  "Enrichment"); **geo columns** (`country_code`, `region`, `city`) are filled
  whenever a GeoLite2 database is present. Don't add new columns for these —
  populate the existing ones.
- **`ip_hash`**, not raw IP — hashed at ingestion (Phase 2), for GDPR alignment.
  Never store or log raw IP addresses.
- **TTL is a placeholder** (1 year DELETE) — not a real retention policy. Real
  policy is Phase 8. `ALTER TABLE ... MODIFY TTL` is cheap to change later
  (metadata-only, applied by background merges), so this isn't a locked-in
  decision.
- **`visitor_id`** is `String` — strategy decided 2026-11-06, see "Visitor
  identification" below. Values are prefixed `h:` (cookie-less hash) or `p:`
  (persistent browser ID) so both kinds can live in one column.
- **`site_id`** is `UInt32`, expected to reference a future Postgres `sites.id`
  (Phase 7, not yet designed).
- **`event_type = 'standard'`** — new category for GA-Enhanced-Measurement-style
  auto-tracked interaction events, see "Standard (auto-tracked) events" above for
  the full reference list and rationale.

Sanity-check queries validating this schema live in `scripts/sanity_check.sql`
(pageviews over time, top pages, top referrers, device breakdown, standard-event
breakdown, plus an `EXPLAIN indexes = 1` to confirm the sort key is actually
being used).

### Postgres (app metadata) — `postgres/init/001_sites.sql`
- **`sites`** — `id`, `name`, `domain` (unique), `created_at`. Minimal; no owner/
  user FK yet (that's Phase 7).
- **`site_tracked_events`** — `(site_id, event_key, enabled)`, one row per site
  per standard event. `event_key` is CHECK-constrained to the reference list in
  "Standard (auto-tracked) events" above. Rows are meant to be seeded per site at
  creation time (default `enabled = true`, opt-out like GA) — the seeding logic
  itself isn't built yet (Phase 2/7 concern).
- **`sites.visitor_id_mode`** and **`daily_salts`** —
  `postgres/init/002_visitor_id.sql`, see "Visitor identification". Init scripts
  only run on an empty volume, so new migration files must be idempotent and
  get applied by hand to existing DBs until migration tooling is chosen.
- `users`, `invites` (004) and `api_keys` (005) — see the Phase 7 sections.

## Conventions

- **C# / .NET** (decided 2026-11-06):
  - **.NET 10 (LTS, supported to Nov 2028)**, pinned via `global.json` (SDK
    10.0.301, `latestFeature` roll-forward). .NET 11 (STS) ships 2026-11-10 —
    don't jump to it; next planned move is .NET 12 LTS.
  - Solution `AnalyticsPlatform.slnx` (new XML solution format) at repo root,
    projects under `src/`. Developer uses **Visual Studio Community 2026**.
  - One web project for now: `src/AnalyticsPlatform.Api` (ASP.NET Core minimal
    APIs) hosting both ingestion and query endpoints. Splitting ingestion into
    its own service is a later decision, not v1.
  - ClickHouse client: **`ClickHouse.Driver`** (official ClickHouse C# client,
    1.5.0 at time of writing; replaces the older community `ClickHouse.Client`
    package — don't use that one). Note 1.4.0 made zstd response compression
    the default.
  - Postgres client: **`Npgsql`** (10.0.3 at time of writing). ORM vs. Dapper vs.
    raw Npgsql not yet decided.
  - Connection strings: `ConnectionStrings:ClickHouse` / `ConnectionStrings:Postgres`
    from configuration — **user secrets** in dev, env vars
    (`ConnectionStrings__ClickHouse`) in Docker/prod. Never in appsettings.json.
    App throws at startup if either is missing.
  - `ClickHouseClient` and `NpgsqlDataSource` are DI singletons. `GET /health`
    runs `IHealthCheck`s against both DBs (JSON body, 503 when unhealthy).
  - Still undecided: analyzers/formatting rules, error-handling conventions.
- **ClickHouse:** schema/table engine decisions — see above, MergeTree family,
  decided.
- **React / TypeScript** (decided 2026-11-07): Vite 8, React 19.3, TypeScript 7
  (native Go compiler), TanStack Query 5 for all server state (one hook per
  endpoint in `src/api.ts`, `keepPreviousData` so refetches dim instead of
  flashing), Recharts 3 for charts, oxlint for linting, plain CSS with color-role
  custom properties (no CSS framework). Exact versions locked in `package-lock.json`.
- **Testing** (decided 2026-11-07): **tests first** — every new feature or
  refactor starts by adding/updating tests (developer's standing rule).
  - `tests/AnalyticsPlatform.Api.Tests` — xUnit v3 (4.x) unit tests, no I/O.
    `FakeTimeProvider` for time; `MemoryCache` gets a fake clock via an
    `ISystemClock` adapter. `IDailySaltProvider` exists so `VisitorIdentifier`
    can be tested without Postgres.
  - `tests/AnalyticsPlatform.Api.IntegrationTests` — `WebApplicationFactory<Program>`
    + Testcontainers (ClickHouse 26.3, Postgres 18 — keep in sync with
    docker-compose; the Testcontainers default images are years old). One
    assembly fixture applies every `*/init/*.sql` to empty DBs; environment
    `Testing` so user secrets are never loaded. Tests isolate by creating their
    own site. `WaitForEventsAsync` polls, so tests survive async ingestion (Phase 5).
  - `global.json` opts into the .NET 10 **Microsoft.Testing.Platform** runner:
    use `dotnet test --project <path>` / `--solution`, not a bare path.
  - Dashboard: Vitest (`npm test`) for `src/*.test.ts`.
  - Gotchas found: TestServer has no `IHttpMaxRequestBodySizeFeature` (body
    limit is now enforced by reading the body manually); `GetCustomAttributes<T>()`
    and ClickHouse `AddParameter` are extension methods needing their namespace.

## How this file relates to the Claude Project

Architecture decisions, schema design, and API contracts get discussed in a
Claude Project *before* implementation. This file is the synced output of that —
when a decision changes in the Project, it should be reflected back here. If
something in this file seems to conflict with an in-progress conversation, the
conversation wins; ask if unsure rather than assuming this file is stale.