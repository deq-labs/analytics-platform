using System.Net;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

// GET /api/sites/{id}/stats/breakdown?dimension=browser|os|device|country
// (the dashboard's "Locations" and "Devices" cards).
public class BreakdownTests(ApiFixture api)
{
    private sealed record Item(string? Value, ulong Visitors, ulong Pageviews);
    private sealed record Breakdown(DateTime From, DateTime To, string Dimension, Item[] Items);

    private static readonly DateTime Day = DateTime.UtcNow.Date.AddDays(-5);

    private async Task<Breakdown> Get(uint site, string dimension, string extra = "")
    {
        var result = await api.Http.GetFromJsonAsync<Breakdown>(
            $"/api/sites/{site}/stats/breakdown?dimension={dimension}&from={Day:yyyy-MM-dd}&to={Day.AddDays(1):yyyy-MM-dd}{extra}",
            TestContext.Current.CancellationToken);
        return result!;
    }

    // v1 + v2 on Chrome, v3 on Firefox, v4 unknown browser, plus noise that
    // must not count: a bot, a non-pageview event, and another site.
    private async Task<uint> SeedAsync()
    {
        var site = await api.CreateSiteAsync();
        var other = await api.CreateSiteAsync();
        await api.SeedPageviewsAsync(site, [
            new(Day.AddHours(1), "/", "h:v1", Browser: "Chrome", Os: "Windows", DeviceType: "desktop", CountryCode: "US"),
            new(Day.AddHours(2), "/a", "h:v1", Browser: "Chrome", Os: "Windows", DeviceType: "desktop", CountryCode: "US"),
            new(Day.AddHours(3), "/", "h:v2", Browser: "Chrome", Os: "Android", DeviceType: "mobile", CountryCode: "DE"),
            new(Day.AddHours(4), "/", "h:v3", Browser: "Firefox", Os: "Windows", DeviceType: "desktop", CountryCode: "US"),
            new(Day.AddHours(5), "/", "h:v4"), // nothing known
            new(Day.AddHours(6), "/", "h:bot", Browser: "Chrome", IsBot: true),
            new(Day.AddHours(7), "/", "h:v1", EventType: "standard", Browser: "Chrome"),
        ]);
        await api.SeedPageviewsAsync(other, [new(Day.AddHours(1), "/", "h:x", Browser: "Safari")]);
        return site;
    }

    [Fact]
    public async Task Browsers_ranked_by_visitors_with_unknown_as_null()
    {
        var site = await SeedAsync();

        var result = await Get(site, "browser");

        Assert.Equal("browser", result.Dimension);
        Assert.Equal(
            [new Item("Chrome", 2, 3), new Item(null, 1, 1), new Item("Firefox", 1, 1)],
            result.Items);
    }

    [Fact]
    public async Task Operating_systems()
    {
        var site = await SeedAsync();

        Assert.Equal(
            [new Item("Windows", 2, 3), new Item(null, 1, 1), new Item("Android", 1, 1)],
            (await Get(site, "os")).Items);
    }

    [Fact]
    public async Task Device_types()
    {
        var site = await SeedAsync();

        Assert.Equal(
            [new Item("desktop", 2, 3), new Item(null, 1, 1), new Item("mobile", 1, 1)],
            (await Get(site, "device")).Items);
    }

    [Fact]
    public async Task Countries_are_iso_codes()
    {
        var site = await SeedAsync();

        Assert.Equal(
            [new Item("US", 2, 3), new Item(null, 1, 1), new Item("DE", 1, 1)],
            (await Get(site, "country")).Items);
    }

    [Fact]
    public async Task Limit_caps_the_list()
    {
        var site = await SeedAsync();

        Assert.Single((await Get(site, "browser", "&limit=1")).Items);
    }

    [Theory]
    [InlineData("")]
    [InlineData("&dimension=user_agent")]
    public async Task Missing_or_unknown_dimension_is_400(string query)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.Http.GetAsync(
            $"/api/sites/{site}/stats/breakdown?x=1{query}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Unknown_site_is_404()
    {
        var response = await api.Http.GetAsync(
            "/api/sites/999999/stats/breakdown?dimension=browser", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
