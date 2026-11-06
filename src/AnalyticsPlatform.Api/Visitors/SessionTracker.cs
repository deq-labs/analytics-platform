using Microsoft.Extensions.Caching.Memory;

namespace AnalyticsPlatform.Api.Visitors;

// Server-side sessions: a visitor's events share one session ID until they've
// been inactive for 30 minutes (the same rule GA uses).
//
// How: the session ID sits in an in-memory cache entry with a *sliding*
// expiration. Every event from that visitor reads the entry, which pushes its
// expiry another 30 minutes out. After 30 quiet minutes it expires, and the next
// event creates a new session.
//
// Known v1 limits (fine for now, revisit with Phase 5 batching):
//   * Restarting the API starts new sessions for anyone mid-visit.
//   * Only works with one API instance; several would each have their own cache.
//   * Two simultaneous first events from a new visitor could get two sessions.
public sealed class SessionTracker(IMemoryCache cache)
{
    public static readonly TimeSpan InactivityTimeout = TimeSpan.FromMinutes(30);

    public Guid GetSessionId(uint siteId, string visitorId) =>
        cache.GetOrCreate(("session", siteId, visitorId), entry =>
        {
            entry.SlidingExpiration = InactivityTimeout;

            // Version 7 GUIDs start with a timestamp, so they sort by creation time.
            return Guid.CreateVersion7();
        });
}
