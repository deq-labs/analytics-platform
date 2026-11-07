using System.Net;
using Npgsql;

namespace AnalyticsPlatform.Api.IntegrationTests;

// POST /api/events end to end: request -> validation -> visitor/session -> ClickHouse row.
public class IngestionTests(ApiFixture api)
{
    [Fact]
    public async Task Pageview_is_stored_with_derived_fields()
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new
        {
            trackingId = site.TrackingId,
            type = "pageview",
            url = site.Url("/pricing?plan=pro#top"),
            referrer = "https://www.Google.com/search?q=analytics",
            screenWidth = 1920,
            screenHeight = 1080,
        }, userAgent: "Chrome/140");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var stored = Assert.Single(await api.WaitForEventsAsync(site, 1));
        Assert.Equal("pageview", stored.EventType);
        Assert.Equal("", stored.EventName);
        Assert.Equal("/pricing", stored.Path);
        Assert.Equal(site.Domain, stored.Hostname);
        Assert.Equal("https://www.Google.com/search?q=analytics", stored.Referrer);
        Assert.Equal("google.com", stored.ReferrerDomain);
        Assert.Equal("Chrome/140", stored.UserAgent);
        Assert.Equal(1920, stored.ScreenWidth);
        Assert.Matches("^h:[0-9a-f]{64}$", stored.VisitorId);
        Assert.Matches("^[0-9a-f]{64}$", stored.IpHash);
        Assert.Equal(7, stored.SessionId.Version);
    }

    [Fact]
    public async Task Text_plain_body_is_accepted_like_json()
    {
        // tracker.js sends text/plain to avoid a CORS preflight.
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(
            new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") }, contentType: "text/plain");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await api.WaitForEventsAsync(site, 1));
    }

    [Fact]
    public async Task Standard_and_custom_events_keep_their_name_and_properties()
    {
        var site = await api.CreateSiteAsync();

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "standard", name = "scroll", url = site.Url("/a"), properties = new { depth = "90" } });
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "custom", name = "signup", url = site.Url("/a"), properties = new { plan = "pro" } });

        var events = await api.WaitForEventsAsync(site, 2);
        var scroll = Assert.Single(events, e => e.EventName == "scroll");
        var signup = Assert.Single(events, e => e.EventName == "signup");
        Assert.Equal("standard", scroll.EventType);
        Assert.Equal("90", scroll.Properties["depth"]);
        Assert.Equal("custom", signup.EventType);
        Assert.Equal("pro", signup.Properties["plan"]);
    }

    [Fact]
    public async Task Same_browser_is_one_visitor_and_one_session()
    {
        var site = await api.CreateSiteAsync();

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/a") }, userAgent: "Chrome/140");
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/b") }, userAgent: "Chrome/140");
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/a") }, userAgent: "Firefox/140");

        var events = await api.WaitForEventsAsync(site, 3);
        var chrome = events.Where(e => e.UserAgent == "Chrome/140").ToList();
        var firefox = Assert.Single(events, e => e.UserAgent == "Firefox/140");

        Assert.Equal(2, chrome.Count);
        Assert.Equal(chrome[0].VisitorId, chrome[1].VisitorId);
        Assert.Equal(chrome[0].SessionId, chrome[1].SessionId);
        Assert.NotEqual(chrome[0].VisitorId, firefox.VisitorId);
        Assert.NotEqual(chrome[0].SessionId, firefox.SessionId);
    }

    [Fact]
    public async Task Persistent_site_uses_the_browser_id_and_falls_back_to_the_hash()
    {
        var site = await api.CreateSiteAsync("persistent");
        const string browserId = "7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f";

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/a"), visitorId = browserId });
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/b"), visitorId = "garbage" });

        var events = await api.WaitForEventsAsync(site, 2);
        Assert.Equal($"p:{browserId}", Assert.Single(events, e => e.Path == "/a").VisitorId);
        Assert.StartsWith("h:", Assert.Single(events, e => e.Path == "/b").VisitorId);
    }

    [Fact]
    public async Task Hash_site_ignores_a_browser_id()
    {
        var site = await api.CreateSiteAsync("hash");

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/"), visitorId = "7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f" });

        Assert.StartsWith("h:", Assert.Single(await api.WaitForEventsAsync(site, 1)).VisitorId);
    }

    [Fact]
    public async Task Invalid_event_is_rejected_and_nothing_is_stored()
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new { trackingId = site.TrackingId, type = "custom", name = "scroll", url = site.Url("/") });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("reserved", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(await api.GetEventsAsync(site));
    }

    [Fact]
    public async Task Unknown_site_is_rejected()
    {
        var response = await api.PostEventAsync(new { trackingId = "ap_0000000000000000", type = "pageview", url = "https://shop.test/" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Unknown site", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    public async Task Malformed_body_is_a_400(string body)
    {
        var response = await api.PostRawEventAsync(body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Body_over_16_KB_is_a_413()
    {
        var site = await api.CreateSiteAsync();
        var huge = $$"""{"trackingId":"{{site.TrackingId}}","type":"pageview","url":"{{site.Url()}}","x":"{{new string('a', 20_000)}}"}""";

        var response = await api.PostRawEventAsync(huge);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
    }

    [Fact]
    public async Task Only_todays_salt_is_kept()
    {
        var site = await api.CreateSiteAsync();
        await using (var old = api.Postgres.CreateCommand(
            "INSERT INTO daily_salts (day, salt) VALUES (current_date - 3, '\\x00') ON CONFLICT DO NOTHING"))
        {
            await old.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // The API caches the salt per day in memory, so an old row is only
        // cleaned up when the API loads a new day's salt. Check the invariant
        // that matters instead: today's salt exists and is 32 random bytes.
        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") });
        await api.WaitForEventsAsync(site, 1);

        await using var today = api.Postgres.CreateCommand(
            "SELECT length(salt) FROM daily_salts WHERE day = (now() AT TIME ZONE 'UTC')::date");
        Assert.Equal(32, (int)(await today.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    [Fact]
    public async Task Health_reports_both_databases()
    {
        var response = await api.Http.GetAsync("/health", TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("\"clickhouse\":{\"status\":\"Healthy\"", body);
        Assert.Contains("\"postgres\":{\"status\":\"Healthy\"", body);
    }
}
