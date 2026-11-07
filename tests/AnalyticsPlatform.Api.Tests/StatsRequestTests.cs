using AnalyticsPlatform.Api.Stats;
using Microsoft.Extensions.Time.Testing;

namespace AnalyticsPlatform.Api.Tests;

public class StatsRequestTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 12, 30, 0, DateTimeKind.Utc);
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));
    private readonly Dictionary<string, string[]> _errors = [];

    private static StatsRequest Request(string? from = null, string? to = null, string? interval = null, int? limit = null) =>
        new(from, to, interval, limit);

    // ---- range ----

    [Fact]
    public void Defaults_to_the_last_seven_days()
    {
        Assert.True(Request().TryGetRange(_time, _errors, out var range));

        Assert.Equal(Now.AddDays(-7), range.From);
        Assert.Equal(Now, range.To);
    }

    [Fact]
    public void Date_without_a_time_zone_is_utc_midnight()
    {
        Assert.True(Request("2026-11-01", "2026-11-02").TryGetRange(_time, _errors, out var range));

        Assert.Equal(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc), range.From);
        Assert.Equal(DateTimeKind.Utc, range.From.Kind);
    }

    [Fact]
    public void Explicit_offsets_are_converted_to_utc()
    {
        Assert.True(Request("2026-11-01T00:00:00+02:00", "2026-11-02").TryGetRange(_time, _errors, out var range));

        Assert.Equal(new DateTime(2026, 10, 31, 22, 0, 0, DateTimeKind.Utc), range.From);
    }

    [Fact]
    public void Only_from_given_runs_until_now()
    {
        Assert.True(Request(from: "2026-11-01").TryGetRange(_time, _errors, out var range));

        Assert.Equal(Now, range.To);
    }

    [Theory]
    [InlineData("2026-11-05", "2026-11-01")] // backwards
    [InlineData("2026-11-01", "2026-11-01")] // empty
    public void From_must_be_before_to(string from, string to)
    {
        Assert.False(Request(from, to).TryGetRange(_time, _errors, out _));
        Assert.Contains("from", _errors.Keys);
    }

    [Fact]
    public void Range_is_at_most_366_days()
    {
        Assert.True(Request("2025-11-07", "2026-11-07").TryGetRange(_time, _errors, out _));
        Assert.False(Request("2025-11-01", "2026-11-07").TryGetRange(_time, _errors, out _));
    }

    [Theory]
    [InlineData("yesterday", null, "from")]
    [InlineData(null, "2026-13-01", "to")]
    public void Unparseable_dates_are_reported(string? from, string? to, string field)
    {
        Assert.False(Request(from, to).TryGetRange(_time, _errors, out _));
        Assert.Contains(field, _errors.Keys);
    }

    // ---- interval ----

    private static readonly DateRange OneWeek = new(Now.AddDays(-7), Now);

    [Theory]
    [InlineData(null, StatsInterval.Day)]
    [InlineData("day", StatsInterval.Day)]
    [InlineData("hour", StatsInterval.Hour)]
    [InlineData("HOUR", StatsInterval.Hour)]
    public void Interval_parses_case_insensitively(string? value, StatsInterval expected)
    {
        Assert.True(Request(interval: value).TryGetInterval(OneWeek, _errors, out var interval));
        Assert.Equal(expected, interval);
    }

    [Fact]
    public void Unknown_interval_is_rejected()
    {
        Assert.False(Request(interval: "week").TryGetInterval(OneWeek, _errors, out _));
        Assert.Contains("interval", _errors.Keys);
    }

    [Fact]
    public void Hourly_is_limited_to_31_days()
    {
        var thirtyOneDays = new DateRange(Now.AddDays(-31), Now);
        var thirtyTwoDays = new DateRange(Now.AddDays(-32), Now);

        Assert.True(Request(interval: "hour").TryGetInterval(thirtyOneDays, _errors, out _));
        Assert.False(Request(interval: "hour").TryGetInterval(thirtyTwoDays, _errors, out _));
    }

    // ---- limit ----

    [Theory]
    [InlineData(null, 10)]
    [InlineData(1, 1)]
    [InlineData(100, 100)]
    public void Valid_limits(int? value, int expected)
    {
        Assert.True(Request(limit: value).TryGetLimit(_errors, out var limit));
        Assert.Equal(expected, limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(101)]
    public void Out_of_range_limits_are_rejected(int value)
    {
        Assert.False(Request(limit: value).TryGetLimit(_errors, out _));
        Assert.Contains("limit", _errors.Keys);
    }
}
