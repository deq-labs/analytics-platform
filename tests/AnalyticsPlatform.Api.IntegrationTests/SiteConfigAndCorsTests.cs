using System.Net;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

public class SiteConfigAndCorsTests(ApiFixture api)
{
    private sealed record ConfigResponse(string VisitorIdMode, string[] StandardEvents);

    [Fact]
    public async Task Config_lists_only_enabled_standard_events()
    {
        var site = await api.CreateSiteAsync("persistent", "scroll", "video_start");

        var config = await api.Http.GetFromJsonAsync<ConfigResponse>(
            $"/api/sites/{site}/config", TestContext.Current.CancellationToken);

        Assert.NotNull(config);
        Assert.Equal("persistent", config.VisitorIdMode);
        Assert.DoesNotContain("scroll", config.StandardEvents);
        Assert.DoesNotContain("video_start", config.StandardEvents);
        Assert.Contains("outbound_click", config.StandardEvents);
        Assert.Equal(6, config.StandardEvents.Length);
    }

    [Fact]
    public async Task Config_for_unknown_site_is_404()
    {
        var response = await api.Http.GetAsync("/api/sites/999999/config", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- CORS: tracker endpoints are open to every origin, dashboard reads are not.

    private async Task<HttpResponseMessage> SendFromOtherOrigin(HttpMethod method, string path, string? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Origin", "https://customer-site.example");
        if (body is not null) request.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/plain");
        return await api.Http.SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Tracker_endpoints_allow_any_origin()
    {
        var site = await api.CreateSiteAsync();

        var config = await SendFromOtherOrigin(HttpMethod.Get, $"/api/sites/{site}/config");
        var ingest = await SendFromOtherOrigin(HttpMethod.Post, "/api/events",
            $$"""{"siteId":{{site}},"type":"pageview","url":"https://customer-site.example/"}""");

        Assert.Equal("*", Assert.Single(config.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal("*", Assert.Single(ingest.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
    }

    [Theory]
    [InlineData("/api/sites")]
    [InlineData("/api/sites/1/stats/top-pages")]
    [InlineData("/health")]
    public async Task Dashboard_and_health_endpoints_send_no_cors_headers(string path)
    {
        var response = await SendFromOtherOrigin(HttpMethod.Get, path);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
