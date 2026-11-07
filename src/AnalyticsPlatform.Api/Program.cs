using System.Threading.RateLimiting;
using AnalyticsPlatform.Api.Auth;
using AnalyticsPlatform.Api.Enrichment;
using AnalyticsPlatform.Api.Health;
using AnalyticsPlatform.Api.Ingestion;
using AnalyticsPlatform.Api.Sites;
using AnalyticsPlatform.Api.Stats;
using AnalyticsPlatform.Api.Visitors;
using ClickHouse.Driver;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// --- Configuration -----------------------------------------------------------
// Connection strings come from configuration, never from code. In Development
// they're read from user secrets (`dotnet user-secrets list` to see them); in
// Docker/production they'll come from environment variables such as
// ConnectionStrings__ClickHouse. Fail at startup if either is missing, rather
// than on the first request.
var clickHouseConnectionString = builder.Configuration.GetConnectionString("ClickHouse")
    ?? throw new InvalidOperationException("Missing connection string 'ClickHouse'.");
var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException("Missing connection string 'Postgres'.");

// --- Services (dependency injection) ------------------------------------------
// ClickHouseClient is thread-safe and designed to be created once and shared, so
// it's registered as a singleton. Same for NpgsqlDataSource, which owns the
// Postgres connection pool. The container disposes both on shutdown.
builder.Services.AddSingleton(_ => new ClickHouseClient(clickHouseConnectionString));
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(postgresConnectionString));

builder.Services.AddHealthChecks()
    .AddCheck<ClickHouseHealthCheck>("clickhouse")
    .AddCheck<PostgresHealthCheck>("postgres");

// Ingestion. All singletons: each holds no per-request state, so one shared
// instance is safe and cheapest. TimeProvider is .NET's injectable clock, so
// tests can fake "now" instead of depending on the real time.
builder.Services.AddMemoryCache();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SiteConfigStore>();
// "When something asks for IDailySaltProvider, give it the DailySaltStore."
builder.Services.AddSingleton<IDailySaltProvider, DailySaltStore>();
builder.Services.AddSingleton<VisitorIdentifier>();
builder.Services.AddSingleton<SessionTracker>();
builder.Services.AddSingleton<UserAgentParser>(); // Phase 6 enrichment (caches per user agent)

// GeoIP: reads the GeoLite2 file that the geoipupdate container keeps fresh.
// A relative path is resolved against the content root (the project folder).
builder.Services.AddOptions<GeoIpOptions>()
    .Bind(builder.Configuration.GetSection("GeoIp"))
    .PostConfigure<IHostEnvironment>((o, env) =>
    {
        if (o.DatabasePath.Length > 0 && !Path.IsPathRooted(o.DatabasePath))
        {
            o.DatabasePath = Path.GetFullPath(Path.Combine(env.ContentRootPath, o.DatabasePath));
        }
    });
builder.Services.AddSingleton<IGeoLocator, MaxMindGeoLocator>();

// Buffered ingestion (Phase 5): endpoint -> EventQueue -> EventBatchWriter -> ClickHouse.
// Settings come from the "Ingestion" config section; ValidateOnStart makes a bad
// value fail at startup instead of at the first event.
builder.Services.AddOptions<IngestionOptions>()
    .Bind(builder.Configuration.GetSection("Ingestion"))
    .Validate(o => o.QueueCapacity > 0 && o.MaxBatchSize > 0 && o.FlushInterval > TimeSpan.Zero,
        "Ingestion: QueueCapacity, MaxBatchSize and FlushInterval must be positive.")
    .ValidateOnStart();
builder.Services.AddSingleton<EventQueue>();
builder.Services.AddSingleton<IEventStore, ClickHouseEventStore>();
// A hosted service: the host starts it with the app and stops it on shutdown.
builder.Services.AddHostedService<EventBatchWriter>();

// Dashboard queries.
builder.Services.AddSingleton<StatsQueries>();
builder.Services.AddSingleton<SiteStore>(); // create / change / delete sites (Phase 7 step 3)

// CORS: browsers block a page on site A from reading responses from site B
// (this API) unless B says it's allowed. The tracker runs on customers' sites,
// i.e. on any origin, so the tracker endpoints allow every origin -- but never
// cookies/credentials, so no visitor login state can ever be sent here.
const string TrackerCorsPolicy = "Tracker";
builder.Services.AddCors(options => options.AddPolicy(TrackerCorsPolicy, policy => policy
    .AllowAnyOrigin()
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type")));

// --- Accounts and login (Phase 7) ------------------------------------------------
builder.Services.AddOptions<AuthOptions>().Bind(builder.Configuration.GetSection("Auth"));
builder.Services.AddSingleton<UserStore>();
// ASP.NET Core Identity's password hasher (PBKDF2, per-user salt, versioned
// format) -- used on its own, without the rest of Identity or Entity Framework.
builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();

// Signing in sets an encrypted session cookie the browser sends back on every
// request. HttpOnly: JavaScript can't read it (stops token theft via XSS).
// SameSite=Strict: browsers never send it on requests started by other sites,
// which is what defends cookie auth against cross-site request forgery (CSRF).
// Two ways to be signed in, picked per request by a "policy scheme": a
// secret API key in `Authorization: Bearer apk_...` (servers, read-only; see
// Auth/ApiKeys.cs), otherwise the dashboard's session cookie.
const string CookieOrApiKey = "CookieOrApiKey";
builder.Services.AddAuthentication(CookieOrApiKey)
    .AddPolicyScheme(CookieOrApiKey, null, options => options.ForwardDefaultSelector = context =>
        ApiKeyAuthenticationHandler.BearerKey(context.Request) is null
            ? CookieAuthenticationDefaults.AuthenticationScheme
            : ApiKeys.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeys.Scheme, null)
    .AddCookie(options =>
    {
        options.Cookie.Name = "ap_session";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest; // Secure whenever served over HTTPS
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
        // This is an API: answer 401/403 instead of redirecting to a login page.
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        // Check every session against the database (deleted / demoted users).
        options.Events.OnValidatePrincipal = AuthEndpoints.ValidateSessionAsync;
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthEndpoints.AdminPolicy, policy => policy.RequireAssertion(c => c.User.IsAdmin()));
    options.AddPolicy(AuthEndpoints.SessionPolicy, policy => policy
        .AddAuthenticationSchemes(CookieAuthenticationDefaults.AuthenticationScheme)
        .RequireAuthenticatedUser());
});

// --- Deployment -----------------------------------------------------------------
// Behind a reverse proxy (Caddy, nginx, a cloud load balancer) the API sees the
// proxy's IP and plain HTTP. ForwardedHeaders:Enabled makes it believe the
// proxy's X-Forwarded-For / X-Forwarded-Proto instead -- real client IPs for
// GeoIP, visitor hashing and rate limiting, and Secure cookies. Only enable it
// when the API is reachable ONLY through the proxy, otherwise anyone could send
// those headers. Optionally pin the proxy with ForwardedHeaders:KnownProxies.
var forwardedHeadersEnabled = builder.Configuration.GetValue<bool>("ForwardedHeaders:Enabled");
if (forwardedHeadersEnabled)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        var knownProxies = builder.Configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
        // The defaults trust only localhost; in a container the proxy has another IP.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var proxy in knownProxies)
        {
            options.KnownProxies.Add(System.Net.IPAddress.Parse(proxy));
        }
    });
}

// The session cookie is encrypted with ASP.NET Core Data Protection keys. By
// default they live in the user profile, which a container loses on restart
// (signing everyone out). DataProtection:KeysPath = a persistent volume.
if (builder.Configuration["DataProtection:KeysPath"] is { Length: > 0 } keysPath)
{
    builder.Services.AddDataProtection()
        .SetApplicationName("AnalyticsPlatform")
        .PersistKeysToFileSystem(new DirectoryInfo(keysPath));
}

// Login/register attempts per client IP per minute (Auth:AttemptsPerMinute).
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(AuthEndpoints.RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = context.RequestServices.GetRequiredService<IOptions<AuthOptions>>().Value.AttemptsPerMinute,
            Window = TimeSpan.FromMinutes(1),
        }));
});

var app = builder.Build();

// --- Middleware (runs on every request, in this order) ---------------------------
if (forwardedHeadersEnabled)
{
    app.UseForwardedHeaders(); // first, so everything after sees the real client
}

app.UseStaticFiles(); // serves files in wwwroot/, e.g. /tracker.js
app.UseCors();
app.UseAuthentication(); // reads the session cookie -> HttpContext.User
app.UseAuthorization();  // enforces RequireAuthorization() on endpoints
app.UseRateLimiter();

// --- Endpoints -----------------------------------------------------------------
app.MapGet("/", () => "Analytics Platform API");
app.MapHealthChecks("/health", new() { ResponseWriter = HealthResponseWriter.WriteJson });

// A route group applies shared settings (here: the CORS policy) to every
// endpoint mapped inside it.
var trackerApi = app.MapGroup("").RequireCors(TrackerCorsPolicy);
trackerApi.MapIngestionEndpoints();
trackerApi.MapSiteEndpoints();

// Dashboard: accounts + reads. Deliberately outside the tracker group, so no
// CORS for any origin, and they require a signed-in user.
app.MapAuthEndpoints();
app.MapStatsEndpoints();
app.MapSiteManagementEndpoints();
app.MapApiKeyEndpoints();

// A test page that loads tracker.js, only when running locally.
if (app.Environment.IsDevelopment())
{
    // Injects the sites (with tracking IDs) so the page can pick the one whose
    // domain matches the host it was opened on.
    app.MapGet("/demo", async (NpgsqlDataSource dataSource) =>
    {
        await using var command = dataSource.CreateCommand(
            "SELECT name, domain, tracking_id FROM sites ORDER BY id");
        await using var reader = await command.ExecuteReaderAsync();
        var sites = new List<object>();
        while (await reader.ReadAsync())
        {
            sites.Add(new { name = reader.GetString(0), domain = reader.GetString(1), trackingId = reader.GetString(2) });
        }

        var html = await File.ReadAllTextAsync(
            Path.Combine(app.Environment.ContentRootPath, "DevPages", "demo.html"));
        return Results.Content(
            html.Replace("/*__SITES__*/[]", System.Text.Json.JsonSerializer.Serialize(sites)), "text/html");
    });
}

app.Run();
