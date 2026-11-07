using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace AnalyticsPlatform.Api.Sites;

public enum VisitorIdMode
{
    Hash,
    Persistent,
}

// A `record` is a class meant to hold data: the compiler generates the
// constructor, properties, equality, and ToString() from this one line.
// TrackingId and Domain have defaults so tests that only care about the
// visitor-ID mode can keep writing `new SiteConfig(1, VisitorIdMode.Hash, [])`.
public sealed record SiteConfig(
    uint Id,
    VisitorIdMode VisitorIdMode,
    IReadOnlyList<string> EnabledStandardEvents,
    string TrackingId = "",
    string Domain = "",
    int? OwnerUserId = null);

// Reads per-site settings from Postgres. Every event needs its site's settings,
// so results are cached in memory for a minute instead of querying Postgres on
// every single event. Unknown sites are cached too (as null) so junk traffic
// can't hammer the database.
//
// Sites can be looked up two ways:
//   * by numeric id     -- the dashboard / stats endpoints (internal)
//   * by tracking ID    -- the tracking snippet (public, see 003_tracking_ids.sql)
public sealed class SiteConfigStore(NpgsqlDataSource dataSource, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    // One query returns the site plus an array of its enabled standard events.
    // LEFT JOIN keeps the site even if it has no site_tracked_events rows;
    // array_agg(...) FILTER collects only the enabled ones. {0} is the WHERE
    // condition -- one of two fixed strings below, never user input.
    private const string SqlTemplate = """
        SELECT s.id, s.visitor_id_mode, s.tracking_id, s.domain, s.owner_user_id,
               coalesce(array_agg(e.event_key ORDER BY e.event_key)
                        FILTER (WHERE e.enabled), '{{}}') AS enabled_events
        FROM sites s
        LEFT JOIN site_tracked_events e ON e.site_id = s.id
        WHERE {0}
        GROUP BY s.id
        """;

    private static readonly string ById = string.Format(SqlTemplate, "s.id = $1");
    private static readonly string ByTrackingId = string.Format(SqlTemplate, "s.tracking_id = $1");

    public async Task<SiteConfig?> GetAsync(uint siteId, CancellationToken cancellationToken)
    {
        // Postgres `sites.id` is a signed int; anything above int.MaxValue can't exist.
        if (siteId is 0 or > int.MaxValue)
        {
            return null;
        }

        return await cache.GetOrCreateAsync(("site", siteId), entry =>
            LoadAsync(entry, ById, (int)siteId, cancellationToken));
    }

    public async Task<SiteConfig?> GetByTrackingIdAsync(string trackingId, CancellationToken cancellationToken) =>
        await cache.GetOrCreateAsync(("tracking", trackingId), entry =>
            LoadAsync(entry, ByTrackingId, trackingId, cancellationToken));

    // Drops a site's cached settings after it changes, under both of its keys.
    public void Forget(uint siteId, string? trackingId)
    {
        cache.Remove(("site", siteId));
        if (trackingId is not null)
        {
            cache.Remove(("tracking", trackingId));
        }
    }

    private async Task<SiteConfig?> LoadAsync(
        ICacheEntry entry, string sql, object key, CancellationToken cancellationToken)
    {
        entry.AbsoluteExpirationRelativeToNow = CacheDuration;

        // $1 is a parameter placeholder. Never build SQL by gluing strings
        // together with user input -- that's how SQL injection happens.
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.AddWithValue(key);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null; // no such site
        }

        return new SiteConfig(
            Id: (uint)reader.GetInt32(0),
            VisitorIdMode: reader.GetString(1) == "persistent" ? VisitorIdMode.Persistent : VisitorIdMode.Hash,
            EnabledStandardEvents: reader.GetFieldValue<string[]>(5),
            TrackingId: reader.GetString(2),
            Domain: reader.GetString(3),
            OwnerUserId: reader.IsDBNull(4) ? null : reader.GetInt32(4));
    }
}
