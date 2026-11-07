using System.Reflection;
using System.Text;
using System.Text.Json;
using AnalyticsPlatform.Api.Ingestion;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using Testcontainers.ClickHouse;
using Testcontainers.PostgreSql;

// One fixture for the whole test assembly: containers start once (~10-20s),
// and every test class gets the same instance through its constructor.
[assembly: AssemblyFixture(typeof(AnalyticsPlatform.Api.IntegrationTests.ApiFixture))]

namespace AnalyticsPlatform.Api.IntegrationTests;

// Starts ClickHouse + Postgres in Docker, applies the repo's real init scripts
// (so the tests also prove those scripts work on an empty database), then runs
// the API in-memory against them.
//
// Tests stay independent by each creating its own site (CreateSiteAsync) and
// only looking at that site's rows -- no cleanup between tests needed.
public sealed class ApiFixture : IAsyncLifetime
{
    // Same images as docker-compose.yml -- keep them in sync.
    private const string ClickHouseImage = "clickhouse/clickhouse-server:26.3";
    private const string PostgresImage = "postgres:18";
    private const string Password = "integration_tests";

    private readonly ClickHouseContainer _clickHouseContainer = new ClickHouseBuilder(ClickHouseImage)
        .WithDatabase("analytics")
        .WithUsername("analytics")
        .WithPassword(Password)
        .Build();

    private readonly PostgreSqlContainer _postgresContainer = new PostgreSqlBuilder(PostgresImage)
        .WithDatabase("analytics_app")
        .WithUsername("analytics")
        .WithPassword(Password)
        .Build();

    private WebApplicationFactory<Program>? _factory;

    public HttpClient Http { get; private set; } = null!;
    public ClickHouseClient ClickHouse { get; private set; } = null!;
    public NpgsqlDataSource Postgres { get; private set; } = null!;
    public string ClickHouseConnectionString { get; private set; } = "";
    public string PostgresConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_clickHouseContainer.StartAsync(), _postgresContainer.StartAsync());

        ClickHouseConnectionString =
            $"Host={_clickHouseContainer.Hostname};" +
            $"Port={_clickHouseContainer.GetMappedPublicPort(ClickHouseBuilder.HttpPort)};" +
            $"Username=analytics;Password={Password};Database=analytics";
        PostgresConnectionString = _postgresContainer.GetConnectionString();

        ClickHouse = new ClickHouseClient(ClickHouseConnectionString);
        Postgres = NpgsqlDataSource.Create(PostgresConnectionString);

        // Apply every init script in order, exactly like the containers do on first start.
        foreach (var file in RepoPaths.SqlFiles("clickhouse/init"))
        {
            await ClickHouse.ExecuteNonQueryAsync(await File.ReadAllTextAsync(file));
        }

        foreach (var file in RepoPaths.SqlFiles("postgres/init"))
        {
            await using var command = Postgres.CreateCommand(await File.ReadAllTextAsync(file));
            await command.ExecuteNonQueryAsync();
        }

        _factory = CreateFactory();
        Http = _factory.CreateClient();
    }

    // Runs the API in-memory against the test containers. Most tests share the
    // fixture's instance (`Http`); tests about startup/shutdown or special
    // settings create their own with this and dispose it themselves.
    //
    // "Testing" environment = user secrets are NOT loaded, so the API can only
    // ever see the test containers. A short flush interval keeps tests fast.
    public WebApplicationFactory<Program> CreateFactory(Action<IWebHostBuilder>? configure = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("ConnectionStrings:ClickHouse", ClickHouseConnectionString);
            builder.UseSetting("ConnectionStrings:Postgres", PostgresConnectionString);
            builder.UseSetting("Ingestion:FlushInterval", "00:00:00.050");
            configure?.Invoke(builder);
        });

    public static Task<HttpResponseMessage> PostEventAsync(HttpClient http, object body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), Encoding.UTF8, "text/plain"),
        };
        return http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        Http?.Dispose();
        if (_factory is not null) await _factory.DisposeAsync();
        ClickHouse?.Dispose();
        if (Postgres is not null) await Postgres.DisposeAsync();
        await _clickHouseContainer.DisposeAsync();
        await _postgresContainer.DisposeAsync();
    }

    // ---- Sites ----------------------------------------------------------------

    // Creates a fresh site with a unique domain; all standard events enabled
    // except the ones listed.
    public async Task<uint> CreateSiteAsync(string visitorIdMode = "hash", params string[] disabledStandardEvents)
    {
        await using var insert = Postgres.CreateCommand(
            "INSERT INTO sites (name, domain, visitor_id_mode) VALUES ($1, $1, $2) RETURNING id");
        insert.Parameters.AddWithValue($"{Guid.NewGuid():N}.test");
        insert.Parameters.AddWithValue(visitorIdMode);
        var siteId = (int)(await insert.ExecuteScalarAsync())!;

        await using var events = Postgres.CreateCommand("""
            INSERT INTO site_tracked_events (site_id, event_key, enabled)
            SELECT $1, key, NOT (key = ANY($3)) FROM unnest($2) AS key
            """);
        events.Parameters.AddWithValue(siteId);
        events.Parameters.AddWithValue(EventTypes.StandardEventNames.ToArray());
        events.Parameters.AddWithValue(disabledStandardEvents);
        await events.ExecuteNonQueryAsync();

        return (uint)siteId;
    }

    // ---- Sending events through the API ---------------------------------------------

    public Task<HttpResponseMessage> PostEventAsync(
        object body, string userAgent = "IntegrationTest/1.0", string contentType = "application/json") =>
        PostRawEventAsync(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), userAgent, contentType);

    public Task<HttpResponseMessage> PostRawEventAsync(
        string body, string userAgent = "IntegrationTest/1.0", string contentType = "application/json")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/events")
        {
            Content = new StringContent(body, Encoding.UTF8, contentType),
        };
        request.Headers.UserAgent.ParseAdd(userAgent);
        return Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    // ---- Reading what landed in ClickHouse -----------------------------------------

    public async Task<List<StoredEvent>> GetEventsAsync(uint siteId)
    {
        const string sql = """
            SELECT event_type, event_name, url, path, hostname, visitor_id, toString(session_id),
                   referrer, referrer_domain, ip_hash, user_agent, screen_width, toJSONString(properties),
                   browser, browser_version, os, os_version, device_type, is_bot,
                   utm_source, utm_medium, utm_campaign, utm_term, utm_content
            FROM events
            WHERE site_id = {site:UInt32}
            ORDER BY timestamp, event_name
            """;
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", siteId);

        var events = new List<StoredEvent>();
        using var reader = await ClickHouse.ExecuteReaderAsync(sql, parameters);
        while (await reader.ReadAsync())
        {
            events.Add(new StoredEvent(
                reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), Guid.Parse(reader.GetString(6)),
                reader.GetString(7), reader.GetString(8), reader.GetString(9), reader.GetString(10),
                reader.GetFieldValue<ushort>(11),
                JsonSerializer.Deserialize<Dictionary<string, string>>(reader.GetString(12))!,
                reader.GetString(13), reader.GetString(14), reader.GetString(15), reader.GetString(16),
                reader.GetString(17), reader.GetFieldValue<byte>(18) == 1,
                new Enrichment.UtmTags(
                    reader.GetString(19), reader.GetString(20), reader.GetString(21),
                    reader.GetString(22), reader.GetString(23))));
        }

        return events;
    }

    // Waits until at least `count` events exist for the site. Ingestion is
    // synchronous today, so this returns immediately -- but Phase 5 makes it
    // asynchronous, and these tests should keep passing unchanged.
    public async Task<List<StoredEvent>> WaitForEventsAsync(uint siteId, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var events = await GetEventsAsync(siteId);
            if (events.Count >= count || DateTime.UtcNow > deadline)
            {
                return events;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    // ---- Seeding ClickHouse directly (for stats tests with exact timestamps) -------

    public Task SeedPageviewsAsync(uint siteId, IEnumerable<SeedPageview> pageviews)
    {
        string[] columns = ["site_id", "event_type", "timestamp", "session_id", "visitor_id", "url", "path", "hostname", "referrer_domain", "is_bot"];
        var rows = pageviews.Select(p => new object[]
        {
            siteId, p.EventType, p.Time, p.Session, p.Visitor, $"https://{p.Hostname}{p.Path}", p.Path, p.Hostname, p.ReferrerDomain,
            (byte)(p.IsBot ? 1 : 0),
        });
        return ClickHouse.InsertBinaryAsync("events", columns, rows);
    }
}

public sealed record StoredEvent(
    string EventType,
    string EventName,
    string Url,
    string Path,
    string Hostname,
    string VisitorId,
    Guid SessionId,
    string Referrer,
    string ReferrerDomain,
    string IpHash,
    string UserAgent,
    ushort ScreenWidth,
    Dictionary<string, string> Properties,
    string Browser,
    string BrowserVersion,
    string Os,
    string OsVersion,
    string DeviceType,
    bool IsBot,
    Enrichment.UtmTags Utm);

public sealed record SeedPageview(
    DateTime Time,
    string Path,
    string Visitor = "h:visitor",
    Guid Session = default,
    string ReferrerDomain = "",
    string Hostname = "site.test",
    string EventType = "pageview",
    bool IsBot = false);

public static class RepoPaths
{
    public static readonly string Root = FindRoot();

    public static IEnumerable<string> SqlFiles(string relativeDirectory) =>
        Directory.GetFiles(Path.Combine(Root, relativeDirectory), "*.sql").Order(StringComparer.Ordinal);

    // The path is written into the assembly at build time (see the .csproj),
    // so it works wherever the build output lives.
    private static string FindRoot()
    {
        var root = typeof(RepoPaths).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepoRoot").Value!;

        return File.Exists(Path.Combine(root, "AnalyticsPlatform.slnx"))
            ? root
            : throw new InvalidOperationException($"Repository root not found at '{root}'.");
    }
}
