using AnalyticsPlatform.Api.Health;
using AnalyticsPlatform.Api.Ingestion;
using AnalyticsPlatform.Api.Sites;
using AnalyticsPlatform.Api.Visitors;
using ClickHouse.Driver;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration -----------------------------------------------------------
// Connection strings come from configuration, never from code. In Development
// they're read from user secrets (`dotnet user-secrets list` to see them); in
// Docker/production they'll come from environment variables such as
// ConnectionStrings__ClickHouse. Fail at startup if either is missing, rather
// than on the first request.
var clickHouseConnectionString = builder.Configuration.GetConnectionString("ClickHouse")
    ?? throw new InvalidOperationException("Missing connection string 'ClickHouse'.");
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");

// --- Services (dependency injection) ------------------------------------------
// ClickHouseClient is thread-safe and designed to be created once and shared, so
// it's registered as a singleton. Same for NpgsqlDataSource, which owns the
// Postgres connection pool. The container disposes both on shutdown.
builder.Services.AddSingleton(_ => new ClickHouseClient(clickHouseConnectionString));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnectionString));

builder.Services.AddHealthChecks()
    .AddCheck<ClickHouseHealthCheck>("clickhouse")
    .AddCheck<PostgresHealthCheck>("postgres");

// Ingestion. All singletons: each holds no per-request state, so one shared
// instance is safe and cheapest. TimeProvider is .NET's injectable clock, so
// tests can fake "now" instead of depending on the real time.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SiteConfigStore>();
builder.Services.AddSingleton<DailySaltStore>();
builder.Services.AddSingleton<VisitorIdentifier>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<EventWriter>();

var app = builder.Build();

// --- Endpoints -----------------------------------------------------------------
app.MapGet("/", () => "Analytics Platform API");
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponseWriter.WriteJson });
app.MapIngestionEndpoints();

app.Run();
