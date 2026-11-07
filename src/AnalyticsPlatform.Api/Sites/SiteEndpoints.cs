using Microsoft.AspNetCore.Http.HttpResults;

namespace AnalyticsPlatform.Api.Sites;

// What tracker.js receives from GET /api/sites/{siteId}/config, e.g.
//   { "visitorIdMode": "hash", "standardEvents": ["scroll", "outbound_click"] }
public sealed record SiteConfigResponse(string VisitorIdMode, IReadOnlyList<string> StandardEvents);

public static class SiteEndpoints
{
    public static IEndpointRouteBuilder MapSiteEndpoints(this IEndpointRouteBuilder app)
    {
        // `{siteId}` in the route becomes the `siteId` parameter below.
        // `:int` would reject non-numbers; uint parsing does the same for us.
        app.MapGet("/api/sites/{siteId}/config", GetConfigAsync);
        return app;
    }

    private static async Task<Results<Ok<SiteConfigResponse>, NotFound>> GetConfigAsync(
        uint siteId,
        HttpContext httpContext,
        SiteConfigStore siteConfigStore,
        CancellationToken cancellationToken)
    {
        var site = await siteConfigStore.GetAsync(siteId, cancellationToken);
        if (site is null)
        {
            return TypedResults.NotFound();
        }

        // Every page load on a tracked site asks for this, so let browsers reuse
        // it for a minute. Toggling an event takes up to ~2 minutes to apply
        // (this + the server-side cache), which is fine for settings.
        httpContext.Response.Headers.CacheControl = "public, max-age=60";

        return TypedResults.Ok(new SiteConfigResponse(
            VisitorIdMode: site.VisitorIdMode == VisitorIdMode.Persistent ? "persistent" : "hash",
            StandardEvents: site.EnabledStandardEvents));
    }
}
