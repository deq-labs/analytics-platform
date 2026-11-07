using AnalyticsPlatform.Api.Visitors;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Time.Testing;

namespace AnalyticsPlatform.Api.Tests;

public class SessionTrackerTests
{
    // MemoryCache reads time from an ISystemClock. This adapter makes it use
    // the FakeTimeProvider, so tests can jump ahead 31 minutes instantly.
    private sealed class FakeClock(TimeProvider time) : ISystemClock
    {
        public DateTimeOffset UtcNow => time.GetUtcNow();
    }

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    private readonly SessionTracker _sessions;

    public SessionTrackerTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions { Clock = new FakeClock(_time) });
        _sessions = new SessionTracker(cache);
    }

    [Fact]
    public void Events_close_together_share_a_session()
    {
        var first = _sessions.GetSessionId(1, "h:abc");
        _time.Advance(TimeSpan.FromMinutes(5));
        var second = _sessions.GetSessionId(1, "h:abc");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Each_event_extends_the_session_sliding_window()
    {
        var first = _sessions.GetSessionId(1, "h:abc");

        // 3 x 20 minutes = an hour in total, but never 30 minutes without activity.
        for (var i = 0; i < 3; i++)
        {
            _time.Advance(TimeSpan.FromMinutes(20));
            Assert.Equal(first, _sessions.GetSessionId(1, "h:abc"));
        }
    }

    [Fact]
    public void Thirty_minutes_of_inactivity_starts_a_new_session()
    {
        var first = _sessions.GetSessionId(1, "h:abc");
        _time.Advance(SessionTracker.InactivityTimeout + TimeSpan.FromSeconds(1));
        var second = _sessions.GetSessionId(1, "h:abc");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Different_visitors_get_different_sessions() =>
        Assert.NotEqual(_sessions.GetSessionId(1, "h:abc"), _sessions.GetSessionId(1, "h:def"));

    [Fact]
    public void Same_visitor_id_on_different_sites_gets_different_sessions() =>
        Assert.NotEqual(_sessions.GetSessionId(1, "p:same"), _sessions.GetSessionId(2, "p:same"));

    [Fact]
    public void Session_ids_are_version_7_guids() =>
        Assert.Equal(7, _sessions.GetSessionId(1, "h:abc").Version);
}
