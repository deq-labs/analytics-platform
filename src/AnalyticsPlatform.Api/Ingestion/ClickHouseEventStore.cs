using AnalyticsPlatform.Api.Enrichment;
using ClickHouse.Driver;

namespace AnalyticsPlatform.Api.Ingestion;

// One row of the ClickHouse `events` table, in C# types. Columns not listed here
// (country_code, region, city) keep their DEFAULT '' until GeoIP enrichment.
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
    Dictionary<string, string> Properties,
    ClientInfo Client,
    UtmTags Utm);

// Where batches of events get stored. EventBatchWriter only knows this
// interface, so unit tests can swap in a fake that fails on demand.
public interface IEventStore
{
    Task InsertAsync(IReadOnlyList<EventRow> batch, CancellationToken cancellationToken);
}

// The real store: one INSERT per batch into ClickHouse's `events` table.
public sealed class ClickHouseEventStore(ClickHouseClient client) : IEventStore
{
    private static readonly string[] Columns =
    [
        "site_id", "event_type", "event_name", "timestamp", "session_id", "visitor_id",
        "url", "path", "hostname", "referrer", "referrer_domain", "user_agent",
        "screen_width", "screen_height", "ip_hash", "properties",
        "browser", "browser_version", "os", "os_version", "device_type", "is_bot",
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content",
    ];

    public Task InsertAsync(IReadOnlyList<EventRow> batch, CancellationToken cancellationToken)
    {
        // Values must be in the same order as Columns above. `Select` is LINQ:
        // it turns each EventRow into an object[] lazily, as the driver reads them.
        var rows = batch.Select(e => new object[]
        {
            e.SiteId, e.EventType, e.EventName, e.Timestamp, e.SessionId, e.VisitorId,
            e.Url, e.Path, e.Hostname, e.Referrer, e.ReferrerDomain, e.UserAgent,
            e.ScreenWidth, e.ScreenHeight, e.IpHash, e.Properties,
            e.Client.Browser, e.Client.BrowserVersion, e.Client.Os, e.Client.OsVersion, e.Client.DeviceType,
            (byte)(e.Client.IsBot ? 1 : 0), // ClickHouse UInt8
            e.Utm.Source, e.Utm.Medium, e.Utm.Campaign, e.Utm.Term, e.Utm.Content,
        });

        return client.InsertBinaryAsync("events", Columns, rows, cancellationToken: cancellationToken);
    }
}
