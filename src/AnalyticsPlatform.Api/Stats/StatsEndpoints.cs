using System.Security.Claims;
using AnalyticsPlatform.Api.Auth;
using AnalyticsPlatform.Api.Sites;
using Microsoft.AspNetCore.Http.HttpResults;
using Npgsql;

namespace AnalyticsPlatform.Api.Stats;

// Read endpoints for the dashboard:
//   GET /api/sites/{siteId}/stats/pageviews      ?from&to&interval=hour|day
//   GET /api/sites/{siteId}/stats/top-pages      ?from&to&limit
//   GET /api/sites/{siteId}/stats/top-referrers  ?from&to&limit
//   GET /api/sites/{siteId}/stats/breakdown      ?dimension=browser|os|device|country&from&to&limit
//
// All of these need a signed-in user who owns the site (or is the admin).
public static class StatsEndpoints
{
    public static IEndpointRouteBuilder MapStatsEndpoints(this IEndpointRouteBuilder app)
    {
        // The dashboard's site picker: your own sites (an admin sees all).
        app.MapGet("/api/sites", ListSitesAsync).RequireAuthorization();

        // Every stats endpoint needs a login AND access to the site in the URL.
        // An *endpoint filter* runs before each handler in the group, so the
        // check lives in one place instead of in every handler.
        var stats = app.MapGroup("/api/sites/{siteId}/stats")
            .RequireAuthorization()
            .AddEndpointFilter(RequireSiteAccessAsync);
        stats.MapGet("/pageviews", GetPageviewsAsync);
        stats.MapGet("/top-pages", GetTopPagesAsync);
        stats.MapGet("/top-referrers", GetTopReferrersAsync);
        stats.MapGet("/breakdown", GetBreakdownAsync);
        return app;
    }

    // A site you don't own answers 404, not 403: it shouldn't even reveal that
    // the site exists.
    private static async ValueTask<object?> RequireSiteAccessAsync(
        EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        if (!uint.TryParse(http.Request.RouteValues["siteId"]?.ToString(), out var siteId))
        {
            return TypedResults.NotFound();
        }

        var site = await http.RequestServices.GetRequiredService<SiteConfigStore>().GetAsync(siteId, http.RequestAborted);
        if (site is null || !(http.User.IsAdmin() || site.OwnerUserId == http.User.UserId()))
        {
            return TypedResults.NotFound();
        }

        return await next(context);
    }

    private static async Task<Ok<List<SiteSummary>>> ListSitesAsync(
        ClaimsPrincipal user, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT id, name, domain, tracking_id FROM sites
            WHERE $1 OR owner_user_id = $2
            ORDER BY name
            """);
        command.Parameters.AddWithValue(user.IsAdmin());
        command.Parameters.AddWithValue(user.UserId());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var sites = new List<SiteSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            sites.Add(new SiteSummary((uint)reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetString(3)));
        }

        return TypedResults.Ok(sites);
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

    // ?dimension=browser|os|device|country (required), plus from/to/limit.
    private static async Task<Results<Ok<BreakdownResponse>, NotFound, ValidationProblem>> GetBreakdownAsync(
        uint siteId,
        [AsParameters] StatsRequest request,
        SiteConfigStore sites,
        StatsQueries queries,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        // Non-short-circuiting `&`: run every check so all problems are reported at once.
        var valid = request.TryGetRange(timeProvider, errors, out var range)
                    & request.TryGetLimit(errors, out var limit)
                    & request.TryGetDimension(errors, out var dimension);
        if (!valid)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (await sites.GetAsync(siteId, cancellationToken) is null)
        {
            return TypedResults.NotFound();
        }

        var items = await queries.BreakdownAsync(siteId, range, dimension, limit, cancellationToken);
        return TypedResults.Ok(new BreakdownResponse(
            range.From, range.To, dimension.ToString().ToLowerInvariant(), items));
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
