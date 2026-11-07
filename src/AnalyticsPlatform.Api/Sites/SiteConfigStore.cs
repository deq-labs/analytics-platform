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
public sealed record SiteConfig(
    uint Id,
    VisitorIdMode VisitorIdMode,
    IReadOnlyList<string> EnabledStandardEvents);

// Reads per-site settings from Postgres. Every event needs its site's settings,
// so results are cached in memory for a minute instead of querying Postgres on
// every single event. Unknown sites are cached too (as null) so junk traffic
// can't hammer the database.
public sealed class SiteConfigStore(NpgsqlDataSource dataSource, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

    // One query returns the site's mode plus an array of its enabled standard
    // events. LEFT JOIN keeps the site even if it has no site_tracked_events
    // rows; array_agg(...) FILTER collects only the enabled ones.
    private const string Sql = """
        SELECT s.visitor_id_mode,
               coalesce(array_agg(e.event_key ORDER BY e.event_key)
                        FILTER (WHERE e.enabled), '{}') AS enabled_events
        FROM sites s
        LEFT JOIN site_tracked_events e ON e.site_id = s.id
        WHERE s.id = $1
        GROUP BY s.id
        """;

    public async Task<SiteConfig?> GetAsync(uint siteId, CancellationToken cancellationToken)
    {
        // Postgres `sites.id` is a signed int; anything above int.MaxValue can't exist.
        if (siteId is 0 or > int.MaxValue)
        {
            return null;
        }

        return await cache.GetOrCreateAsync(("site", siteId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;

            // $1 is a parameter placeholder. Never build SQL by gluing strings
            // together with user input -- that's how SQL injection happens.
            await using var command = dataSource.CreateCommand(Sql);
            command.Parameters.AddWithValue((int)siteId);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null; // no such site
            }

            var mode = reader.GetString(0) == "persistent" ? VisitorIdMode.Persistent : VisitorIdMode.Hash;
            var enabledEvents = reader.GetFieldValue<string[]>(1);

            return new SiteConfig(siteId, mode, enabledEvents);
        });
    }
}
