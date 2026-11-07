namespace AnalyticsPlatform.Api.Ingestion;

// The domain check at ingestion: a page may send events for a site only if its
// hostname is the site's registered domain, its www. form, or a subdomain.
// This (not the public tracking ID) is what stops someone copying a site's
// snippet onto their own pages to pollute its stats.
//
// It isn't bulletproof -- a script can claim any URL -- but it's the standard
// trade-off for browser-side tracking (GA and Rybbit work the same way).
public static class SiteDomain
{
    public static bool Allows(string siteDomain, string host)
    {
        var site = Normalize(siteDomain);
        var page = Normalize(host);
        if (site.Length == 0 || page.Length == 0)
        {
            return false;
        }

        // Equal, or a real subdomain: "blog.shop.test" ends with ".shop.test".
        // Note the dot -- "notshop.test" must not match "shop.test".
        return page == site || page.EndsWith("." + site, StringComparison.Ordinal);
    }

    // Turns what someone types into the "Domain" box into the domain we store:
    // "https://www.Example.com/blog" -> "example.com". Accepts a bare domain or
    // a pasted http(s) URL; unicode names are stored in their ASCII (punycode)
    // form, which is what browsers put in location.hostname.
    public static bool TryNormalizeForRegistration(string? input, out string domain)
    {
        domain = "";
        var text = input?.Trim() ?? "";
        if (text.Length is 0 or > 300)
        {
            return false;
        }

        // Let System.Uri do the parsing (ports, paths, unicode) instead of
        // hand-written string slicing.
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0
            || uri.HostNameType != UriHostNameType.Dns)
        {
            return false;
        }

        var host = Normalize(uri.IdnHost);
        var labels = host.Split('.');
        if (host.Length > 253
            || (labels.Length < 2 && host != "localhost")
            || !labels.All(IsValidLabel))
        {
            return false;
        }

        domain = host;
        return true;
    }

    // One dot-separated part: letters, digits and inner hyphens, 1-63 chars.
    private static bool IsValidLabel(string label) =>
        label.Length is > 0 and <= 63
        && label[0] != '-' && label[^1] != '-'
        && label.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    private static string Normalize(string host)
    {
        var lower = host.Trim().ToLowerInvariant();
        return lower.StartsWith("www.", StringComparison.Ordinal) ? lower[4..] : lower;
    }
}
