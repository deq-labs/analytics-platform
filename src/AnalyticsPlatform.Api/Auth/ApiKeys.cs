using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Npgsql;

namespace AnalyticsPlatform.Api.Auth;

public sealed record ApiKeyInfo(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);
public sealed record CreatedApiKey(int Id, string Name, string Prefix, string Key, DateTime CreatedAt);
public sealed record CreateApiKeyRequest(string? Name);

// Secret API keys (Phase 7 step 4; table in postgres/init/005_api_keys.sql).
//
//   GET    /api/keys        your keys (never the secret itself)
//   POST   /api/keys        create one -- the response is the only time the key is shown
//   DELETE /api/keys/{id}   revoke
//
// Then call the read endpoints with `Authorization: Bearer apk_...`.
public static class ApiKeys
{
    public const string Scheme = "ApiKey";
    public const string Prefix = "apk_";
    public const string AuthMethodClaim = "auth_method";

    public static IEndpointRouteBuilder MapApiKeyEndpoints(this IEndpointRouteBuilder app)
    {
        // Managing keys needs a real login: a key can't mint more keys.
        var keys = app.MapGroup("/api/keys").RequireAuthorization(AuthEndpoints.SessionPolicy);
        keys.MapGet("", ListAsync);
        keys.MapPost("", CreateAsync);
        keys.MapDelete("/{id:int}", DeleteAsync);
        return app;
    }

    // "apk_" + 32 random bytes as URL-safe base64 (43 characters).
    public static string NewKey() =>
        Prefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    private static async Task<Ok<List<ApiKeyInfo>>> ListAsync(
        ClaimsPrincipal user, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name, prefix, created_at, last_used_at FROM api_keys WHERE user_id = $1 ORDER BY created_at");
        command.Parameters.AddWithValue(user.UserId());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var keys = new List<ApiKeyInfo>();
        while (await reader.ReadAsync(cancellationToken))
        {
            keys.Add(new ApiKeyInfo(reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
                reader.GetDateTime(3), reader.IsDBNull(4) ? null : reader.GetDateTime(4)));
        }

        return TypedResults.Ok(keys);
    }

    private static async Task<Results<Created<CreatedApiKey>, ValidationProblem>> CreateAsync(
        CreateApiKeyRequest request, ClaimsPrincipal user, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is 0 or > 100)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["name"] = ["Must be 1 to 100 characters."] });
        }

        var key = NewKey();
        var prefix = key[..12]; // "apk_" + 8 characters: enough to recognise, useless to attack

        await using var command = dataSource.CreateCommand("""
            INSERT INTO api_keys (user_id, name, prefix, key_hash) VALUES ($1, $2, $3, $4)
            RETURNING id, created_at
            """);
        command.Parameters.AddWithValue(user.UserId());
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(prefix);
        command.Parameters.AddWithValue(Hash(key));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        return TypedResults.Created((string?)null, new CreatedApiKey(reader.GetInt32(0), name, prefix, key, reader.GetDateTime(1)));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        int id, ClaimsPrincipal user, NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        // "AND user_id = $2": you can only delete your own keys.
        await using var command = dataSource.CreateCommand("DELETE FROM api_keys WHERE id = $1 AND user_id = $2");
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(user.UserId());
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1 ? TypedResults.NoContent() : TypedResults.NotFound();
    }
}

// An authentication handler: ASP.NET Core calls it to turn a request into a
// user (ClaimsPrincipal). This one looks for `Authorization: Bearer apk_...`,
// hashes the key and looks the hash up. The resulting user has just an id --
// no email and, deliberately, no admin claim -- plus auth_method=api_key.
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    NpgsqlDataSource dataSource)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var key = BearerKey(Request);
        if (key is null)
        {
            return AuthenticateResult.NoResult(); // not our kind of request
        }

        // One round trip: find the key and record that it was used.
        await using var command = dataSource.CreateCommand(
            "UPDATE api_keys SET last_used_at = now() WHERE key_hash = $1 RETURNING user_id");
        command.Parameters.AddWithValue(ApiKeys.Hash(key));
        if (await command.ExecuteScalarAsync(Context.RequestAborted) is not int userId)
        {
            return AuthenticateResult.Fail("Unknown API key.");
        }

        Claim[] claims =
        [
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ApiKeys.AuthMethodClaim, "api_key"),
        ];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ApiKeys.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ApiKeys.Scheme));
    }

    // The key from "Authorization: Bearer apk_...", or null.
    public static string? BearerKey(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        const string bearer = "Bearer ";
        return header.StartsWith(bearer, StringComparison.OrdinalIgnoreCase)
               && header.AsSpan(bearer.Length).Trim().StartsWith(ApiKeys.Prefix, StringComparison.Ordinal)
            ? header[bearer.Length..].Trim()
            : null;
    }
}
