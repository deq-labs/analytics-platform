# Dashboard

React + TypeScript dashboard for the analytics platform (Vite, TanStack Query,
Recharts).

## Run it

1. Start the databases: `docker compose up -d` (repo root).
2. Start the API in Visual Studio (F5, `http` profile on port 5115).
3. In this folder:

```sh
npm install
npm run dev
```

Open the URL Vite prints (usually http://localhost:5173). Requests to `/api`
are proxied to the API, so no CORS setup is needed. To point at an API on
another port: `API_URL=http://localhost:5199 npm run dev`.

For data to look at, seed the demo site: `scripts/dev_seed.sql`, then
`scripts/dev_events.sql` (see the comments in each file).

## Scripts

| Command | Does |
|---|---|
| `npm run dev` | Dev server with hot reload |
| `npm run build` | Type-check (`tsc -b`) and production build to `dist/` |
| `npm run lint` | Lint with oxlint |
| `npm test` | Unit tests with Vitest (`src/*.test.ts`) |

## Layout

- `src/api.ts` — response types (mirror the C# records) and one TanStack Query hook per endpoint
- `src/format.ts` — date-range presets (UTC), number/date formatting, axis ticks
- `src/components/` — `Card`, `PageviewsChart`, `TopList`
- `src/index.css` — color roles for light and dark mode
