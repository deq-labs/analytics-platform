using AnalyticsPlatform.Api.Enrichment;

namespace AnalyticsPlatform.Api.Tests;

// Real user-agent strings -> what lands in the browser/os/device_type/is_bot columns.
public class UserAgentParserTests
{
    private const string ChromeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.7339.80 Safari/537.36";
    private const string SafariIphone =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 18_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Mobile/15E148 Safari/604.1";
    private const string ChromeAndroidTablet =
        "Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36";
    private const string ChromeAndroidPhone =
        "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Mobile Safari/537.36";
    private const string FirefoxLinux =
        "Mozilla/5.0 (X11; Linux x86_64; rv:144.0) Gecko/20100101 Firefox/144.0";
    private const string SafariMac =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.5 Safari/605.1.15";
    private const string EdgeWindows =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0.0.0 Safari/537.36 Edg/140.0.3485.54";

    private readonly UserAgentParser _parser = new();

    [Fact]
    public void Chrome_on_windows_is_a_desktop()
    {
        var client = _parser.Parse(ChromeWindows);

        Assert.Equal(new ClientInfo("Chrome", "140", "Windows", "10", "desktop", IsBot: false), client);
    }

    [Fact]
    public void Safari_on_iphone_is_mobile()
    {
        var client = _parser.Parse(SafariIphone);

        Assert.Equal("Mobile Safari", client.Browser);
        Assert.Equal("iOS", client.Os);
        Assert.Equal("18", client.OsVersion);
        Assert.Equal("mobile", client.DeviceType);
    }

    [Fact]
    public void Android_tablet_and_phone_are_told_apart()
    {
        Assert.Equal("tablet", _parser.Parse(ChromeAndroidTablet).DeviceType);
        Assert.Equal("mobile", _parser.Parse(ChromeAndroidPhone).DeviceType);
    }

    [Theory]
    [InlineData(FirefoxLinux, "Firefox", "GNU/Linux")]
    [InlineData(SafariMac, "Safari", "Mac")]
    [InlineData(EdgeWindows, "Microsoft Edge", "Windows")]
    public void Other_desktop_browsers(string userAgent, string browser, string os)
    {
        var client = _parser.Parse(userAgent);

        Assert.Equal(browser, client.Browser);
        Assert.Equal(os, client.Os);
        Assert.Equal("desktop", client.DeviceType);
        Assert.False(client.IsBot);
    }

    [Fact]
    public void Versions_are_major_only()
    {
        // Keeps the LowCardinality columns small and the dashboard readable:
        // "Chrome 140", not "Chrome 140.0.7339.80".
        Assert.Equal("140", _parser.Parse(ChromeWindows).BrowserVersion);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)")]
    [InlineData("Mozilla/5.0 (compatible; bingbot/2.0; +http://www.bing.com/bingbot.htm)")]
    [InlineData("curl/8.9.1")]                // tools aren't visitors either
    [InlineData("python-requests/2.32.3")]
    [InlineData("")]                          // real browsers always send a user agent
    public void Bots_tools_and_missing_user_agents_are_flagged(string userAgent)
    {
        Assert.True(_parser.Parse(userAgent).IsBot);
    }

    [Fact]
    public void Gibberish_is_not_a_crash()
    {
        var client = _parser.Parse("¯\\_(ツ)_/¯ totally/real browser");

        Assert.NotNull(client);
    }

    [Fact]
    public void Repeated_and_concurrent_calls_give_the_same_answer()
    {
        // The parser caches results per user agent and is shared by all requests.
        var results = Enumerable.Range(0, 200)
            .AsParallel()
            .Select(i => _parser.Parse(i % 2 == 0 ? ChromeWindows : SafariIphone))
            .ToList();

        Assert.Equal(2, results.Distinct().Count());
    }
}
