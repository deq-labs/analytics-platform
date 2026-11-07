using AnalyticsPlatform.Api.Ingestion;
using ClickHouse.Driver;
using ClickHouse.Driver.ADO.Parameters;
using ClickHouse.Driver.Utility;
using Npgsql;

namespace AnalyticsPlatform.Api.Sites;

// Everything the dashboard's site settings page shows. StandardEvents maps each
// standard event name to on/off.
public sealed record SiteDetails(
    uint Id,
    string Name,
    string Domain,
    string TrackingId,
    string VisitorIdMode,
    SortedDictionary<string, bool> StandardEvents);

// Creating, changing and deleting sites (Phase 7 step 3). Reads for the
// tracker and the stats endpoints stay in SiteConfigStore (cached); every
// change here tells that cache to forget the site, so the tracker sees the new
// settings on its next request instead of up to a minute later.
public sealed class SiteStore(NpgsqlDataSource dataSource, ClickHouseClient clickHouse, SiteConfigStore cache)
{
    public enum CreateOutcome { Created, DomainTaken }

    public async Task<(CreateOutcome Outcome, SiteDetails? Site)> CreateAsync(
        int ownerUserId, string name, string domain, string visitorIdMode, CancellationToken cancellationToken)
    {
        // One transaction: the site and its standard-event rows appear together or not at all.
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        int siteId;
        try
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO sites (name, domain, visitor_id_mode, owner_user_id) VALUES ($1, $2, $3, $4) RETURNING id",
                connection);
            insert.Parameters.AddWithValue(name);
            insert.Parameters.AddWithValue(domain);
            insert.Parameters.AddWithValue(visitorIdMode);
            insert.Parameters.AddWithValue(ownerUserId);
            siteId = (int)(await insert.ExecuteScalarAsync(cancellationToken))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (CreateOutcome.DomainTaken, null);
        }

        // Standard events are opt-out, like GA's Enhanced Measurement: all on.
        await using (var events = new NpgsqlCommand(
            "INSERT INTO site_tracked_events (site_id, event_key, enabled) SELECT $1, key, true FROM unnest($2) AS key",
            connection))
        {
            events.Parameters.AddWithValue(siteId);
            events.Parameters.AddWithValue(EventTypes.StandardEventNames.ToArray());
            await events.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return (CreateOutcome.Created, await GetAsync((uint)siteId, cancellationToken));
    }

    public async Task<SiteDetails?> GetAsync(uint siteId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT s.id, s.name, s.domain, s.tracking_id, s.visitor_id_mode,
                   coalesce(array_agg(e.event_key) FILTER (WHERE e.enabled), '{}') AS enabled
            FROM sites s
            LEFT JOIN site_tracked_events e ON e.site_id = s.id
            WHERE s.id = $1
            GROUP BY s.id
            """);
        command.Parameters.AddWithValue((int)siteId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var enabled = reader.GetFieldValue<string[]>(5);
        // Every known standard event appears, on or off -- missing rows count as off.
        var standardEvents = new SortedDictionary<string, bool>(
            EventTypes.StandardEventNames.ToDictionary(name => name, name => enabled.Contains(name)),
            StringComparer.Ordinal);

        return new SiteDetails((uint)reader.GetInt32(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4), standardEvents);
    }

    // Null arguments mean "leave as is".
    public async Task UpdateAsync(
        uint siteId, string? name, string? visitorIdMode, IReadOnlyDictionary<string, bool>? standardEvents,
        CancellationToken cancellationToken)
    {
        var before = await cache.GetAsync(siteId, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        // coalesce($2, name): keep the current value when the parameter is NULL.
        await using (var update = new NpgsqlCommand(
            "UPDATE sites SET name = coalesce($2, name), visitor_id_mode = coalesce($3, visitor_id_mode) WHERE id = $1",
            connection))
        {
            update.Parameters.AddWithValue((int)siteId);
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)name ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            update.Parameters.Add(new NpgsqlParameter { Value = (object?)visitorIdMode ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        if (standardEvents is { Count: > 0 })
        {
            // "Upsert": insert the row, or update it if it already exists.
            await using var upsert = new NpgsqlCommand("""
                INSERT INTO site_tracked_events (site_id, event_key, enabled)
                SELECT $1, key, enabled FROM unnest($2::text[], $3::boolean[]) AS t(key, enabled)
                ON CONFLICT (site_id, event_key) DO UPDATE SET enabled = excluded.enabled
                """, connection);
            upsert.Parameters.AddWithValue((int)siteId);
            upsert.Parameters.AddWithValue(standardEvents.Keys.ToArray());
            upsert.Parameters.AddWithValue(standardEvents.Values.ToArray());
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        cache.Forget(siteId, before?.TrackingId);
    }

    // A fresh random tracking ID, same format as the column default. The old
    // one stops working at once (e.g. after it was copied somewhere it shouldn't be).
    public async Task RegenerateTrackingIdAsync(uint siteId, CancellationToken cancellationToken)
    {
        var before = await cache.GetAsync(siteId, cancellationToken);
        await using var command = dataSource.CreateCommand(
            "UPDATE sites SET tracking_id = 'ap_' || substr(replace(gen_random_uuid()::text, '-', ''), 1, 16) WHERE id = $1");
        command.Parameters.AddWithValue((int)siteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        cache.Forget(siteId, before?.TrackingId);
    }

    // Deletes the site's events in ClickHouse first, then the site itself: if
    // ClickHouse is down the request fails and nothing has been half-deleted.
    public async Task DeleteAsync(uint siteId, CancellationToken cancellationToken)
    {
        var before = await cache.GetAsync(siteId, cancellationToken);

        // A "lightweight delete": rows disappear from queries immediately and
        // are physically removed by ClickHouse's background merges later.
        foreach (var table in ClickHouseTables.PerSite)
        {
            var parameters = new ClickHouseParameterCollection();
            parameters.AddParameter("site", siteId);
            await clickHouse.ExecuteNonQueryAsync(
                $"DELETE FROM {table} WHERE site_id = {{site:UInt32}}", parameters, cancellationToken: cancellationToken);
        }

        await using var command = dataSource.CreateCommand("DELETE FROM sites WHERE id = $1"); // cascades to site_tracked_events
        command.Parameters.AddWithValue((int)siteId);
        await command.ExecuteNonQueryAsync(cancellationToken);
        cache.Forget(siteId, before?.TrackingId);
    }
}

// ClickHouse tables that hold rows per site_id (deleted along with the site).
public static class ClickHouseTables
{
    public static readonly string[] PerSite = ["events", "pageviews_hourly"];
}
