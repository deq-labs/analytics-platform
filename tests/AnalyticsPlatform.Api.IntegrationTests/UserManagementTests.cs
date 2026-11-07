using System.Net;
using System.Net.Http.Json;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 8 backlog: admins manage users, and sessions follow the database --
// deleting or demoting someone takes effect on their very next request, not
// when their cookie expires.
public class UserManagementTests(ApiFixture api)
{
    private sealed record AdminUser(int Id, string Email, bool IsAdmin, int Sites, DateTime CreatedAt);
    private sealed record Site(uint Id, string Name, string Domain, string TrackingId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SetAdmin(int userId, bool isAdmin)
    {
        await using var command = api.Postgres.CreateCommand("UPDATE users SET is_admin = $2 WHERE id = $1");
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(isAdmin);
        await command.ExecuteNonQueryAsync(Ct);
    }

    [Fact]
    public async Task Admins_list_users_with_their_site_counts()
    {
        var (_, user) = await api.CreateUserClientAsync();
        await api.CreateSiteAsync(user.Id);
        await api.CreateSiteAsync(user.Id);

        var users = await api.Http.GetFromJsonAsync<AdminUser[]>("/api/admin/users", Ct);

        var listed = Assert.Single(users!, u => u.Id == user.Id);
        Assert.Equal((user.Email, false, 2), (listed.Email, listed.IsAdmin, listed.Sites));
        Assert.Contains(users!, u => u.Id == api.AdminUserId && u.IsAdmin);
    }

    [Fact]
    public async Task Only_admins_can_manage_users()
    {
        var (client, user) = await api.CreateUserClientAsync();

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.DeleteAsync($"/api/admin/users/{api.AdminUserId}", Ct)).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_user_ends_their_session_and_removes_their_sites()
    {
        var (client, user) = await api.CreateUserClientAsync();
        var site = await api.CreateSiteAsync(user.Id);
        await api.SeedPageviewsAsync(site, [new SeedPageview(DateTime.UtcNow.AddMinutes(-1), "/")]);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sites", Ct)).StatusCode);

        var response = await api.Http.DeleteAsync($"/api/admin/users/{user.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/sites", Ct)).StatusCode); // same cookie
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.GetAsync($"/api/sites/{site}", Ct)).StatusCode);
        Assert.Empty(await api.GetEventsAsync(site));
    }

    [Fact]
    public async Task Admins_cannot_delete_themselves()
    {
        var response = await api.Http.DeleteAsync($"/api/admin/users/{api.AdminUserId}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Deleting_an_unknown_user_is_a_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await api.Http.DeleteAsync("/api/admin/users/999999", Ct)).StatusCode);
    }

    [Fact]
    public async Task Promotion_and_demotion_apply_to_existing_sessions()
    {
        var (client, user) = await api.CreateUserClientAsync();

        await SetAdmin(user.Id, true);
        Assert.True((await client.GetFromJsonAsync<UserInfo>("/api/auth/me", Ct))!.IsAdmin);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/admin/users", Ct)).StatusCode);

        await SetAdmin(user.Id, false);
        Assert.False((await client.GetFromJsonAsync<UserInfo>("/api/auth/me", Ct))!.IsAdmin);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/users", Ct)).StatusCode);
    }
}
