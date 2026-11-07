# Analytics Platform

A self-hosted web analytics platform combining Google Analytics 4's auto-tracked
events with Rybbit's lightweight, self-hostable simplicity. A learning project
focused on **C# / ASP.NET Core** and **ClickHouse**.

**Status:** all 8 phases complete: tracking snippet → C# ingestion → ClickHouse → React dashboard, with accounts, site management, API keys, rollups and retention.
See [`CLAUDE.md`](CLAUDE.md) for the full design,
decisions, and phase plan.

## Stack

- **C# / ASP.NET Core (.NET 10)**: ingestion and query APIs in
  `src/AnalyticsPlatform.Api` (open `AnalyticsPlatform.slnx` in Visual Studio 2026,
  or run `dotnet run --project src/AnalyticsPlatform.Api`)
- **ClickHouse**: event store
- **Postgres**: app metadata (users, sites, settings, invites, API keys)
- **React + TypeScript**: dashboard in [`dashboard/`](dashboard/README.md)
- **Docker Compose**: local development and self-hosting

## Running locally

Requires Docker.

```sh
cp .env.example .env    # then set passwords
docker compose up -d
```

| Service    | Host port                    | Database        |
|------------|------------------------------|-----------------|
| ClickHouse | 8123 (HTTP), 9000 (native)   | `analytics`     |
| Postgres   | 5433                         | `analytics_app` |

Schemas are created automatically on first start from `clickhouse/init/` and
`postgres/init/`. Optional IP geolocation: put a free MaxMind GeoLite2 account ID and licence key in `.env` (`MAXMIND_ACCOUNT_ID`, `MAXMIND_LICENSE_KEY`); the `geoipupdate` container then downloads the database into `data/geoip/`. `scripts/sanity_check.sql` contains queries that validate the
ClickHouse schema.

## Tests

```sh
dotnet test                 # all C# tests (integration tests need Docker running)
dotnet test --project tests/AnalyticsPlatform.Api.Tests   # unit tests only, no Docker
cd dashboard && npm test    # dashboard tests (Vitest)
cd tests/tracker && npm install && npm test   # tracker.js in a simulated browser (jsdom)
```

In Visual Studio: **Test → Test Explorer → Run All**.

- `tests/AnalyticsPlatform.Api.Tests` — fast unit tests (xUnit v3).
- `tests/AnalyticsPlatform.Api.IntegrationTests` — the real API against
  throwaway ClickHouse + Postgres containers (Testcontainers). They apply the
  repo's init scripts to empty databases and never touch your dev databases.
- `tests/tracker` — `tracker.js` loaded into a simulated browser page (jsdom),
  with every request it would send recorded.

## Self-hosting

One image runs the API, the dashboard and `tracker.js` (see `Dockerfile`).

```sh
cp .env.example .env                       # set real passwords
docker compose --profile app up -d --build
```

Open `http://localhost:8080` (or `APP_PORT`). The first account you create
becomes the administrator; after that, sign-up is by invite link (Settings →
Invite someone), unless `OPEN_REGISTRATION=true`.

Then in **Settings**: add a site, copy its tracking code into your pages, and
optionally create API keys for reading stats from a server:

```sh
curl -H "Authorization: Bearer apk_..." https://analytics.example.com/api/sites/1/stats/top-pages
```

**On a public server**, put a TLS reverse proxy in front, expose only the
proxy, and set `BEHIND_PROXY=true` so the app trusts its forwarded client IP
and HTTPS headers. A minimal [Caddy](https://caddyserver.com) config (it fetches
the certificate itself):

```
analytics.example.com {
    reverse_proxy localhost:8080
}
```

When publishing through a proxy, bind the app to localhost only, e.g.
`APP_PORT=127.0.0.1:8080`.

| Data | Kept |
|---|---|
| Raw events | 13 months |
| Hourly pageview rollup | 5 years |
| Cookie-less visitor salts | 1 day |

Upgrading an existing install: new files in `postgres/init/` and
`clickhouse/init/` only run on empty volumes. Apply new ones by hand (they are
safe to re-run), and after adding the rollup run `scripts/rebuild_rollups.sql`
once.
