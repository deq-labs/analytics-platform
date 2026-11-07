using System.Net;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 7, step 1: the snippet identifies its site by a random public tracking
// ID (like GA's measurement ID), and events are only accepted from the site's
// own domain.
public class TrackingIdTests(ApiFixture api)
{
    private sealed record Site(uint Id, string Name, string Domain, string TrackingId);

    [Fact]
    public async Task Every_site_gets_a_distinct_random_tracking_id()
    {
        var a = await api.CreateSiteAsync();
        var b = await api.CreateSiteAsync();

        Assert.Matches("^ap_[0-9a-f]{16}$", a.TrackingId);
        Assert.NotEqual(a.TrackingId, b.TrackingId);
    }

    [Theory]
    [InlineData("/")]                  // the site's own domain
    public async Task Events_from_the_sites_domain_are_accepted(string path)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url(path) });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Single(await api.WaitForEventsAsync(site, 1));
    }

    [Theory]
    [InlineData("www.")]
    [InlineData("blog.")]
    [InlineData("deep.sub.")]
    public async Task Www_and_subdomains_are_accepted(string prefix)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new
        {
            trackingId = site.TrackingId,
            type = "pageview",
            url = $"https://{prefix}{site.Domain}/",
        });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.test/")]                     // someone else's site
    [InlineData("https://not{0}/")]                        // ends with the domain, but isn't a subdomain
    [InlineData("https://{0}.evil.test/")]                 // starts with the domain
    public async Task Events_from_other_domains_are_rejected_and_not_stored(string urlTemplate)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new
        {
            trackingId = site.TrackingId,
            type = "pageview",
            url = string.Format(urlTemplate, site.Domain),
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("domain", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(await api.GetEventsAsync(site));
    }

    [Fact]
    public async Task The_old_numeric_site_id_is_no_longer_accepted()
    {
        var site = await api.CreateSiteAsync();

        var response = await api.PostEventAsync(new { siteId = site.Id, type = "pageview", url = site.Url() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("trackingId", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Sites_list_includes_the_tracking_id_for_the_snippet()
    {
        var site = await api.CreateSiteAsync();

        var sites = await api.Http.GetFromJsonAsync<Site[]>("/api/sites", TestContext.Current.CancellationToken);

        Assert.Equal(site.TrackingId, Assert.Single(sites!, s => s.Id == site.Id).TrackingId);
    }
}
