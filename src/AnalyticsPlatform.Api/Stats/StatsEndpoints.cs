using AnalyticsPlatform.Api.Sites;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AnalyticsPlatform.Api.Stats;

// Read endpoints for the dashboard:
//   GET /api/sites/{siteId}/stats/pageviews      ?from&to&interval=hour|day
//   GET /api/sites/{siteId}/stats/top-pages      ?from&to&limit
//   GET /api/sites/{siteId}/stats/top-referrers  ?from&to&limit
//
// No authentication yet -- that's Phase 7. Until then, don't expose this API
// beyond your own machine.
public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        var stats = app.MapGroup("/api/sites/{siteId}/stats");
        stats.MapGet("/pageviews", GetPageviewsAsync);
        stats.MapGet("/top-pages", GetTopPagesAsync);
        stats.MapGet("/top-referrers", GetTopReferrersAsync);
        return app;
    }

    private static async Task<Results<Ok<TimeSeriesResponse>, NotFound, ValidationProblem>> GetPageviewsAsync(
        uint siteId,
        [AsParameters] StatsRequest request,
        SiteConfigStore sites,
        StatsQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.TryGetRange(timeProvider, errors, out var range) ||
            !request.TryGetInterval(range, errors, out var interval))
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await sites.GetAsync(siteId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var points = await queries.PageviewsOverTimeAsync(siteId, range, interval, cancellationToken);
        return TypedResults.Ok(new TimeSeriesResponse(
            range.From, range.To, interval.ToString().ToLowerInvariant(), points));
    }

    private static async Task<Results<Ok<TopListResponse<TopPage>>, NotFound, ValidationProblem>> GetTopPagesAsync(
        uint siteId,
        [AsParameters] StatsRequest request,
        SiteConfigStore sites,
        StatsQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.TryGetRange(timeProvider, errors, out var range) ||
            !request.TryGetLimit(errors, out var limit))
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await sites.GetAsync(siteId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var pages = await queries.TopPagesAsync(siteId, range, limit, cancellationToken);
        return TypedResults.Ok(new TopListResponse<TopPage>(range.From, range.To, pages));
    }

    private static async Task<Results<Ok<TopListResponse<TopReferrer>>, NotFound, ValidationProblem>> GetTopReferrersAsync(
        uint siteId,
        [AsParameters] StatsRequest request,
        SiteConfigStore sites,
        StatsQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (!request.TryGetRange(timeProvider, errors, out var range) ||
            !request.TryGetLimit(errors, out var limit))
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await sites.GetAsync(siteId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var referrers = await queries.TopReferrersAsync(siteId, range, limit, cancellationToken);
        return TypedResults.Ok(new TopListResponse<TopReferrer>(range.From, range.To, referrers));
    }
}
