using System.Text.Json;
using AnalyticsPlatform.Api.Sites;
using Microsoft.AspNetCore.Http.Features;
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

    // Events are tiny; anything bigger than this is junk or abuse.
    private const long MaxBodyBytes = 16 * 1024;

    // ASP.NET Core fills in every parameter for us: `httpContext` and
    // `cancellationToken` from the current request, the rest from the
    // dependency injection container.
    //
    // The return type lists every possible response (204, 400, or 413), which
    // also documents the endpoint for anyone reading it.
    private static async Task<Results<NoContent, ValidationProblem, StatusCodeHttpResult>> HandleEventAsync(
        HttpContext httpContext,
        SiteConfigStore siteConfigStore,
        VisitorIdentifier visitorIdentifier,
        SessionTracker sessionTracker,
        EventWriter eventWriter,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // We read the JSON body ourselves instead of taking an `EventRequest`
        // parameter, because ASP.NET Core's automatic binding only accepts
        // `Content-Type: application/json`. tracker.js sends `text/plain`: that
        // keeps the cross-site request "simple" so browsers skip the extra CORS
        // preflight (OPTIONS) request before every event.
        EventRequest? request;
        try
        {
            httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxBodyBytes;
            request = await JsonSerializer.DeserializeAsync<EventRequest>(
                httpContext.Request.Body, JsonSerializerOptions.Web, cancellationToken);
        }
        catch (JsonException)
        {
            return InvalidBody();
        }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (request is null)
        {
            return InvalidBody();
        }

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
                ReferrerDomain: ReferrerDomainOf(request.Referrer),
                UserAgent: userAgent,
                ScreenWidth: request.ScreenWidth ?? 0,
                ScreenHeight: request.ScreenHeight ?? 0,
                IpHash: visitor.IpHash,
                Properties: request.Properties ?? []),
            cancellationToken);

        return TypedResults.NoContent();
    }

    // "https://www.Google.com/search?q=x" -> "google.com", so "top referrers"
    // groups www and non-www together. Uri already lowercases the host.
    // `host[4..]` is a *range*: everything from index 4 to the end.
    // Full referrer/UTM parsing (search engine names, campaigns) is Phase 6.
    private static string ReferrerDomainOf(string? referrer)
    {
        if (!EventRequestValidator.IsHttpUrl(referrer, out var uri))
        {
            return "";
        }

        var host = uri.Host;
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static ValidationProblem InvalidBody() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["body"] = ["Must be a JSON event object."],
        });
}
