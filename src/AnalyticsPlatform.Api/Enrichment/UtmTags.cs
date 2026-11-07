using Microsoft.AspNetCore.WebUtilities;

namespace AnalyticsPlatform.Api.Enrichment;

// Campaign tags from the page URL's query string, e.g.
//   https://shop.test/sale?utm_source=newsletter&utm_medium=email&utm_campaign=black_friday
// Stored in the utm_* columns. Values keep their case (like GA), so
// inconsistent tagging ("Newsletter" vs "newsletter") stays visible.
public sealed record UtmTags(string Source, string Medium, string Campaign, string Term, string Content)
{
    public static readonly UtmTags None = new("", "", "", "", "");

    // The columns are LowCardinality; long free-text values would bloat them.
    private const int MaxLength = 100;

    public static UtmTags FromUrl(Uri url)
    {
        if (!url.Query.Contains("utm_", StringComparison.OrdinalIgnoreCase))
        {
            return None; // the common case: skip parsing entirely
        }

        // ParseQuery decodes %20 and + and matches names case-insensitively.
        var query = QueryHelpers.ParseQuery(url.Query);

        string Get(string name) =>
            query.TryGetValue(name, out var values) && values.Count > 0 ? Clean(values[0]) : "";

        return new UtmTags(
            Get("utm_source"), Get("utm_medium"), Get("utm_campaign"), Get("utm_term"), Get("utm_content"));
    }

    private static string Clean(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        return trimmed.Length > MaxLength ? trimmed[..MaxLength] : trimmed;
    }
}
