using System.Text.Json;
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
        var body = await ReadBodyAsync(httpContext.Request, MaxBodyBytes, cancellationToken);
        if (body is null)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        EventRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<EventRequest>(body, JsonSerializerOptions.Web);
        }
        catch (JsonException)
        {
            return InvalidBody();
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
                ReferrerDomain: ReferrerDomain.From(request.Referrer),
                UserAgent: userAgent,
                ScreenWidth: request.ScreenWidth ?? 0,
                ScreenHeight: request.ScreenHeight ?? 0,
                IpHash: visitor.IpHash,
                Properties: request.Properties ?? []),
            cancellationToken);

        return TypedResults.NoContent();
    }

    // Reads the request body, but gives up (returns null) as soon as it's over
    // `maxBytes`, so a huge or endless upload can't eat memory. Done by hand
    // rather than via the server's IHttpMaxRequestBodySizeFeature, which not
    // every host provides (the in-memory test server doesn't, and the integration
    // tests caught the crash that caused).
    private static async Task<byte[]?> ReadBodyAsync(HttpRequest request, long maxBytes, CancellationToken cancellationToken)
    {
        if (request.ContentLength > maxBytes)
        {
            return null; // reject before reading anything
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static ValidationProblem InvalidBody() =>
        TypedResults.ValidationProblem(new Dictionary<string, string[]>
        {
            ["body"] = ["Must be a JSON event object."],
        });
}
