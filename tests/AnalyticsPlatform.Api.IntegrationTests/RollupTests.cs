using System.Net.Http.Json;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 8: hourly rollups (clickhouse/init/002_rollups.sql) and retention
// (003_retention.sql).
public class RollupTests(ApiFixture api)
{
    private sealed record Point(DateTime Time, ulong Pageviews, ulong Visitors);
    private sealed record Series(DateTime From, DateTime To, string Interval, Point[] Points);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTime Day = new(2026, 3, 10, 0, 0, 0, DateTimeKind.Utc);
    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private async Task<List<(DateTime Hour, ulong Pageviews, ulong Visitors)>> RollupRows(uint siteId)
    {
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", siteId);
        using var reader = await api.ClickHouse.ExecuteReaderAsync("""
            SELECT hour, sum(pageviews), uniqMerge(visitors)
            FROM pageviews_hourly WHERE site_id = {site:UInt32}
            GROUP BY hour ORDER BY hour
            """, parameters);
        var rows = new List<(DateTime, ulong, ulong)>();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetDateTime(0), reader.GetFieldValue<ulong>(1), reader.GetFieldValue<ulong>(2)));
        }

        return rows;
    }

    [Fact]
    public async Task New_pageviews_are_rolled_up_per_hour_without_bots_or_other_events()
    {
        var site = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site,
        [
            new SeedPageview(Day.AddHours(9).AddMinutes(5), "/", Visitor: "h:a"),
            new SeedPageview(Day.AddHours(9).AddMinutes(50), "/about", Visitor: "h:a"),
            new SeedPageview(Day.AddHours(9).AddMinutes(51), "/", Visitor: "h:b"),
            new SeedPageview(Day.AddHours(10), "/", Visitor: "h:c"),
            new SeedPageview(Day.AddHours(10), "/", Visitor: "h:bot", IsBot: true),
            new SeedPageview(Day.AddHours(10), "/", Visitor: "h:d", EventType: "custom"),
        ]);

        var rows = await RollupRows(site);

        Assert.Equal(
            [(Day.AddHours(9), 3UL, 2UL), (Day.AddHours(10), 1UL, 1UL)],
            rows);
    }

    [Fact]
    public async Task The_time_series_is_read_from_the_rollup()
    {
        // Rows written straight into the rollup (no raw events at all) still
        // show up -- proof the endpoint doesn't scan the events table.
        var site = await api.CreateSiteAsync();
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", site.Id);
        await api.ClickHouse.ExecuteNonQueryAsync("""
            INSERT INTO pageviews_hourly
            SELECT {site:UInt32}, toDateTime('2026-03-10 14:00:00', 'UTC'), 42, uniqState(toString(number))
            FROM numbers(7)
            """, parameters);

        var series = await api.Http.GetFromJsonAsync<Series>(
            $"/api/sites/{site}/stats/pageviews?from={Iso(Day)}&to={Iso(Day.AddDays(1))}&interval=day", Ct);

        Assert.Equal(new Point(Day, 42, 7), Assert.Single(series!.Points));
    }

    [Fact]
    public async Task Daily_visitors_are_distinct_across_hours()
    {
        var site = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site,
        [
            new SeedPageview(Day.AddHours(1), "/", Visitor: "h:same"),
            new SeedPageview(Day.AddHours(5), "/", Visitor: "h:same"),
            new SeedPageview(Day.AddHours(23), "/", Visitor: "h:other"),
        ]);

        var series = await api.Http.GetFromJsonAsync<Series>(
            $"/api/sites/{site}/stats/pageviews?from={Iso(Day)}&to={Iso(Day.AddDays(1))}&interval=day", Ct);

        Assert.Equal(new Point(Day, 3, 2), Assert.Single(series!.Points));
    }

    [Fact]
    public async Task Hourly_buckets_and_empty_hours_still_work()
    {
        var site = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site, [new SeedPageview(Day.AddHours(2).AddMinutes(30), "/")]);

        var series = await api.Http.GetFromJsonAsync<Series>(
            $"/api/sites/{site}/stats/pageviews?from={Iso(Day)}&to={Iso(Day.AddHours(4))}&interval=hour", Ct);

        Assert.Equal([0UL, 0UL, 1UL, 0UL], series!.Points.Select(p => p.Pageviews));
    }

    // ---- Retention ----

    private async Task<string> CreateStatement(string table)
    {
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("table", table);
        return (string)(await api.ClickHouse.ExecuteScalarAsync(
            "SELECT create_table_query FROM system.tables WHERE database = currentDatabase() AND name = {table:String}",
            parameters))!;
    }

    [Fact]
    public async Task Raw_events_are_kept_for_13_months()
    {
        Assert.Contains("toIntervalMonth(13)", await CreateStatement("events"));
    }

    [Fact]
    public async Task Hourly_rollups_are_kept_for_5_years()
    {
        Assert.Contains("toIntervalYear(5)", await CreateStatement("pageviews_hourly"));
    }
}
