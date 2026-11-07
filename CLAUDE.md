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

### Enrichment (Phase 6, partial) — `src/AnalyticsPlatform.Api/Enrichment/`
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
- Next: GeoLite2 (needs MaxMind key), then breakdown endpoints + dashboard card.

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
- `POST /api/events` — JSON `EventRequest` (`siteId`, `type`, `name`, `url`,
  `referrer`, `visitorId`, `screenWidth`, `screenHeight`, `properties`).
  Validates, resolves the site (cached 1 min), computes visitor/session/ip_hash,
  **queues** the row and returns **202 Accepted** (see "Batched ingestion"
  below). Errors are 400 ValidationProblem; full queue / shutting down is 503
  with `Retry-After: 5`.
  Body is read manually (not `[FromBody]`) so it accepts `Content-Type:
  text/plain` — the tracker sends text/plain so cross-origin posts are CORS
  "simple requests" with no preflight. Body capped at 16 KB (413).
- `GET /api/sites/{id}/config` → `{ visitorIdMode, standardEvents[] }` (enabled
  ones only), `Cache-Control: max-age=60`, 404 for unknown sites.
- CORS policy `Tracker` (any origin, GET/POST, no credentials) applies only to
  the tracker route group, not `/health` or future dashboard endpoints.
- **`wwwroot/tracker.js`** — dependency-free ES5-ish snippet served by the API:
  `<script defer src=".../tracker.js" data-site-id="1">`. Pageview on load +
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
   breakdown (empty state until Phase 6). Exactly these four. Resist adding more.
5. **Batching + async ingestion** — ✅ done 2026-11-07 (see "Batched ingestion"
   under Current state).
6. **Enrichment at ingestion** — 🟡 in progress. ✅ user-agent parsing + bot
   flag + UTM tags (2026-11-07, see "Enrichment" under Current state).
   ⏳ IP geolocation (MaxMind GeoLite2) — waiting on the developer's MaxMind
   account/licence key. Then: device/browser/geo breakdown endpoints + the
   dashboard's "Locations & devices" card. Populate existing columns, don't
   restructure the table.
7. **Auth, multi-site, API keys** — Postgres-backed users, site registration,
   per-site API keys for the tracking snippet. The `sites` and
   `site_tracked_events` tables themselves already exist (pulled forward, see
   "Standard (auto-tracked) events" above) — this phase adds `users`/`api_keys`
   and real auth around them, not the tables from scratch.
8. **Rollups, retention, remaining backlog** — hourly/daily rollup tables, real
   retention policy (current TTL is a placeholder), then whatever's next from the
   backlog. Also the point to evaluate whether the Python service earns its place.

We are currently **in Phase 6** (UA + UTM done; GeoIP pending a MaxMind key).
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
  "Enrichment"); **geo columns** (`country_code`, `region`, `city`) are still
  empty until GeoIP lands. Don't add new columns for these — populate the
  existing ones.
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
- **`users` / `api_keys`** are still a Phase 7 decision — don't scaffold them
  speculatively. Only `sites`/`site_tracked_events` were pulled forward, and only
  because the tracking snippet needs them.

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