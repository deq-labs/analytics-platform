using AnalyticsPlatform.Api.Auth;

namespace AnalyticsPlatform.Api.Sites;

// An *endpoint filter* for every route with {siteId} in it: it runs before the
// handler and lets the request through only if the signed-in user owns the
// site (or is the admin). Used by the stats and the site-management groups.
public static class SiteAccess
{
    // Attach to a route group: group.RequireSiteAccess().
    public static RouteGroupBuilder RequireSiteAccess(this RouteGroupBuilder group) =>
        group.AddEndpointFilter(FilterAsync);

    // A site you can't access answers 404, not 403: it shouldn't even reveal
    // that the site exists.
    private static async ValueTask<object?> FilterAsync(
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
}
