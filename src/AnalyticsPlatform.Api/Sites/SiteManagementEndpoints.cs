using System.Security.Claims;
using AnalyticsPlatform.Api.Auth;
using AnalyticsPlatform.Api.Ingestion;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AnalyticsPlatform.Api.Sites;

public sealed record CreateSiteRequest(string? Name, string? Domain, string? VisitorIdMode);

// PATCH semantics: only the fields you send change; null/missing = leave as is.
public sealed record UpdateSiteRequest(string? Name, string? VisitorIdMode, Dictionary<string, bool>? StandardEvents);

// Site management for the dashboard (Phase 7 step 3):
//   POST   /api/sites                          create (you become the owner)
//   GET    /api/sites/{siteId}                 settings
//   PATCH  /api/sites/{siteId}                 change name / visitor-ID mode / standard events
//   POST   /api/sites/{siteId}/tracking-id     replace the tracking ID with a new one
//   DELETE /api/sites/{siteId}                 delete the site and all its events
// (GET /api/sites, the list, lives in StatsEndpoints.)
public static class SiteManagementEndpoints
{
    private static readonly string[] VisitorIdModes = ["hash", "persistent"];

    public static IEndpointRouteBuilder MapSiteManagementEndpoints(this IEndpointRouteBuilder app)
    {
        // Changing settings needs a dashboard login -- an API key (step 4) is read-only.
        app.MapPost("/api/sites", CreateAsync).RequireAuthorization(AuthEndpoints.SessionPolicy);

        var site = app.MapGroup("/api/sites/{siteId}")
            .RequireAuthorization(AuthEndpoints.SessionPolicy)
            .RequireSiteAccess();
        site.MapGet("", GetAsync);
        site.MapPatch("", UpdateAsync);
        site.MapPost("/tracking-id", RegenerateTrackingIdAsync);
        site.MapDelete("", DeleteAsync);
        return app;
    }

    private static async Task<Results<Created<SiteDetails>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateSiteRequest request, ClaimsPrincipal user, SiteStore sites, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = ValidateName(request.Name, errors);
        if (!SiteDomain.TryNormalizeForRegistration(request.Domain, out var domain))
        {
            errors["domain"] = ["Must be a domain name like example.com."];
        }

        var mode = request.VisitorIdMode ?? "hash";
        ValidateMode(mode, errors);
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var (outcome, created) = await sites.CreateAsync(user.UserId(), name!, domain, mode, cancellationToken);
        if (outcome == SiteStore.CreateOutcome.DomainTaken)
        {
            return TypedResults.Problem($"{domain} is already registered.", statusCode: StatusCodes.Status409Conflict);
        }

        return TypedResults.Created($"/api/sites/{created!.Id}", created);
    }

    private static async Task<Results<Ok<SiteDetails>, NotFound>> GetAsync(
        uint siteId, SiteStore sites, CancellationToken cancellationToken) =>
        await sites.GetAsync(siteId, cancellationToken) is { } site ? TypedResults.Ok(site) : TypedResults.NotFound();

    private static async Task<Results<Ok<SiteDetails>, NotFound, ValidationProblem>> UpdateAsync(
        uint siteId, UpdateSiteRequest request, SiteStore sites, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var name = request.Name is null ? null : ValidateName(request.Name, errors);
        if (request.VisitorIdMode is not null)
        {
            ValidateMode(request.VisitorIdMode, errors);
        }

        var unknown = request.StandardEvents?.Keys.Where(k => !EventTypes.StandardEventNames.Contains(k)).ToList();
        if (unknown is { Count: > 0 })
        {
            errors["standardEvents"] = [$"Unknown standard event(s): {string.Join(", ", unknown)}."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        await sites.UpdateAsync(siteId, name, request.VisitorIdMode, request.StandardEvents, cancellationToken);
        return await sites.GetAsync(siteId, cancellationToken) is { } site ? TypedResults.Ok(site) : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<SiteDetails>, NotFound>> RegenerateTrackingIdAsync(
        uint siteId, SiteStore sites, CancellationToken cancellationToken)
    {
        await sites.RegenerateTrackingIdAsync(siteId, cancellationToken);
        return await GetAsync(siteId, sites, cancellationToken);
    }

    private static async Task<NoContent> DeleteAsync(uint siteId, SiteStore sites, CancellationToken cancellationToken)
    {
        await sites.DeleteAsync(siteId, cancellationToken);
        return TypedResults.NoContent();
    }

    private static string? ValidateName(string? name, Dictionary<string, string[]> errors)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > 100)
        {
            errors["name"] = ["Must be 1 to 100 characters."];
            return null;
        }

        return trimmed;
    }

    private static void ValidateMode(string mode, Dictionary<string, string[]> errors)
    {
        if (!VisitorIdModes.Contains(mode))
        {
            errors["visitorIdMode"] = ["Must be 'hash' or 'persistent'."];
        }
    }
}
