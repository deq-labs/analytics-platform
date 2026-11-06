using AnalyticsPlatform.Api.Sites;
using AnalyticsPlatform.Api.Visitors;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AnalyticsPlatform.Api.Ingestion;

public static class IngestionEndpoints
{
    // An *extension method* (note `this`): lets Program.cs write
    // `app.MapIngestionEndpoints()` as if it were built into ASP.NET Core.
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/events", HandleEventAsync);
        return app;
    }

    // ASP.NET Core fills in every parameter for us: `request` from the JSON body,
    // `httpContext` and `cancellationToken` from the current request, and the
    // rest from the dependency injection container.
    //
    // The return type lists every possible response (204 or 400), which also
    // documents the endpoint for anyone reading it.
    private static async Task<Results<NoContent, ValidationProblem>> HandleEventAsync(
        EventRequest request,
        HttpContext httpContext,
        SiteConfigStore siteConfigStore,
        VisitorIdentifier visitorIdentifier,
        SessionTracker sessionTracker,
        EventWriter eventWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var errors = EventRequestValidator.Validate(request);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var site = await siteConfigStore.GetAsync(request.SiteId, cancellationToken);
        if (site is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["siteId"] = ["Unknown site."],
            });
        }

        var userAgent = httpContext.Request.Headers.UserAgent.ToString();

        var visitor = await visitorIdentifier.IdentifyAsync(
            site,
            httpContext.Connection.RemoteIpAddress,
            userAgent,
            request.VisitorId,
            cancellationToken);

        var sessionId = sessionTracker.GetSessionId(site.Id, visitor.VisitorId);

        EventRequestValidator.IsHttpUrl(request.Url, out var url);

        await eventWriter.WriteAsync(
            new EventRow(
                SiteId: site.Id,
                EventType: request.Type,
                EventName: request.Name ?? "",
                Timestamp: timeProvider.GetUtcNow().UtcDateTime,
                SessionId: sessionId,
                VisitorId: visitor.VisitorId,
                Url: request.Url,
                Path: url.AbsolutePath,
                Hostname: url.Host,
                Referrer: request.Referrer ?? "",
                UserAgent: userAgent,
                ScreenWidth: request.ScreenWidth ?? 0,
                ScreenHeight: request.ScreenHeight ?? 0,
                IpHash: visitor.IpHash,
                Properties: request.Properties ?? []),
            cancellationToken);

        return TypedResults.NoContent();
    }
}
