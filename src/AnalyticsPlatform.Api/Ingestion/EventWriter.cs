using ClickHouse.Driver;

namespace AnalyticsPlatform.Api.Ingestion;

// One row of the ClickHouse `events` table, in C# types. Columns not listed here
// (browser, os, country_code, utm_*, ...) keep their DEFAULT '' until Phase 6
// enrichment fills them in.
public sealed record EventRow(
    uint SiteId,
    string EventType,
    string EventName,
    DateTime Timestamp,
    Guid SessionId,
    string VisitorId,
    string Url,
    string Path,
    string Hostname,
    string Referrer,
    string ReferrerDomain,
    string UserAgent,
    ushort ScreenWidth,
    ushort ScreenHeight,
    string IpHash,
    Dictionary<string, string> Properties);

// Writes events to ClickHouse. Phase 2 deliberately does one insert per event
// (simple, easy to debug). Phase 5 replaces this with buffered batch inserts,
// which is how ClickHouse wants to be fed at real volume.
public sealed class EventWriter(ClickHouseClient client)
{
    private static readonly string[] Columns =
    [
        "site_id", "event_type", "event_name", "timestamp", "session_id", "visitor_id",
        "url", "path", "hostname", "referrer", "referrer_domain", "user_agent",
        "screen_width", "screen_height", "ip_hash", "properties",
    ];

    public Task WriteAsync(EventRow e, CancellationToken cancellationToken)
    {
        // Values must be in the same order as Columns above.
        object[] row =
        [
            e.SiteId, e.EventType, e.EventName, e.Timestamp, e.SessionId, e.VisitorId,
            e.Url, e.Path, e.Hostname, e.Referrer, e.ReferrerDomain, e.UserAgent,
            e.ScreenWidth, e.ScreenHeight, e.IpHash, e.Properties,
        ];

        return client.InsertBinaryAsync("events", Columns, [row], cancellationToken: cancellationToken);
    }
}
