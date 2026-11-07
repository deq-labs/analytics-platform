using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Npgsql;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 7 step 2: accounts, login, and who can see what.
public class AuthTests(ApiFixture api)
{
    private sealed record Status(bool SetupRequired, bool OpenRegistration);
    private sealed record Site(uint Id, string Name, string Domain, string TrackingId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Task<HttpResponseMessage> Register(HttpClient client, string email, string password, string? inviteToken = null) =>
        client.PostAsJsonAsync("/api/auth/register", new { email, password, inviteToken }, Ct);

    private static Task<HttpResponseMessage> Login(HttpClient client, string email, string password) =>
        client.PostAsJsonAsync("/api/auth/login", new { email, password }, Ct);

    private static string Email() => $"{Guid.NewGuid():N}@integration.test";

    // ---- First run (each test gets its own empty database) ----

    [Fact]
    public async Task A_fresh_install_asks_for_setup()
    {
        await using var factory = api.CreateFactory(postgresConnectionString: await api.CreateFreshPostgresDatabaseAsync());
        var client = factory.CreateClient();

        var status = await client.GetFromJsonAsync<Status>("/api/auth/status", Ct);

        Assert.Equal(new Status(SetupRequired: true, OpenRegistration: false), status);
    }

    [Fact]
    public async Task The_first_account_becomes_admin_and_claims_unowned_sites()
    {
        var database = await api.CreateFreshPostgresDatabaseAsync();
        await using (var dataSource = NpgsqlDataSource.Create(database))
        await using (var insert = dataSource.CreateCommand("INSERT INTO sites (name, domain) VALUES ('Old site', 'old.test')"))
        {
            await insert.ExecuteNonQueryAsync(Ct); // a site from before accounts existed
        }

        await using var factory = api.CreateFactory(postgresConnectionString: database);
        var client = factory.CreateClient();

        var user = await ApiFixture.RegisterAsync(client, Email(), ApiFixture.TestPassword);

        Assert.True(user.IsAdmin);
        Assert.Contains(await client.GetFromJsonAsync<Site[]>("/api/sites", Ct) ?? [], s => s.Domain == "old.test");
        Assert.False((await client.GetFromJsonAsync<Status>("/api/auth/status", Ct))!.SetupRequired);
    }

    [Fact]
    public async Task After_setup_registration_needs_an_invite_by_default()
    {
        await using var factory = api.CreateFactory(postgresConnectionString: await api.CreateFreshPostgresDatabaseAsync());
        await ApiFixture.RegisterAsync(factory.CreateClient(), Email(), ApiFixture.TestPassword); // the admin

        var response = await Register(factory.CreateClient(), Email(), ApiFixture.TestPassword);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Open_registration_lets_anyone_sign_up_as_a_normal_user()
    {
        await using var factory = api.CreateFactory(
            b => b.UseSetting("Auth:OpenRegistration", "true"),
            postgresConnectionString: await api.CreateFreshPostgresDatabaseAsync());
        await ApiFixture.RegisterAsync(factory.CreateClient(), Email(), ApiFixture.TestPassword); // the admin

        var second = await ApiFixture.RegisterAsync(factory.CreateClient(), Email(), ApiFixture.TestPassword);

        Assert.False(second.IsAdmin);
        Assert.True((await factory.CreateClient().GetFromJsonAsync<Status>("/api/auth/status", Ct))!.OpenRegistration);
    }

    // ---- Invites ----

    [Fact]
    public async Task An_invite_works_once()
    {
        var token = await api.CreateInviteAsync();

        var first = await Register(api.AnonymousClient(), Email(), ApiFixture.TestPassword, token);
        var second = await Register(api.AnonymousClient(), Email(), ApiFixture.TestPassword, token);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
    }

    [Fact]
    public async Task An_expired_invite_is_rejected()
    {
        var token = await api.CreateInviteAsync();
        await using (var expire = api.Postgres.CreateCommand("UPDATE invites SET expires_at = now() - interval '1 minute'"))
        {
            await expire.ExecuteNonQueryAsync(Ct);
        }

        var response = await Register(api.AnonymousClient(), Email(), ApiFixture.TestPassword, token);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_admins_can_create_invites()
    {
        var (user, _) = await api.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsync("/api/admin/invites", null, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.AnonymousClient().PostAsync("/api/admin/invites", null, Ct)).StatusCode);
    }

    // ---- Registration rules ----

    [Theory]
    [InlineData("not-an-email", ApiFixture.TestPassword, "email")]
    [InlineData("ok@integration.test", "short", "password")] // under 12 characters
    public async Task Invalid_registration_is_a_400(string email, string password, string field)
    {
        var response = await Register(api.AnonymousClient(), email, password, await api.CreateInviteAsync());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Emails_are_unique_ignoring_case()
    {
        var response = await Register(api.AnonymousClient(), ApiFixture.AdminEmail.ToUpperInvariant(),
            ApiFixture.TestPassword, await api.CreateInviteAsync());

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Passwords_are_stored_hashed()
    {
        await using var command = api.Postgres.CreateCommand("SELECT password_hash FROM users WHERE id = $1");
        command.Parameters.AddWithValue(api.AdminUserId);
        var hash = (string)(await command.ExecuteScalarAsync(Ct))!;

        Assert.DoesNotContain(ApiFixture.TestPassword, hash);
        Assert.StartsWith("AQAAAA", hash); // ASP.NET Core Identity v3 format (PBKDF2)
    }

    // ---- Login / logout ----

    [Fact]
    public async Task Login_me_and_logout()
    {
        var client = api.AnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);

        var login = await Login(client, ApiFixture.AdminEmail, ApiFixture.TestPassword);
        Assert.Equal(HttpStatusCode.NoContent, login.StatusCode);
        var me = await client.GetFromJsonAsync<UserInfo>("/api/auth/me", Ct);
        Assert.Equal((api.AdminUserId, true), (me!.Id, me.IsAdmin));

        await client.PostAsync("/api/auth/logout", null, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me", Ct)).StatusCode);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_email() =>
        Assert.Equal(HttpStatusCode.NoContent,
            (await Login(api.AnonymousClient(), ApiFixture.AdminEmail.ToUpperInvariant(), ApiFixture.TestPassword)).StatusCode);

    [Fact]
    public async Task Wrong_password_and_unknown_email_look_identical()
    {
        // Don't reveal which emails have accounts.
        var wrongPassword = await Login(api.AnonymousClient(), ApiFixture.AdminEmail, "not the password at all");
        var unknownEmail = await Login(api.AnonymousClient(), "nobody@integration.test", "not the password at all");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownEmail.StatusCode);
        Assert.Equal(await wrongPassword.Content.ReadAsStringAsync(Ct), await unknownEmail.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_session_cookie_is_http_only_and_same_site_strict()
    {
        var response = await Login(api.AnonymousClient(), ApiFixture.AdminEmail, ApiFixture.TestPassword);

        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ap_session="));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);  // JavaScript can't read it
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase); // not sent by other sites
    }

    [Fact]
    public async Task Repeated_login_attempts_are_rate_limited()
    {
        await using var factory = api.CreateFactory(b => b.UseSetting("Auth:AttemptsPerMinute", "3"));
        var client = factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await Login(client, ApiFixture.AdminEmail, "wrong password guess")).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[^1]);
    }

    // ---- Who can see what ----

    [Theory]
    [InlineData("/api/sites")]
    [InlineData("/api/sites/{0}/stats/pageviews")]
    [InlineData("/api/sites/{0}/stats/top-pages")]
    [InlineData("/api/sites/{0}/stats/top-referrers")]
    [InlineData("/api/sites/{0}/stats/breakdown?dimension=browser")]
    public async Task Dashboard_endpoints_need_a_login(string pathTemplate)
    {
        var site = await api.CreateSiteAsync();

        var response = await api.AnonymousClient().GetAsync(string.Format(pathTemplate, site.Id), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tracking_endpoints_stay_public()
    {
        var site = await api.CreateSiteAsync();
        var visitor = api.AnonymousClient();

        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync($"/api/tracking/{site.TrackingId}/config", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await ApiFixture.PostEventAsync(visitor,
            new { trackingId = site.TrackingId, type = "pageview", url = site.Url() })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/health", Ct)).StatusCode);
    }

    [Fact]
    public async Task Users_only_see_their_own_sites_and_admins_see_all()
    {
        var (bob, bobUser) = await api.CreateUserClientAsync();
        var adminsSite = await api.CreateSiteAsync();
        var bobsSite = await api.CreateSiteAsync(bobUser.Id);

        // Bob: his own site works; the admin's site is "not found" (no hint it exists).
        Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync($"/api/sites/{bobsSite.Id}/stats/top-pages", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync($"/api/sites/{adminsSite.Id}/stats/top-pages", Ct)).StatusCode);
        var bobsList = await bob.GetFromJsonAsync<Site[]>("/api/sites", Ct);
        Assert.Equal([bobsSite.Id], bobsList!.Select(s => s.Id));

        // The admin sees both.
        Assert.Equal(HttpStatusCode.OK, (await api.Http.GetAsync($"/api/sites/{bobsSite.Id}/stats/top-pages", Ct)).StatusCode);
        var adminsList = await api.Http.GetFromJsonAsync<Site[]>("/api/sites", Ct);
        Assert.Contains(adminsList!, s => s.Id == bobsSite.Id);
        Assert.Contains(adminsList!, s => s.Id == adminsSite.Id);
    }
}
