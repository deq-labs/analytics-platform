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
public sealed record SiteConfig(uint Id, VisitorIdMode VisitorIdMode);

// Reads per-site settings from Postgres. Every event needs its site's settings,
// so results are cached in memory for a minute instead of querying Postgres on
// every single event. Unknown sites are cached too (as null) so junk traffic
// can't hammer the database.
public sealed class SiteConfigStore(NpgsqlDataSource dataSource, IMemoryCache cache)
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(1);

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
            await using var command = dataSource.CreateCommand(
                "SELECT visitor_id_mode FROM sites WHERE id = $1");
            command.Parameters.AddWithValue((int)siteId);

            var mode = (string?)await command.ExecuteScalarAsync(cancellationToken);

            return mode switch
            {
                null => null,
                "persistent" => new SiteConfig(siteId, VisitorIdMode.Persistent),
                _ => new SiteConfig(siteId, VisitorIdMode.Hash),
            };
        });
    }
}
