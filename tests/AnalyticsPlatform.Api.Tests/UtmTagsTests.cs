using AnalyticsPlatform.Api.Enrichment;

namespace AnalyticsPlatform.Api.Tests;

public class UtmTagsTests
{
    private static UtmTags From(string url) => UtmTags.FromUrl(new Uri(url));

    [Fact]
    public void Reads_all_five_utm_parameters()
    {
        var tags = From("https://shop.test/sale?utm_source=newsletter&utm_medium=email&utm_campaign=black_friday&utm_term=shoes&utm_content=hero_button");

        Assert.Equal(new UtmTags("newsletter", "email", "black_friday", "shoes", "hero_button"), tags);
    }

    [Fact]
    public void Missing_parameters_are_empty() =>
        Assert.Equal(UtmTags.None, From("https://shop.test/sale?ref=abc"));

    [Fact]
    public void Values_are_url_decoded_and_trimmed() =>
        Assert.Equal("spring sale", From("https://shop.test/?utm_campaign=%20spring%20sale+").Campaign);

    [Fact]
    public void Case_is_kept() =>
        // Like GA: "Newsletter" and "newsletter" are different sources. Normalizing
        // would hide inconsistent tagging instead of surfacing it.
        Assert.Equal("Newsletter", From("https://shop.test/?utm_source=Newsletter").Source);

    [Fact]
    public void Parameter_names_are_matched_case_insensitively() =>
        Assert.Equal("google", From("https://shop.test/?UTM_SOURCE=google").Source);

    [Fact]
    public void A_repeated_parameter_uses_the_first_value() =>
        Assert.Equal("first", From("https://shop.test/?utm_source=first&utm_source=second").Source);

    [Fact]
    public void Long_values_are_capped_at_100_characters() =>
        Assert.Equal(100, From($"https://shop.test/?utm_campaign={new string('x', 500)}").Campaign.Length);

    [Fact]
    public void Utm_in_the_fragment_is_ignored() =>
        // Only the query string counts; #fragments never reach servers in normal links.
        Assert.Equal(UtmTags.None, From("https://shop.test/#utm_source=x"));
}
