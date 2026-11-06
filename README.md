# Analytics Platform

A self-hosted web analytics platform combining Google Analytics 4's auto-tracked
events with Rybbit's lightweight, self-hostable simplicity. A learning project
focused on **C# / ASP.NET Core** and **ClickHouse**.

**Status:** Phase 1 of 8 complete (database schemas + local infrastructure).
The C# ingestion API is next. See [`CLAUDE.md`](CLAUDE.md) for the full design,
decisions, and phase plan.

## Stack

- **C# / ASP.NET Core**: ingestion and query APIs (not started)
- **ClickHouse**: event store
- **Postgres**: app metadata (sites, per-site tracking settings)
- **React**: dashboard (not started)
- **Docker Compose**: local development

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
`postgres/init/`. `scripts/sanity_check.sql` contains queries that validate the
ClickHouse schema.
