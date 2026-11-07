using System.Net;
using AnalyticsPlatform.Api.Sites;
using AnalyticsPlatform.Api.Visitors;

namespace AnalyticsPlatform.Api.Tests;

public class VisitorIdentifierTests
{
    // A stand-in for the Postgres-backed DailySaltStore. Tests set the salt
    // directly; changing it simulates the next day.
    private sealed class FakeSalt : IDailySaltProvider
    {
        public byte[] Salt { get; set; } = Enumerable.Repeat((byte)7, 32).ToArray();

        public Task<byte[]> GetTodaysSaltAsync(CancellationToken cancellationToken) => Task.FromResult(Salt);
    }

    private const string Chrome = "Mozilla/5.0 Chrome/140";
    private const string Firefox = "Mozilla/5.0 Firefox/140";
    private static readonly IPAddress Ip = IPAddress.Parse("203.0.113.7");
    private static readonly SiteConfig HashSite = new(1, VisitorIdMode.Hash, []);
    private static readonly SiteConfig PersistentSite = new(2, VisitorIdMode.Persistent, []);

    private readonly FakeSalt _salt = new();
    private readonly VisitorIdentifier _identifier;

    // xUnit creates a new instance of the class for every test, so each test
    // gets a fresh FakeSalt and identifier.
    public VisitorIdentifierTests() => _identifier = new VisitorIdentifier(_salt);

    private Task<VisitorIdentity> Identify(
        SiteConfig? site = null, IPAddress? ip = null, string userAgent = Chrome, string? clientId = null) =>
        _identifier.IdentifyAsync(site ?? HashSite, ip ?? Ip, userAgent, clientId, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Hash_mode_produces_a_prefixed_sha256()
    {
        var visitor = await Identify();

        Assert.Matches("^h:[0-9a-f]{64}$", visitor.VisitorId);
    }

    [Fact]
    public async Task Same_inputs_on_the_same_day_give_the_same_visitor()
    {
        Assert.Equal((await Identify()).VisitorId, (await Identify()).VisitorId);
    }

    [Fact]
    public async Task Different_browser_gives_a_different_visitor()
    {
        Assert.NotEqual((await Identify(userAgent: Chrome)).VisitorId, (await Identify(userAgent: Firefox)).VisitorId);
    }

    [Fact]
    public async Task Same_person_on_two_sites_is_two_visitors()
    {
        var onSiteOne = await Identify(new SiteConfig(1, VisitorIdMode.Hash, []));
        var onSiteThree = await Identify(new SiteConfig(3, VisitorIdMode.Hash, []));

        Assert.NotEqual(onSiteOne.VisitorId, onSiteThree.VisitorId);
    }

    [Fact]
    public async Task A_new_daily_salt_makes_yesterdays_visitor_unlinkable()
    {
        var today = await Identify();
        _salt.Salt = Enumerable.Repeat((byte)8, 32).ToArray(); // "tomorrow"
        var tomorrow = await Identify();

        Assert.NotEqual(today.VisitorId, tomorrow.VisitorId);
        Assert.NotEqual(today.IpHash, tomorrow.IpHash);
    }

    [Fact]
    public async Task Ipv4_written_as_ipv6_is_the_same_visitor()
    {
        var plain = await Identify(ip: IPAddress.Parse("203.0.113.7"));
        var mapped = await Identify(ip: IPAddress.Parse("::ffff:203.0.113.7"));

        Assert.Equal(plain.VisitorId, mapped.VisitorId);
        Assert.Equal(plain.IpHash, mapped.IpHash);
    }

    [Fact]
    public async Task Ip_hash_never_contains_the_ip_address()
    {
        var visitor = await Identify();

        Assert.Matches("^[0-9a-f]{64}$", visitor.IpHash);
        Assert.DoesNotContain("203.0.113.7", visitor.IpHash);
        Assert.DoesNotContain("203.0.113.7", visitor.VisitorId);
    }

    [Fact]
    public async Task Missing_ip_address_still_works()
    {
        var visitor = await _identifier.IdentifyAsync(HashSite, null, Chrome, null, TestContext.Current.CancellationToken);

        Assert.StartsWith("h:", visitor.VisitorId);
    }

    [Fact]
    public async Task Persistent_mode_uses_the_browser_id()
    {
        var visitor = await Identify(PersistentSite, clientId: "7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f");

        Assert.Equal("p:7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f", visitor.VisitorId);
    }

    [Fact]
    public async Task Persistent_mode_normalizes_the_uuid_format()
    {
        var upper = await Identify(PersistentSite, clientId: "7C1E2F40-8A5B-4C3D-9E1F-0A2B3C4D5E6F");
        var braces = await Identify(PersistentSite, clientId: "{7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f}");

        Assert.Equal("p:7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f", upper.VisitorId);
        Assert.Equal(upper.VisitorId, braces.VisitorId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-uuid")]
    [InlineData("<script>alert(1)</script>")]
    public async Task Persistent_mode_falls_back_to_the_hash_without_a_valid_uuid(string? clientId)
    {
        var visitor = await Identify(PersistentSite, clientId: clientId);

        Assert.StartsWith("h:", visitor.VisitorId);
    }

    [Fact]
    public async Task Hash_mode_ignores_a_browser_id()
    {
        var visitor = await Identify(HashSite, clientId: "7c1e2f40-8a5b-4c3d-9e1f-0a2b3c4d5e6f");

        Assert.StartsWith("h:", visitor.VisitorId);
    }
}
