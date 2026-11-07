using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;

namespace AnalyticsPlatform.Api.IntegrationTests;

// Settings for running behind a reverse proxy / in a container (Phase 8).
public class DeploymentTests(ApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static async Task<string> LoginCookie(HttpClient client, bool forwardedHttps)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { email = ApiFixture.AdminEmail, password = ApiFixture.TestPassword }),
        };
        if (forwardedHttps)
        {
            request.Headers.Add("X-Forwarded-Proto", "https");
        }

        var response = await client.SendAsync(request, Ct);
        response.EnsureSuccessStatusCode();
        return string.Join("; ", response.Headers.GetValues("Set-Cookie"));
    }

    [Fact]
    public async Task Behind_a_TLS_proxy_the_session_cookie_is_secure()
    {
        // The proxy speaks HTTPS to browsers and plain HTTP to the API; with
        // ForwardedHeaders enabled the API trusts its X-Forwarded-Proto.
        await using var factory = api.CreateFactory(b => b.UseSetting("ForwardedHeaders:Enabled", "true"));

        var cookie = await LoginCookie(factory.CreateClient(), forwardedHttps: true);

        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_unless_enabled()
    {
        // Otherwise anyone could claim to be HTTPS (or spoof their IP).
        await using var factory = api.CreateFactory();

        var cookie = await LoginCookie(factory.CreateClient(), forwardedHttps: true);

        Assert.DoesNotContain("secure", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cookie_encryption_keys_can_be_kept_in_a_folder()
    {
        // In a container the default key location is lost on every restart,
        // which would sign everyone out. DataProtection:KeysPath = a volume.
        var folder = Directory.CreateTempSubdirectory("ap-keys-").FullName;
        try
        {
            await using (var factory = api.CreateFactory(b => b.UseSetting("DataProtection:KeysPath", folder)))
            {
                await LoginCookie(factory.CreateClient(), forwardedHttps: false);
            }

            Assert.NotEmpty(Directory.GetFiles(folder, "key-*.xml"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
