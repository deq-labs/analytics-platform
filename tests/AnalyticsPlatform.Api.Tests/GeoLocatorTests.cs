using System.Net;
using System.Reflection;
using AnalyticsPlatform.Api.Enrichment;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AnalyticsPlatform.Api.Tests;

public class GeoLocatorTests : IDisposable
{
    // The real GeoLite2 database, downloaded by the geoipupdate container.
    // Tests that need it are *skipped* (not failed) when it isn't there, e.g.
    // on a fresh clone without a MaxMind account.
    private static readonly string RealDatabase = Path.Combine(
        typeof(GeoLocatorTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "RepoRoot").Value!,
        "data", "geoip", "GeoLite2-City.mmdb");

    private readonly FakeTimeProvider _time = new();
    private readonly List<IDisposable> _toDispose = [];

    private MaxMindGeoLocator Locator(string path)
    {
        var locator = new MaxMindGeoLocator(
            Options.Create(new GeoIpOptions { DatabasePath = path }), _time, NullLogger<MaxMindGeoLocator>.Instance);
        _toDispose.Add(locator);
        return locator;
    }

    private static void RequireRealDatabase() =>
        Assert.SkipUnless(File.Exists(RealDatabase), $"GeoLite2 database not downloaded ({RealDatabase}).");

    public void Dispose() => _toDispose.ForEach(d => d.Dispose());

    // ---- No database: geo is optional, never an error ----

    [Theory]
    [InlineData("")]
    [InlineData("C:/definitely/not/here/GeoLite2-City.mmdb")]
    public void Without_a_database_everything_is_unknown(string path)
    {
        var locator = Locator(path);

        Assert.Equal(GeoLocation.Unknown, locator.Locate(IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void Missing_ip_address_is_unknown() =>
        Assert.Equal(GeoLocation.Unknown, Locator("").Locate(null));

    // ---- With the real database ----

    [Fact]
    public void Looks_up_the_country()
    {
        RequireRealDatabase();

        Assert.Equal("US", Locator(RealDatabase).Locate(IPAddress.Parse("8.8.8.8")).CountryCode);
    }

    [Fact]
    public void Ipv4_written_as_ipv6_is_looked_up_the_same()
    {
        RequireRealDatabase();
        var locator = Locator(RealDatabase);

        Assert.Equal(
            locator.Locate(IPAddress.Parse("8.8.8.8")),
            locator.Locate(IPAddress.Parse("::ffff:8.8.8.8")));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.10")]
    [InlineData("::1")]
    public void Private_and_loopback_addresses_are_unknown(string ip)
    {
        RequireRealDatabase();

        Assert.Equal(GeoLocation.Unknown, Locator(RealDatabase).Locate(IPAddress.Parse(ip)));
    }

    [Fact]
    public void Picks_up_a_database_that_appears_later()
    {
        // The geoipupdate container downloads (and every 72h replaces) the
        // file while the API is running. The locator re-checks the file's
        // timestamp every few minutes and reloads.
        RequireRealDatabase();
        var directory = Directory.CreateTempSubdirectory("geoip-test-");
        var path = Path.Combine(directory.FullName, "GeoLite2-City.mmdb");
        try
        {
            var locator = Locator(path);
            Assert.Equal(GeoLocation.Unknown, locator.Locate(IPAddress.Parse("8.8.8.8"))); // not there yet

            File.Copy(RealDatabase, path);
            Assert.Equal(GeoLocation.Unknown, locator.Locate(IPAddress.Parse("8.8.8.8"))); // not re-checked yet

            _time.Advance(MaxMindGeoLocator.ReloadCheckInterval);
            Assert.Equal("US", locator.Locate(IPAddress.Parse("8.8.8.8")).CountryCode);
        }
        finally
        {
            Dispose();
            _toDispose.Clear();
            directory.Delete(recursive: true);
        }
    }
}
