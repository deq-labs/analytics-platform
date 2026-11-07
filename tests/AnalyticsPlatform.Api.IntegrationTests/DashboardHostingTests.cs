using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace AnalyticsPlatform.Api.IntegrationTests;

// In production the API also serves the built dashboard (dashboard/dist copied
// into its web root by the Dockerfile), so browser, dashboard and API share one
// origin -- which the SameSite=Strict session cookie needs.
public class DashboardHostingTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string WebRootWithDashboard()
    {
        var folder = Directory.CreateTempSubdirectory("ap-webroot-").FullName;
        File.WriteAllText(Path.Combine(folder, "index.html"), "<!doctype html><title>Dashboard</title>");
        Directory.CreateDirectory(Path.Combine(folder, "assets"));
        File.WriteAllText(Path.Combine(folder, "assets", "app.js"), "console.log('app')");
        return folder;
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/?view=settings&site=3")]
    [InlineData("/some/client/route")]
    public async Task The_dashboard_is_served_for_page_requests(string path)
    {
        var root = WebRootWithDashboard();
        await using var factory = api.CreateFactory(b => b.UseWebRoot(root));

        var response = await factory.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<title>Dashboard</title>", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Assets_are_served_as_files()
    {
        await using var factory = api.CreateFactory(b => b.UseWebRoot(WebRootWithDashboard()));

        var body = await factory.CreateClient().GetStringAsync("/assets/app.js", Ct);

        Assert.Equal("console.log('app')", body);
    }

    [Theory]
    [InlineData("/api/does-not-exist")]
    [InlineData("/assets/missing.js")]
    public async Task Unknown_api_routes_and_files_stay_404s(string path)
    {
        // Never answer a mistyped API call or a missing asset with the HTML page.
        await using var factory = api.CreateFactory(b => b.UseWebRoot(WebRootWithDashboard()));

        var response = await factory.CreateClient().GetAsync(path, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Without_a_built_dashboard_the_root_still_answers()
    {
        // Development: the dashboard runs on Vite, the API's web root has only tracker.js.
        var response = await api.Http.GetAsync("/", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Analytics Platform API", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Tracker_js_is_still_served()
    {
        var response = await api.AnonymousClient().GetAsync("/tracker.js", Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
