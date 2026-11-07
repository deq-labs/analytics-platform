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
}
