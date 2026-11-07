using System.Net;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 7 step 3: creating and managing sites from the dashboard.
public class SiteManagementTests(ApiFixture api)
{
    private sealed record SiteDetails(
        uint Id, string Name, string Domain, string TrackingId, string VisitorIdMode,
        Dictionary<string, bool> StandardEvents);

    private sealed record TrackerConfig(string VisitorIdMode, string[] StandardEvents);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Domain() => $"{Guid.NewGuid():N}.test";

    private static async Task<SiteDetails> Create(HttpClient client, string name, string domain, string? visitorIdMode = null)
    {
        var response = await client.PostAsJsonAsync("/api/sites", new { name, domain, visitorIdMode }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SiteDetails>(Ct))!;
    }

    [Fact]
    public async Task Creating_a_site_gives_it_a_tracking_id_and_all_standard_events_on()
    {
        var domain = Domain();

        var site = await Create(api.Http, "My blog", domain);

        Assert.Equal("My blog", site.Name);
        Assert.Equal(domain, site.Domain);
        Assert.Matches("^ap_[0-9a-f]{16}$", site.TrackingId);
        Assert.Equal("hash", site.VisitorIdMode);
        Assert.Equal(8, site.StandardEvents.Count);
        Assert.All(site.StandardEvents.Values, Assert.True);

        // ...and the tracker can use it straight away.
        var config = await api.AnonymousClient().GetFromJsonAsync<TrackerConfig>($"/api/tracking/{site.TrackingId}/config", Ct);
        Assert.Equal(8, config!.StandardEvents.Length);
    }

    [Theory]
    [InlineData("https://www.{0}-Example.COM/some/path?x=1", "{0}-example.com")]
    [InlineData("  Shop.{0}.Example.com  ", "shop.{0}.example.com")]
    [InlineData("http://{0}.localhost:3000", "{0}.localhost")]
    public async Task Domains_are_normalized(string input, string expected)
    {
        // {0} = a unique word, so every run registers a new domain (domains are unique).
        var unique = Guid.NewGuid().ToString("N")[..8];

        var site = await Create(api.Http, "Site", string.Format(input, unique));

        Assert.Equal(string.Format(expected, unique), site.Domain);
    }

    [Theory]
    [InlineData("", "example.com", "name")]
    [InlineData("Site", "", "domain")]
    [InlineData("Site", "not a domain", "domain")]
    [InlineData("Site", "example", "domain")]
    public async Task Invalid_sites_are_a_400(string name, string domain, string field)
    {
        var response = await api.Http.PostAsJsonAsync("/api/sites", new { name, domain }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_domain_can_only_be_registered_once()
    {
        var domain = Domain();
        await Create(api.Http, "First", domain);

        var response = await api.Http.PostAsJsonAsync("/api/sites", new { name = "Second", domain = "www." + domain }, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task New_sites_belong_to_their_creator()
    {
        var (client, _) = await api.CreateUserClientAsync();
        var site = await Create(client, "Mine", Domain());

        var (other, _) = await api.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/sites/{site.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/sites/{site.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync($"/api/sites/{site.Id}", Ct)).StatusCode); // admin
    }

    [Fact]
    public async Task Settings_can_be_changed_and_the_tracker_sees_them_immediately()
    {
        var site = await Create(api.Http, "Before", Domain());
        // Warm the tracker's cache first, so this also proves it's invalidated.
        await api.AnonymousClient().GetFromJsonAsync<TrackerConfig>($"/api/tracking/{site.TrackingId}/config", Ct);

        var response = await api.Http.PatchAsJsonAsync($"/api/sites/{site.Id}", new
        {
            name = "After",
            visitorIdMode = "persistent",
            standardEvents = new Dictionary<string, bool> { ["scroll"] = false, ["form_start"] = false },
        }, Ct);

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<SiteDetails>(Ct))!;
        Assert.Equal("After", updated.Name);
        Assert.Equal("persistent", updated.VisitorIdMode);
        Assert.False(updated.StandardEvents["scroll"]);
        Assert.True(updated.StandardEvents["outbound_click"]);

        var config = await api.AnonymousClient().GetFromJsonAsync<TrackerConfig>($"/api/tracking/{site.TrackingId}/config", Ct);
        Assert.Equal("persistent", config!.VisitorIdMode);
        Assert.DoesNotContain("scroll", config.StandardEvents);
        Assert.Equal(6, config.StandardEvents.Length);
    }

    [Theory]
    [InlineData("""{ "visitorIdMode": "fingerprint" }""", "visitorIdMode")]
    [InlineData("""{ "standardEvents": { "page_view": false } }""", "standardEvents")]
    [InlineData("""{ "name": "" }""", "name")]
    public async Task Invalid_updates_are_a_400(string body, string field)
    {
        var site = await Create(api.Http, "Site", Domain());

        var response = await api.Http.PatchAsync($"/api/sites/{site.Id}",
            new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains($"\"{field}\"", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Regenerating_the_tracking_id_retires_the_old_one()
    {
        var site = await Create(api.Http, "Site", Domain());
        var anonymous = api.AnonymousClient();
        await anonymous.GetAsync($"/api/tracking/{site.TrackingId}/config", Ct); // cached

        var response = await api.Http.PostAsync($"/api/sites/{site.Id}/tracking-id", null, Ct);

        response.EnsureSuccessStatusCode();
        var updated = (await response.Content.ReadFromJsonAsync<SiteDetails>(Ct))!;
        Assert.NotEqual(site.TrackingId, updated.TrackingId);
        Assert.Matches("^ap_[0-9a-f]{16}$", updated.TrackingId);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync($"/api/tracking/{site.TrackingId}/config", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync($"/api/tracking/{updated.TrackingId}/config", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_site_removes_it_and_its_events()
    {
        var site = await Create(api.Http, "Doomed", Domain());
        await api.SeedPageviewsAsync(site.Id, [new SeedPageview(DateTime.UtcNow.AddMinutes(-5), "/")]);

        var response = await api.Http.DeleteAsync($"/api/sites/{site.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync($"/api/sites/{site.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await api.AnonymousClient().GetAsync($"/api/tracking/{site.TrackingId}/config", Ct)).StatusCode);
        Assert.Empty(await api.GetEventsAsync(site.Id));
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", site.Id);
        Assert.Equal(0UL, await api.ClickHouse.ExecuteScalarAsync(
            "SELECT count() FROM pageviews_hourly WHERE site_id = {site:UInt32}", parameters)); // the rollup too
    }

    [Fact]
    public async Task Other_users_cannot_change_or_delete_your_site()
    {
        var site = await Create(api.Http, "Admin's", Domain());
        var (other, _) = await api.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.PatchAsJsonAsync($"/api/sites/{site.Id}", new { name = "Mine now" }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/sites/{site.Id}/tracking-id", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/sites/{site.Id}", Ct)).StatusCode);
        Assert.Equal("Admin's", (await api.Http.GetFromJsonAsync<SiteDetails>($"/api/sites/{site.Id}", Ct))!.Name);
    }

    [Fact]
    public async Task Site_management_needs_a_login()
    {
        var anonymous = api.AnonymousClient();

        var response = await anonymous.PostAsJsonAsync("/api/sites", new { name = "x", domain = Domain() }, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
