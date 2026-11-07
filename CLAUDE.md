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
- **React** — dashboard frontend. Likely TanStack Query for data fetching (prior
  experience with it from other work), but not finalized.
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

### Ingestion API (Phase 2, done)
- `POST /api/events` — JSON `EventRequest` (`siteId`, `type`, `name`, `url`,
  `referrer`, `visitorId`, `screenWidth`, `screenHeight`, `properties`).
  Validates, resolves the site (cached 1 min), computes visitor/session/ip_hash,
  one synchronous ClickHouse insert, returns 204. Errors are 400 ValidationProblem.
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
  tests (tracker was verified with an ad-hoc Node fake-DOM harness; testing
  approach still undecided).

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
3. **Query API + raw dashboard reads** — read endpoints: pageviews over time, top
   pages, top referrers. Validate via curl/Postman before any UI exists. Fix slow
   queries now.
4. **React dashboard (v1 views only)** — time series chart, top pages table, top
   referrers table, geo/device breakdown. Exactly these four. Resist adding more.
5. **Batching + async ingestion** — buffer events, batch-write to ClickHouse
   instead of one insert per event. Do this after the loop is trusted end-to-end,
   not before.
6. **Enrichment at ingestion** — IP geolocation (MaxMind GeoLite2), user-agent
   parsing, referrer/UTM parsing. The schema already has placeholder columns for
   this (see below) — populate them, don't restructure the table.
7. **Auth, multi-site, API keys** — Postgres-backed users, site registration,
   per-site API keys for the tracking snippet. The `sites` and
   `site_tracked_events` tables themselves already exist (pulled forward, see
   "Standard (auto-tracked) events" above) — this phase adds `users`/`api_keys`
   and real auth around them, not the tables from scratch.
8. **Rollups, retention, remaining backlog** — hourly/daily rollup tables, real
   retention policy (current TTL is a placeholder), then whatever's next from the
   backlog. Also the point to evaluate whether the Python service earns its place.

We are currently between **Phase 2 (done 2026-11-07) and Phase 3 (next)**.

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
- **Browser/OS/geo columns already exist** (`browser`, `os`, `device_type`,
  `country_code`, `region`, `city`, etc.) but are empty (`DEFAULT ''`) until Phase
  6 enrichment lands. Don't add new columns for these — populate the existing
  ones.
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
- **React:** state/data-fetching — likely TanStack Query, not finalized.
- **Testing:** not yet decided.

## How this file relates to the Claude Project

Architecture decisions, schema design, and API contracts get discussed in a
Claude Project *before* implementation. This file is the synced output of that —
when a decision changes in the Project, it should be reflected back here. If
something in this file seems to conflict with an in-progress conversation, the
conversation wins; ask if unsure rather than assuming this file is stale.