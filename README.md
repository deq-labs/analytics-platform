# Analytics Platform

A self-hosted web analytics platform combining Google Analytics 4's auto-tracked
events with Rybbit's lightweight, self-hostable simplicity. A learning project
focused on **C# / ASP.NET Core** and **ClickHouse**.

**Status:** Phases 1–4 of 8 complete: tracking snippet → C# ingestion → ClickHouse → React dashboard works end to end.
See [`CLAUDE.md`](CLAUDE.md) for the full design,
decisions, and phase plan.

## Stack

- **C# / ASP.NET Core (.NET 10)**: ingestion and query APIs in
  `src/AnalyticsPlatform.Api` (open `AnalyticsPlatform.slnx` in Visual Studio 2026,
  or run `dotnet run --project src/AnalyticsPlatform.Api`)
- **ClickHouse**: event store
- **Postgres**: app metadata (sites, per-site tracking settings)
- **React + TypeScript**: dashboard in [`dashboard/`](dashboard/README.md)
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

## Tests

```sh
dotnet test                 # all C# tests (integration tests need Docker running)
dotnet test --project tests/AnalyticsPlatform.Api.Tests   # unit tests only, no Docker
cd dashboard && npm test    # dashboard tests (Vitest)
```

In Visual Studio: **Test → Test Explorer → Run All**.

- `tests/AnalyticsPlatform.Api.Tests` — fast unit tests (xUnit v3).
- `tests/AnalyticsPlatform.Api.IntegrationTests` — the real API against
  throwaway ClickHouse + Postgres containers (Testcontainers). They apply the
  repo's init scripts to empty databases and never touch your dev databases.
