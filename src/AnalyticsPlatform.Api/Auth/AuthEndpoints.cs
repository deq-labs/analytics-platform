using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using AnalyticsPlatform.Api.Sites;
using Microsoft.Extensions.Options;

namespace AnalyticsPlatform.Api.Auth;

public sealed record RegisterRequest(string? Email, string? Password, string? InviteToken);
public sealed record LoginRequest(string? Email, string? Password);
public sealed record UserInfo(int Id, string Email, bool IsAdmin);
public sealed record AuthStatus(bool SetupRequired, bool OpenRegistration);
public sealed record InviteResponse(string Token, DateTime ExpiresAt);

//   GET  /api/auth/status    anonymous: first-run setup needed? open sign-up?
//   POST /api/auth/register  first account = admin; then invite-only unless Auth:OpenRegistration
//   POST /api/auth/login     sets the session cookie
//   POST /api/auth/logout
//   GET  /api/auth/me        who am I (401 if not signed in)
//   POST /api/admin/invites  admin only: a one-time sign-up token
//   GET  /api/admin/users     admin only: everyone, with how many sites they own
//   DELETE /api/admin/users/{id}  admin only: the user, their sites and all their data
public static class AuthEndpoints
{
    public const string AdminPolicy = "Admin";
    // Signed in through the dashboard (a session cookie), not with an API key.
    public const string SessionPolicy = "Session";
    public const string RateLimitPolicy = "auth";
    private const string AdminClaim = "admin";

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var auth = app.MapGroup("/api/auth");
        auth.MapGet("/status", GetStatusAsync);
        auth.MapPost("/register", RegisterAsync).RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/login", LoginAsync).RequireRateLimiting(RateLimitPolicy);
        auth.MapPost("/logout", LogoutAsync);
        auth.MapGet("/me", GetMe).RequireAuthorization(SessionPolicy);

        app.MapPost("/api/admin/invites", CreateInviteAsync).RequireAuthorization(AdminPolicy);
        app.MapGet("/api/admin/users", ListUsersAsync).RequireAuthorization(AdminPolicy);
        app.MapDelete("/api/admin/users/{id:int}", DeleteUserAsync).RequireAuthorization(AdminPolicy);
        return app;
    }

    // ---- Claims: what the session cookie remembers about you ----

    public static int UserId(this ClaimsPrincipal user) =>
        int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    public static bool IsAdmin(this ClaimsPrincipal user) => user.HasClaim(AdminClaim, "true");

    private static ClaimsPrincipal PrincipalFor(AppUser user)
    {
        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Email, user.Email),
            new(AdminClaim, user.IsAdmin ? "true" : "false"),
        ];
        return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
    }

    private static Task SignInAsync(HttpContext http, AppUser user) =>
        http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, PrincipalFor(user),
            new AuthenticationProperties { IsPersistent = true });

    // Runs on every request that arrives with a session cookie (wired up in
    // Program.cs as the cookie's OnValidatePrincipal event). The cookie is only
    // a snapshot from sign-in time, so check it against the database:
    //   * user deleted          -> reject the cookie (401) and clear it;
    //   * admin flag changed    -> swap in fresh claims and re-issue the cookie.
    // One primary-key lookup per dashboard request -- cheap for a dashboard.
    //
    // Only for endpoints that require a user: tracker requests (/api/events) can
    // arrive with the cookie too (a tracked page on the dashboard's own domain),
    // and checking the database for each of those would be pure waste -- a
    // burst of events exhausted the connection pool in a test.
    public static async Task ValidateSessionAsync(CookieValidatePrincipalContext context)
    {
        if (context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAuthorizeData>() is null)
        {
            return;
        }

        var users = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
        var principal = context.Principal!;
        var user = await users.FindByIdAsync(principal.UserId(), context.HttpContext.RequestAborted);
        if (user is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        if (user.IsAdmin != principal.IsAdmin() || user.Email != principal.FindFirstValue(ClaimTypes.Email))
        {
            context.ReplacePrincipal(PrincipalFor(user));
            context.ShouldRenew = true;
        }
    }

    // ---- Endpoints ----

    private static async Task<Ok<AuthStatus>> GetStatusAsync(
        UserStore users, IOptions<AuthOptions> options, CancellationToken cancellationToken) =>
        TypedResults.Ok(new AuthStatus(
            SetupRequired: !await users.AnyUsersAsync(cancellationToken),
            OpenRegistration: options.Value.OpenRegistration));

    private static async Task<Results<Created<UserInfo>, ValidationProblem, ProblemHttpResult>> RegisterAsync(
        RegisterRequest request,
        HttpContext http,
        UserStore users,
        IPasswordHasher<AppUser> hasher,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var email = request.Email?.Trim() ?? "";
        var password = request.Password ?? "";

        var errors = new Dictionary<string, string[]>();
        if (email.Length is 0 or > 254 || !email.Contains('@') || email.Contains(' '))
        {
            errors["email"] = ["Must be a valid email address."];
        }

        if (password.Length < settings.MinPasswordLength || password.Length > 128)
        {
            errors["password"] = [$"Must be {settings.MinPasswordLength} to 128 characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var hash = hasher.HashPassword(null!, password); // the user object isn't used by the default hasher
        var inviteHash = string.IsNullOrEmpty(request.InviteToken) ? null : HashToken(request.InviteToken);

        var (outcome, user) = await users.RegisterAsync(email, hash, inviteHash, settings.OpenRegistration, cancellationToken);
        switch (outcome)
        {
            case RegisterOutcome.RegistrationClosed:
                return TypedResults.Problem("Registration is by invite only.", statusCode: StatusCodes.Status403Forbidden);
            case RegisterOutcome.InvalidInvite:
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["inviteToken"] = ["This invite is invalid, expired, or already used."],
                });
            case RegisterOutcome.EmailTaken:
                return TypedResults.Problem("An account with this email already exists.", statusCode: StatusCodes.Status409Conflict);
        }

        await SignInAsync(http, user!);
        return TypedResults.Created("/api/auth/me", new UserInfo(user!.Id, user.Email, user.IsAdmin));
    }

    // Computed once: a real hash to "check" against when the email is unknown.
    private static readonly string DummyHash = new PasswordHasher<AppUser>().HashPassword(null!, "dummy password for timing");

    private static async Task<Results<NoContent, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        HttpContext http,
        UserStore users,
        IPasswordHasher<AppUser> hasher,
        CancellationToken cancellationToken)
    {
        var user = await users.FindByEmailAsync(request.Email?.Trim() ?? "", cancellationToken);

        // Always run the (deliberately slow) hash check, even for unknown emails,
        // so response time doesn't reveal which emails have accounts. Same message
        // either way, for the same reason.
        var result = hasher.VerifyHashedPassword(user!, user?.PasswordHash ?? DummyHash, request.Password ?? "");
        if (user is null || result == PasswordVerificationResult.Failed)
        {
            return TypedResults.Problem("Invalid email or password.", statusCode: StatusCodes.Status401Unauthorized);
        }

        await SignInAsync(http, user);
        return TypedResults.NoContent();
    }

    private static async Task<NoContent> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync();
        return TypedResults.NoContent();
    }

    private static Ok<UserInfo> GetMe(ClaimsPrincipal user) =>
        TypedResults.Ok(new UserInfo(user.UserId(), user.FindFirstValue(ClaimTypes.Email)!, user.IsAdmin()));

    private static async Task<Created<InviteResponse>> CreateInviteAsync(
        ClaimsPrincipal user,
        UserStore users,
        TimeProvider timeProvider,
        IOptions<AuthOptions> options,
        CancellationToken cancellationToken)
    {
        // 32 random bytes, URL-safe. Shown once; only its hash is stored.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime + options.Value.InviteLifetime;

        await users.CreateInviteAsync(user.UserId(), HashToken(token), expiresAt, cancellationToken);
        return TypedResults.Created((string?)null, new InviteResponse(token, expiresAt));
    }

    private static async Task<Ok<List<UserSummary>>> ListUsersAsync(UserStore users, CancellationToken cancellationToken) =>
        TypedResults.Ok(await users.ListAsync(cancellationToken));

    private static async Task<Results<NoContent, NotFound, ProblemHttpResult>> DeleteUserAsync(
        int id, ClaimsPrincipal admin, UserStore users, SiteStore sites, CancellationToken cancellationToken)
    {
        if (id == admin.UserId())
        {
            return TypedResults.Problem("You can't delete your own account.", statusCode: StatusCodes.Status400BadRequest);
        }

        // Sites first, through SiteStore, so their ClickHouse events go too
        // (the Postgres cascade alone would leave the events behind).
        foreach (var siteId in await users.SiteIdsOwnedByAsync(id, cancellationToken))
        {
            await sites.DeleteAsync(siteId, cancellationToken);
        }

        // Their session dies on its next request (ValidateSessionAsync).
        return await users.DeleteAsync(id, cancellationToken) ? TypedResults.NoContent() : TypedResults.NotFound();
    }

    private static byte[] HashToken(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));
}
