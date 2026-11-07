using System.Net;
using AnalyticsPlatform.Api.Enrichment;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace AnalyticsPlatform.Api.IntegrationTests;

// GeoIP at ingestion, end to end. The real MaxMind lookup is unit-tested
// separately; here a fake locator proves the wiring and the privacy rule:
// the raw IP is used for the lookup and then thrown away.
public class GeoTests(ApiFixture api)
{
    private const string ClientIp = "203.0.113.9"; // a documentation-only address (RFC 5737)

    private sealed class FakeGeoLocator : IGeoLocator
    {
        public GeoLocation Locate(IPAddress? ipAddress) =>
            ipAddress?.ToString() == ClientIp ? new GeoLocation("NL", "North Holland", "Amsterdam") : GeoLocation.Unknown;
    }

    // The in-memory test server has no real client IP. This runs first in the
    // pipeline and sets one, as if the request came from ClientIp.
    private sealed class FakeClientIp : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ClientIp);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private async Task<uint> SendOneEventFromClientIpAsync()
    {
        var site = await api.CreateSiteAsync();
        var factory = api.CreateFactory(b => b.ConfigureServices(services =>
        {
            services.AddSingleton<IGeoLocator, FakeGeoLocator>(); // registered last, so it wins
            services.AddSingleton<IStartupFilter, FakeClientIp>();
        }));
        await using (factory)
        {
            var response = await ApiFixture.PostEventAsync(factory.CreateClient(),
                new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        } // disposing flushes the queue

        return site;
    }

    [Fact]
    public async Task Country_region_and_city_are_stored()
    {
        var site = await SendOneEventFromClientIpAsync();

        var stored = Assert.Single(await api.WaitForEventsAsync(site, 1));
        Assert.Equal(new GeoLocation("NL", "North Holland", "Amsterdam"), stored.Geo);
    }

    [Fact]
    public async Task The_raw_ip_address_is_never_stored()
    {
        var site = await SendOneEventFromClientIpAsync();
        await api.WaitForEventsAsync(site, 1);

        // Render every column of the row as text and search it for the IP.
        var parameters = new ClickHouseParameterCollection();
        parameters.AddParameter("site", site);
        parameters.AddParameter("ip", ClientIp);
        var leaks = await api.ClickHouse.ExecuteScalarAsync(
            "SELECT countIf(position(formatRowNoNewline('TSV', *), {ip:String}) > 0) FROM events WHERE site_id = {site:UInt32}",
            parameters);

        Assert.Equal(0UL, Convert.ToUInt64(leaks));
    }

    [Fact]
    public async Task Without_a_geo_database_events_still_arrive()
    {
        // The shared test API has no GeoLite2 database configured.
        var site = await api.CreateSiteAsync();

        await api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") });

        Assert.Equal(GeoLocation.Unknown, Assert.Single(await api.WaitForEventsAsync(site, 1)).Geo);
    }
}
