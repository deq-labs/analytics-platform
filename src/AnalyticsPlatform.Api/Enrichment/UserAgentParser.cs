using DeviceDetectorNET;
using DeviceDetectorNET.Parser;
using Microsoft.Extensions.Caching.Memory;

namespace AnalyticsPlatform.Api.Enrichment;

// What we keep from a user-agent string. DeviceType is "desktop", "mobile",
// "tablet", "other" (TVs, consoles, ...), or "" when unknown.
public sealed record ClientInfo(
    string Browser,
    string BrowserVersion,
    string Os,
    string OsVersion,
    string DeviceType,
    bool IsBot)
{
    public static readonly ClientInfo Bot = new("", "", "", "", "", IsBot: true);
}

// Parses user agents with DeviceDetector.NET (a port of Matomo's device
// detector). Parsing runs hundreds of regexes, so results are cached per
// user-agent string -- real traffic repeats the same few thousand strings.
//
// Known limit: Chrome-based browsers freeze the OS version in the user agent
// ("Windows NT 10.0" even on Windows 11). Telling those apart needs
// User-Agent Client Hints (Sec-CH-UA-* headers) -- a later improvement.
public sealed class UserAgentParser : IDisposable
{
    // Longer strings are junk or abuse; don't run hundreds of regexes on them.
    private const int MaxParsedLength = 512;

    // A static constructor runs once, before the class is first used.
    // "Major" truncation: "140.0.7339.80" -> "140".
    static UserAgentParser() =>
        DeviceDetector.SetVersionTruncation(VersionTruncation.VERSION_TRUNCATION_MAJOR);

    // Its own cache with a size limit, so a flood of unique junk user agents
    // can't grow memory without bound (each entry counts as size 1).
    private readonly MemoryCache _cache = new(new MemoryCacheOptions { SizeLimit = 10_000 });

    public ClientInfo Parse(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent))
        {
            return ClientInfo.Bot; // every real browser sends a user agent
        }

        return _cache.GetOrCreate(userAgent, entry =>
        {
            entry.Size = 1;
            entry.SlidingExpiration = TimeSpan.FromHours(1);
            return ParseUncached(userAgent.Length > MaxParsedLength ? userAgent[..MaxParsedLength] : userAgent);
        })!;
    }

    private static ClientInfo ParseUncached(string userAgent)
    {
        var detector = new DeviceDetector(userAgent);
        detector.Parse();

        // Libraries = curl, python-requests, Go's HTTP client, ... Not visitors.
        if (detector.IsBot() || detector.IsLibrary())
        {
            return ClientInfo.Bot;
        }

        var client = detector.GetClient();
        var os = detector.GetOs();

        // DeviceDetector reports automation browsers (Puppeteer and Playwright
        // run "HeadlessChrome" by default) as ordinary browsers. They're bots here.
        if (client.Success && client.Match.Name.StartsWith("Headless", StringComparison.OrdinalIgnoreCase)
            || userAgent.Contains("HeadlessChrome", StringComparison.Ordinal))
        {
            return ClientInfo.Bot;
        }

        return new ClientInfo(
            Browser: client.Success ? client.Match.Name : "",
            BrowserVersion: client.Success ? client.Match.Version : "",
            Os: os.Success ? os.Match.Name : "",
            OsVersion: os.Success ? os.Match.Version : "",
            DeviceType: DeviceTypeOf(detector.GetDeviceName()),
            IsBot: false);
    }

    // DeviceDetector knows ~15 device kinds; the dashboard needs three.
    private static string DeviceTypeOf(string deviceName) => deviceName switch
    {
        "desktop" => "desktop",
        "smartphone" or "phablet" or "feature phone" => "mobile",
        "tablet" => "tablet",
        "" => "",
        _ => "other",
    };

    public void Dispose() => _cache.Dispose();
}
