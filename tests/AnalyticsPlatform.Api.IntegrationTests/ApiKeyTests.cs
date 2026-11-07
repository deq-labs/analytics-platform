using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Phase 7 step 4: secret API keys, so a server (a script, a report job, another
// app) can read stats without a browser login. Keys are read-only.
public class ApiKeyTests(ApiFixture api)
{
    private sealed record CreatedKey(int Id, string Name, string Prefix, string Key, DateTime CreatedAt);
    private sealed record KeyInfo(int Id, string Name, string Prefix, DateTime CreatedAt, DateTime? LastUsedAt);
    private sealed record Site(uint Id, string Name, string Domain, string TrackingId);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<CreatedKey> CreateKey(HttpClient client, string name = "Reports")
    {
        var response = await client.PostAsJsonAsync("/api/keys", new { name }, Ct);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedKey>(Ct))!;
    }

    // A client with no cookies that sends the key, like a server would.
    private HttpClient KeyClient(string key)
    {
        var client = api.AnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return client;
    }

    [Fact]
    public async Task A_new_key_is_shown_once_and_listed_by_its_prefix_only()
    {
        var (client, _) = await api.CreateUserClientAsync();

        var created = await CreateKey(client, "Nightly report");

        Assert.Matches("^apk_[A-Za-z0-9_-]{43}$", created.Key);
        Assert.StartsWith(created.Prefix, created.Key);
        Assert.Equal("Nightly report", created.Name);

        var json = await client.GetStringAsync("/api/keys", Ct);
        Assert.DoesNotContain(created.Key, json);
        var keys = await client.GetFromJsonAsync<KeyInfo[]>("/api/keys", Ct);
        var listed = Assert.Single(keys!);
        Assert.Equal((created.Id, created.Prefix, (DateTime?)null), (listed.Id, listed.Prefix, listed.LastUsedAt));
    }

    [Fact]
    public async Task A_key_reads_stats_for_its_owners_sites()
    {
        var (client, user) = await api.CreateUserClientAsync();
        var site = await api.CreateSiteAsync(user.Id);
        var key = await CreateKey(client);
        var server = KeyClient(key.Key);

        var stats = await server.GetAsync($"/api/sites/{site}/stats/top-pages", Ct);
        var sites = await server.GetFromJsonAsync<Site[]>("/api/sites", Ct);

        Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
        Assert.Equal(site.Id, Assert.Single(sites!).Id);
        var listed = Assert.Single((await client.GetFromJsonAsync<KeyInfo[]>("/api/keys", Ct))!);
        Assert.NotNull(listed.LastUsedAt);
    }

    [Fact]
    public async Task A_key_cannot_read_other_peoples_sites()
    {
        var (client, _) = await api.CreateUserClientAsync();
        var key = await CreateKey(client);
        var someoneElses = await api.CreateSiteAsync(); // owned by the admin

        var response = await KeyClient(key.Key).GetAsync($"/api/sites/{someoneElses}/stats/top-pages", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task An_admins_key_does_not_carry_admin_powers()
    {
        var key = await CreateKey(api.Http);
        var (_, user) = await api.CreateUserClientAsync();
        var usersSite = await api.CreateSiteAsync(user.Id);
        var server = KeyClient(key.Key);

        Assert.Equal(HttpStatusCode.NotFound, (await server.GetAsync($"/api/sites/{usersSite}/stats/top-pages", Ct)).StatusCode);
        Assert.False((await server.PostAsync("/api/admin/invites", null, Ct)).IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("POST", "/api/sites")]
    [InlineData("POST", "/api/keys")]
    [InlineData("GET", "/api/keys")]
    [InlineData("GET", "/api/auth/me")]
    public async Task Keys_are_read_only(string method, string path)
    {
        var (client, user) = await api.CreateUserClientAsync();
        var key = await CreateKey(client);

        var request = new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = new StringContent("""{ "name": "x", "domain": "keys-are-read-only.test" }""", Encoding.UTF8, "application/json"),
        };
        var response = await KeyClient(key.Key).SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_key_cannot_change_a_site()
    {
        var (client, user) = await api.CreateUserClientAsync();
        var site = await api.CreateSiteAsync(user.Id);
        var key = await CreateKey(client);

        var response = await KeyClient(key.Key).DeleteAsync($"/api/sites/{site}", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/sites/{site}", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("apk_this-is-not-a-real-key-at-all-0000000000000")]
    [InlineData("apk_")]
    [InlineData("not-even-an-api-key")]
    public async Task Unknown_keys_are_rejected(string key)
    {
        var response = await KeyClient(key).GetAsync("/api/sites", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_deleted_key_stops_working()
    {
        var (client, _) = await api.CreateUserClientAsync();
        var key = await CreateKey(client);
        var server = KeyClient(key.Key);
        Assert.Equal(HttpStatusCode.OK, (await server.GetAsync("/api/sites", Ct)).StatusCode);

        var delete = await client.DeleteAsync($"/api/keys/{key.Id}", Ct);

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await server.GetAsync("/api/sites", Ct)).StatusCode);
    }

    [Fact]
    public async Task You_cannot_see_or_delete_other_peoples_keys()
    {
        var (owner, _) = await api.CreateUserClientAsync();
        var key = await CreateKey(owner);
        var (other, _) = await api.CreateUserClientAsync();

        Assert.Empty((await other.GetFromJsonAsync<KeyInfo[]>("/api/keys", Ct))!);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/keys/{key.Id}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await KeyClient(key.Key).GetAsync("/api/sites", Ct)).StatusCode);
    }

    [Fact]
    public async Task Keys_are_stored_hashed()
    {
        var (client, _) = await api.CreateUserClientAsync();
        var key = await CreateKey(client);

        await using var command = api.Postgres.CreateCommand("SELECT row_to_json(k)::text FROM api_keys k WHERE id = $1");
        command.Parameters.AddWithValue(key.Id);
        var row = (string)(await command.ExecuteScalarAsync(Ct))!;

        Assert.DoesNotContain(key.Key, row);
        Assert.DoesNotContain(key.Key["apk_".Length..], row);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_key_needs_a_name(string name)
    {
        var response = await api.Http.PostAsJsonAsync("/api/keys", new { name }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
