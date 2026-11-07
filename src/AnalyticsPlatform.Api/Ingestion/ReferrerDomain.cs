namespace AnalyticsPlatform.Api.Ingestion;

// Turns a referrer URL into the domain shown in "top referrers":
//   "https://www.Google.com/search?q=x" -> "google.com"
// so www and non-www group together. Uri already lowercases the host.
// Full referrer/UTM parsing (search engine names, campaigns) is Phase 6 and
// will grow here.
public static class ReferrerDomain
{
    public static string From(string? referrer)
    {
        if (!EventRequestValidator.IsHttpUrl(referrer, out var uri))
        {
            return "";
        }

        var host = uri.Host;
        // `host[4..]` is a *range*: everything from index 4 to the end.
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }
}
