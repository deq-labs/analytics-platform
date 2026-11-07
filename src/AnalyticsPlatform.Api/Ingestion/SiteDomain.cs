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

    private static string Normalize(string host)
    {
        var lower = host.Trim().ToLowerInvariant();
        return lower.StartsWith("www.", StringComparison.Ordinal) ? lower[4..] : lower;
    }
}
