using AnalyticsPlatform.Api.Ingestion;

namespace AnalyticsPlatform.Api.Tests;

public class EventRequestValidatorTests
{
    // Builds a valid request; each test changes just the part it's about.
    private static EventRequest Event(
        string type = "pageview",
        string? name = null,
        string url = "https://example.com/pricing",
        string? referrer = null,
        string? trackingId = "ap_0123456789abcdef",
        Dictionary<string, string>? properties = null) =>
        new(trackingId, type, name, url, referrer, VisitorId: null, ScreenWidth: null, ScreenHeight: null, properties);

    private static Dictionary<string, string[]> Validate(EventRequest request) =>
        EventRequestValidator.Validate(request);

    [Fact]
    public void Valid_pageview_has_no_errors() =>
        Assert.Empty(Validate(Event(referrer: "https://google.com/")));

    [Theory]
    [InlineData("scroll")]
    [InlineData("outbound_click")]
    [InlineData("file_download")]
    [InlineData("form_start")]
    [InlineData("form_submit")]
    [InlineData("video_start")]
    [InlineData("video_progress")]
    [InlineData("video_complete")]
    public void Standard_event_accepts_every_reserved_name(string name) =>
        Assert.Empty(Validate(Event("standard", name, properties: new() { ["depth"] = "90" })));

    [Fact]
    public void Standard_event_rejects_unknown_name() =>
        Assert.Contains("name", Validate(Event("standard", "made_up")).Keys);

    [Theory]
    [InlineData("scroll")]
    [InlineData("form_submit")]
    public void Custom_event_cannot_use_a_reserved_standard_name(string name)
    {
        var errors = Validate(Event("custom", name));

        Assert.Contains("reserved", Assert.Single(errors["name"]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Custom_event_requires_a_name(string? name) =>
        Assert.Contains("name", Validate(Event("custom", name)).Keys);

    [Fact]
    public void Custom_event_name_is_at_most_64_characters()
    {
        Assert.Empty(Validate(Event("custom", new string('a', 64))));
        Assert.Contains("name", Validate(Event("custom", new string('a', 65))).Keys);
    }

    [Fact]
    public void Pageview_cannot_have_a_name() =>
        Assert.Contains("name", Validate(Event("pageview", "something")).Keys);

    [Fact]
    public void Pageview_cannot_have_properties() =>
        Assert.Contains("properties", Validate(Event(properties: new() { ["a"] = "b" })).Keys);

    [Theory]
    [InlineData("click")]
    [InlineData("PAGEVIEW")] // types are case-sensitive
    [InlineData("")]
    [InlineData(null)]
    public void Unknown_type_is_rejected(string? type) =>
        Assert.Contains("type", Validate(Event(type!)).Keys);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Tracking_id_is_required(string? trackingId) =>
        Assert.Contains("trackingId", Validate(Event(trackingId: trackingId)).Keys);

    [Fact]
    public void Tracking_id_is_at_most_64_characters() =>
        Assert.Contains("trackingId", Validate(Event(trackingId: new string('a', 65))).Keys);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com/file")]
    [InlineData("/relative/path")]
    [InlineData("not a url")]
    [InlineData("")]
    public void Url_must_be_absolute_http_or_https(string url) =>
        Assert.Contains("url", Validate(Event(url: url)).Keys);

    [Fact]
    public void Url_is_at_most_2048_characters()
    {
        var prefix = "https://example.com/";
        Assert.Empty(Validate(Event(url: prefix + new string('a', 2048 - prefix.Length))));
        Assert.Contains("url", Validate(Event(url: prefix + new string('a', 2049 - prefix.Length))).Keys);
    }

    [Fact]
    public void Referrer_is_optional_but_must_be_http_when_present()
    {
        Assert.Empty(Validate(Event(referrer: null)));
        Assert.Empty(Validate(Event(referrer: "")));
        Assert.Contains("referrer", Validate(Event(referrer: "android-app://com.google.android.gm/")).Keys);
    }

    [Fact]
    public void Properties_are_limited_to_25()
    {
        Dictionary<string, string> Props(int count) =>
            Enumerable.Range(0, count).ToDictionary(i => $"k{i}", i => "v");

        Assert.Empty(Validate(Event("custom", "signup", properties: Props(25))));
        Assert.Contains("properties", Validate(Event("custom", "signup", properties: Props(26))).Keys);
    }

    [Theory]
    [InlineData("", "value")]   // empty key
    [InlineData("k", null)]     // placeholder, replaced below with a 513-char value
    public void Property_keys_and_values_have_length_limits(string key, string? value)
    {
        value ??= new string('v', 513);
        var errors = Validate(Event("custom", "signup", properties: new() { [key] = value }));

        Assert.Contains("properties", errors.Keys);
    }

    [Fact]
    public void Multiple_problems_are_all_reported()
    {
        var errors = Validate(Event("custom", "scroll", url: "nope", trackingId: null));

        Assert.Equal(["name", "trackingId", "url"], errors.Keys.Order());
    }
}
