using AnalyticsPlatform.Api.Ingestion;

namespace AnalyticsPlatform.Api.Tests;

public class ReferrerDomainTests
{
    [Theory]
    [InlineData("https://www.google.com/search?q=x", "google.com")]
    [InlineData("https://www.Google.COM/", "google.com")] // hosts are lowercased
    [InlineData("https://google.com/", "google.com")]
    [InlineData("https://news.ycombinator.com/item?id=1", "news.ycombinator.com")]
    [InlineData("http://t.co/abc123", "t.co")]
    [InlineData("https://wwwexample.com/", "wwwexample.com")] // only a real "www." label is stripped
    [InlineData("https://www.co.uk.example/", "co.uk.example")]
    public void Extracts_the_domain_without_www(string referrer, string expected) =>
        Assert.Equal(expected, ReferrerDomain.From(referrer));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("android-app://com.google.android.gm/")]
    [InlineData("/relative")]
    public void Returns_empty_for_missing_or_non_http_referrers(string? referrer) =>
        Assert.Equal("", ReferrerDomain.From(referrer));
}
