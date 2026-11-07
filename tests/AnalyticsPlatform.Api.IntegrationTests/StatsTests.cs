using System.Net;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

// The stats endpoints against known data seeded straight into ClickHouse, so
// every expected number can be worked out by hand.
public class StatsTests(ApiFixture api)
{
    private sealed record Point(DateTime Time, ulong Pageviews, ulong Visitors);
    private sealed record Series(DateTime From, DateTime To, string Interval, Point[] Points);
    private sealed record Page(string Path, ulong Pageviews, ulong Visitors);
    private sealed record Referrer(string? Domain, ulong Sessions, ulong Visitors);
    private sealed record TopList<T>(DateTime From, DateTime To, T[] Items);
    private sealed record Site(uint Id, string Name, string Domain);

    // A day well inside the 1-year TTL, relative to today so the tests keep working.
    private static readonly DateTime Day = DateTime.UtcNow.Date.AddDays(-10);

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private Task<T?> Get<T>(string url) => api.Http.GetFromJsonAsync<T>(url, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Top_pages_counts_pageviews_and_visitors_per_path()
    {
        var site = await api.CreateSiteAsync();
        var other = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site, [
            new(Day.AddHours(1), "/a", "h:v1"),
            new(Day.AddHours(2), "/a", "h:v2"),
            new(Day.AddHours(3), "/a", "h:v2"),
            new(Day.AddHours(4), "/b", "h:v1"),
            new(Day.AddHours(5), "/b", "h:v3"),
            new(Day.AddHours(6), "/c", "h:v1"),
            new(Day.AddHours(7), "/z", "h:v1", EventType: "standard")]); // not a pageview
        await api.SeedPageviewsAsync(other, [ new(Day.AddHours(1), "/a", "h:v9")]); // other site

        var result = await Get<TopList<Page>>(
            $"/api/sites/{site}/stats/top-pages?from={Iso(Day)}&to={Iso(Day.AddDays(1))}&limit=2");

        Assert.Equal([new Page("/a", 3, 2), new Page("/b", 2, 2)], result!.Items);
    }

    [Fact]
    public async Task Daily_series_fills_empty_days_with_zeros()
    {
        var site = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site, [
            new(Day.AddHours(9), "/", "h:v1"),
            new(Day.AddHours(10), "/", "h:v2"),
            new(Day.AddDays(2).AddHours(8), "/", "h:v1")]);

        var result = await Get<Series>(
            $"/api/sites/{site}/stats/pageviews?from={Iso(Day)}&to={Iso(Day.AddDays(3))}&interval=day");

        Assert.Equal("day", result!.Interval);
        Assert.Equal(
            [new Point(Day, 2, 2), new Point(Day.AddDays(1), 0, 0), new Point(Day.AddDays(2), 1, 1)],
            result.Points);
    }

    [Fact]
    public async Task Hourly_series_buckets_by_hour()
    {
        var site = await api.CreateSiteAsync();
        var ten = Day.AddHours(10);
        await api.SeedPageviewsAsync(site, [
            new(ten.AddMinutes(15), "/", "h:v1"),
            new(ten.AddMinutes(45), "/", "h:v1"),
            new(ten.AddHours(2).AddMinutes(5), "/", "h:v2")]);

        var result = await Get<Series>(
            $"/api/sites/{site}/stats/pageviews?from={Iso(ten)}&to={Iso(ten.AddHours(3))}&interval=hour");

        Assert.Equal(
            [new Point(ten, 2, 1), new Point(ten.AddHours(1), 0, 0), new Point(ten.AddHours(2), 1, 1)],
            result!.Points);
    }

    [Fact]
    public async Task Range_includes_from_and_excludes_to()
    {
        var site = await api.CreateSiteAsync();
        var from = Day.AddHours(12);
        var to = Day.AddHours(13);
        await api.SeedPageviewsAsync(site, [
            new(from, "/included"),
            new(to, "/excluded")]);

        var result = await Get<TopList<Page>>(
            $"/api/sites/{site}/stats/top-pages?from={Iso(from)}&to={Iso(to)}");

        Assert.Equal("/included", Assert.Single(result!.Items).Path);
    }

    [Fact]
    public async Task Referrers_are_counted_per_session_by_entry_page()
    {
        var site = await api.CreateSiteAsync();
        Guid S() => Guid.NewGuid();
        var (s1, s2, s3, s4, s5) = (S(), S(), S(), S(), S());
        await api.SeedPageviewsAsync(site, [
            // s1: arrives from Google, then browses internally (no referrer).
            new(Day.AddHours(1), "/", "h:v1", s1, "google.com"),
            new(Day.AddHours(1).AddMinutes(2), "/pricing", "h:v1", s1, ""),
            // s2: typed the URL (direct).
            new(Day.AddHours(2), "/", "h:v2", s2, ""),
            // s3: Reddit.
            new(Day.AddHours(3), "/blog", "h:v3", s3, "reddit.com"),
            // s4: Google again, different visitor.
            new(Day.AddHours(4), "/", "h:v4", s4, "google.com"),
            // s5: entry referrer is the site itself (session resumed) -> direct.
            new(Day.AddHours(5), "/docs", "h:v5", s5, "site.test", Hostname: "www.site.test")]);

        var result = await Get<TopList<Referrer>>(
            $"/api/sites/{site}/stats/top-referrers?from={Iso(Day)}&to={Iso(Day.AddDays(1))}");

        // Ties are ordered by name; direct ("") sorts first.
        Assert.Equal(
            [new Referrer(null, 2, 2), new Referrer("google.com", 2, 2), new Referrer("reddit.com", 1, 1)],
            result!.Items);
    }

    [Fact]
    public async Task Sites_list_includes_new_sites()
    {
        var site = await api.CreateSiteAsync();

        var sites = await Get<Site[]>("/api/sites");

        Assert.Contains(sites!, s => s.Id == site);
    }

    [Theory]
    [InlineData("pageviews")]
    [InlineData("top-pages")]
    [InlineData("top-referrers")]
    public async Task Unknown_site_is_404(string endpoint)
    {
        var response = await api.Http.GetAsync($"/api/sites/999999/stats/{endpoint}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("pageviews?interval=week")]
    [InlineData("pageviews?from=2026-11-05&to=2026-11-01")]
    [InlineData("top-pages?limit=0")]
    [InlineData("top-referrers?from=yesterday")]
    public async Task Invalid_parameters_are_400(string query)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.Http.GetAsync($"/api/sites/{site}/stats/{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
