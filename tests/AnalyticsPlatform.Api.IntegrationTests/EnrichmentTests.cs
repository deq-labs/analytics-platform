using System.Net;
using System.Net.Http.Json;
using AnalyticsPlatform.Api.Enrichment;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 6: browser/OS/device and UTM tags are filled in at ingestion, and bots
// are kept (flagged) but excluded from dashboard numbers.
public class EnrichmentTests(ApiFixture api)
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.7339.80 Safari/537.36";
    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
    private const string Googlebot =
        "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

    private sealed record Page(string Path, ulong Pageviews, ulong Visitors);
    private sealed record TopList<T>(DateTime From, DateTime To, T[] Items);

    [Fact]
    public async Task Browser_os_and_device_are_stored()
    {
        var site = await api.CreateSiteAsync();

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/desktop") }, userAgent: ChromeWindows);
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/phone") }, userAgent: SafariIphone);

        var events = await api.WaitForEventsAsync(site, 2);
        var desktop = Assert.Single(events, e => e.Path == "/desktop");
        var phone = Assert.Single(events, e => e.Path == "/phone");

        Assert.Equal(("Chrome", "140", "Windows", "10", "desktop", false),
            (desktop.Browser, desktop.BrowserVersion, desktop.Os, desktop.OsVersion, desktop.DeviceType, desktop.IsBot));
        Assert.Equal(("Mobile Safari", "iOS", "mobile"), (phone.Browser, phone.Os, phone.DeviceType));
    }

    [Fact]
    public async Task Utm_tags_are_read_from_the_page_url()
    {
        var site = await api.CreateSiteAsync();

        await api.PostEventAsync(new
        {
            trackingId = site.TrackingId,
            type = "pageview",
            url = site.Url("/sale?utm_source=newsletter&utm_medium=email&utm_campaign=black%20friday&utm_term=shoes&utm_content=hero"),
        }, userAgent: ChromeWindows);

        var stored = Assert.Single(await api.WaitForEventsAsync(site, 1));
        Assert.Equal(new UtmTags("newsletter", "email", "black friday", "shoes", "hero"), stored.Utm);
    }

    [Fact]
    public async Task Bots_are_stored_but_flagged()
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") }, userAgent: Googlebot);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); // don't tell bots anything
        Assert.True(Assert.Single(await api.WaitForEventsAsync(site, 1)).IsBot);
    }

    [Fact]
    public async Task Bot_traffic_is_left_out_of_stats()
    {
        var site = await api.CreateSiteAsync();
        var day = DateTime.UtcNow.Date.AddDays(-3);
        await api.SeedPageviewsAsync(site, [
            new(day.AddHours(1), "/human", "h:v1"),
            new(day.AddHours(2), "/bot-only", "h:bot", IsBot: true),
            new(day.AddHours(3), "/human", "h:bot", IsBot: true),
        ]);

        var result = await api.Http.GetFromJsonAsync<TopList<Page>>(
            $"/api/sites/{site}/stats/top-pages?from={day:yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}",
            TestContext.Current.CancellationToken);

        Assert.Equal([new Page("/human", 1, 1)], result!.Items);
    }
}
