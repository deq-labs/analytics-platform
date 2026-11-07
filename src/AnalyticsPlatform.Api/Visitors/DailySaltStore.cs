using System.Security.Cryptography;
using Npgsql;

namespace AnalyticsPlatform.Api.Visitors;

// Provides today's random salt for cookie-less hashing (see CLAUDE.md
// "Visitor identification"). The salt lives in Postgres so every API restart
// (and, later, every API instance) uses the same one; it's also kept in memory
// so Postgres is only asked once per day.
public sealed class DailySaltStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IDailySaltProvider
{
    private sealed record SaltForDay(DateOnly Day, byte[] Salt);

    // Many requests run at the same time. The SemaphoreSlim makes sure only one
    // of them goes to Postgres when the day rolls over; the others wait for it.
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private volatile SaltForDay? _current;

    public async Task<byte[]> GetTodaysSaltAsync(CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Fast path: almost every call ends here.
        var current = _current;
        if (current?.Day == today)
        {
            return current.Salt;
        }

        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Another request may have refreshed it while we were waiting.
            current = _current;
            if (current?.Day == today)
            {
                return current.Salt;
            }

            var salt = await LoadOrCreateAsync(today, cancellationToken);
            _current = new SaltForDay(today, salt);
            return salt;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private async Task<byte[]> LoadOrCreateAsync(DateOnly today, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

        // Create today's salt unless it already exists. ON CONFLICT DO NOTHING
        // makes this safe even if two API instances try at the same moment.
        await using (var insert = new NpgsqlCommand(
            "INSERT INTO daily_salts (day, salt) VALUES ($1, $2) ON CONFLICT (day) DO NOTHING",
            connection))
        {
            insert.Parameters.AddWithValue(today);
            insert.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        // Delete older salts. This is the privacy guarantee: once yesterday's
        // salt is gone, yesterday's visitor hashes can't be recomputed or linked.
        await using (var delete = new NpgsqlCommand(
            "DELETE FROM daily_salts WHERE day < $1", connection))
        {
            delete.Parameters.AddWithValue(today);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = new NpgsqlCommand(
            "SELECT salt FROM daily_salts WHERE day = $1", connection);
        select.Parameters.AddWithValue(today);
        return (byte[])(await select.ExecuteScalarAsync(cancellationToken))!;
    }
}
