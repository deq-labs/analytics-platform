using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 5: ingestion is asynchronous. The API answers 202 as soon as an event is
// queued; a background writer inserts batches into ClickHouse.
public class BatchingTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_burst_of_concurrent_events_all_arrive()
    {
        var site = await api.CreateSiteAsync();

        var responses = await Task.WhenAll(Enumerable.Range(0, 300).Select(i =>
            api.PostEventAsync(new { trackingId = site.TrackingId, type = "pageview", url = site.Url($"/p/{i}") })));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Accepted, r.StatusCode));
        var stored = await api.WaitForEventsAsync(site, 300);
        Assert.Equal(300, stored.Count);
        Assert.Equal(300, stored.Select(e => e.Path).Distinct().Count()); // no duplicates
    }

    [Fact]
    public async Task Events_buffered_at_shutdown_are_written_before_the_api_stops()
    {
        var site = await api.CreateSiteAsync();

        // A separate API instance that would never flush on its own.
        var factory = api.CreateFactory(b => b.UseSetting("Ingestion:FlushInterval", "01:00:00"));
        var http = factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var response = await ApiFixture.PostEventAsync(http, new { trackingId = site.TrackingId, type = "pageview", url = site.Url($"/{i}") });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        await Task.Delay(300, Ct);
        Assert.Empty(await api.GetEventsAsync(site)); // proves they're buffered, not written yet

        await factory.DisposeAsync(); // graceful shutdown

        Assert.Equal(5, (await api.GetEventsAsync(site)).Count);
    }

    [Fact]
    public async Task A_full_queue_answers_503_instead_of_growing_forever()
    {
        var site = await api.CreateSiteAsync();

        // ClickHouse "down" (nothing listens on port 1), a tiny queue, and a
        // short shutdown deadline so disposing this instance doesn't hang.
        var factory = api.CreateFactory(b =>
        {
            b.UseSetting("ConnectionStrings:ClickHouse", "Host=127.0.0.1;Port=1;Username=x;Password=x");
            b.UseSetting("Ingestion:QueueCapacity", "1");
            b.UseSetting("Ingestion:MaxBatchSize", "1");
            b.ConfigureServices(s => s.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(1)));
        });
        await using (factory)
        {
            var http = factory.CreateClient();

            var responses = new List<HttpResponseMessage>();
            for (var i = 0; i < 5; i++)
            {
                responses.Add(await ApiFixture.PostEventAsync(http, new { trackingId = site.TrackingId, type = "pageview", url = site.Url("/") }));
            }

            var rejected = responses.Where(r => r.StatusCode == HttpStatusCode.ServiceUnavailable).ToList();
            Assert.NotEmpty(rejected);
            Assert.All(rejected, r => Assert.True(r.Headers.RetryAfter is not null, "503 should say when to retry"));
        }
    }
}
