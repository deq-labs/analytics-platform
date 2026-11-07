using AnalyticsPlatform.Api.Ingestion;

namespace AnalyticsPlatform.Api.Tests;

// Is a page hostname allowed to send events for a site's registered domain?
public class SiteDomainTests
{
    [Theory]
    [InlineData("shop.test", "shop.test")]
    [InlineData("shop.test", "www.shop.test")]     // www is the same site
    [InlineData("www.shop.test", "shop.test")]     // ...in either direction
    [InlineData("shop.test", "blog.shop.test")]    // subdomains belong to the site
    [InlineData("shop.test", "a.b.shop.test")]
    [InlineData("Shop.Test", "SHOP.test")]         // hostnames are case-insensitive
    [InlineData("localhost", "localhost")]
    [InlineData("persistent.localhost", "persistent.localhost")]
    public void Allowed(string siteDomain, string host) =>
        Assert.True(SiteDomain.Allows(siteDomain, host));

    [Theory]
    [InlineData("shop.test", "evil.test")]
    [InlineData("shop.test", "notshop.test")]          // a suffix match without a dot is not a subdomain
    [InlineData("shop.test", "shop.test.evil.test")]   // a prefix match is not the site either
    [InlineData("blog.shop.test", "shop.test")]        // a parent domain isn't covered by a subdomain site
    [InlineData("shop.test", "")]
    public void Rejected(string siteDomain, string host) =>
        Assert.False(SiteDomain.Allows(siteDomain, host));

    // What a user types when registering a site -> the domain we store.
    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("  Example.COM ", "example.com")]
    [InlineData("www.example.com", "example.com")]                   // www is the same site
    [InlineData("https://www.example.com/blog?x=1", "example.com")]  // pasted a URL
    [InlineData("http://shop.example.com:8080", "shop.example.com")] // port dropped
    [InlineData("example.com/", "example.com")]
    [InlineData("localhost", "localhost")]                           // fine for local testing
    [InlineData("blog.localhost", "blog.localhost")]
    [InlineData("xn--bcher-kva.example", "xn--bcher-kva.example")]   // punycode
    [InlineData("bücher.example", "xn--bcher-kva.example")]          // unicode -> punycode
    public void Registration_normalizes(string input, string expected)
    {
        Assert.True(SiteDomain.TryNormalizeForRegistration(input, out var domain));
        Assert.Equal(expected, domain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("example")]          // needs a dot (except localhost)
    [InlineData("not a domain")]
    [InlineData("exa_mple.com")]
    [InlineData("-example.com")]
    [InlineData("example..com")]
    [InlineData("user@example.com")]
    [InlineData("ftp://example.com")]
    public void Registration_rejects(string input) =>
        Assert.False(SiteDomain.TryNormalizeForRegistration(input, out _));
}
