using Npgsql;

namespace AnalyticsPlatform.Api.Auth;

public sealed record AppUser(int Id, string Email, string PasswordHash, bool IsAdmin);

// A row in the admin's user list.
public sealed record UserSummary(int Id, string Email, bool IsAdmin, int Sites, DateTime CreatedAt);

public enum RegisterOutcome
{
    Created,
    RegistrationClosed, // invite-only and no invite given
    InvalidInvite,      // unknown, expired, or already used
    EmailTaken,
}

// Users and invites in Postgres (tables in postgres/init/004_users.sql).
public sealed class UserStore(NpgsqlDataSource dataSource)
{
    // Any fixed number: all registrations take this lock, one at a time.
    private const long RegistrationLockKey = 0x41505F5245_47; // "AP_REG"

    public async Task<bool> AnyUsersAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT EXISTS (SELECT 1 FROM users)");
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    public async Task<AppUser?> FindByIdAsync(int id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, email, password_hash, is_admin FROM users WHERE id = $1");
        command.Parameters.AddWithValue(id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AppUser(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    public async Task<List<UserSummary>> ListAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            SELECT u.id, u.email, u.is_admin, count(s.id)::int, u.created_at
            FROM users u LEFT JOIN sites s ON s.owner_user_id = u.id
            GROUP BY u.id ORDER BY u.created_at
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var users = new List<UserSummary>();
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(new UserSummary(reader.GetInt32(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3), reader.GetDateTime(4)));
        }

        return users;
    }

    // The ids of the sites a user owns (deleted with them).
    public async Task<List<uint>> SiteIdsOwnedByAsync(int userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT id FROM sites WHERE owner_user_id = $1");
        command.Parameters.AddWithValue(userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<uint>();
        while (await reader.ReadAsync(cancellationToken)) ids.Add((uint)reader.GetInt32(0));
        return ids;
    }

    public async Task<bool> DeleteAsync(int userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM users WHERE id = $1");
        command.Parameters.AddWithValue(userId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<AppUser?> FindByEmailAsync(string email, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, email, password_hash, is_admin FROM users WHERE lower(email) = lower($1)");
        command.Parameters.AddWithValue(email);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new AppUser(reader.GetInt32(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    // Creates an account. Everything happens in one transaction so the rules
    // can't be raced:
    //   * a Postgres advisory lock makes registrations run one at a time, so two
    //     simultaneous sign-ups on a fresh install can't both become admin;
    //   * the invite is checked and marked used in the same transaction.
    public async Task<(RegisterOutcome Outcome, AppUser? User)> RegisterAsync(
        string email, string passwordHash, byte[]? inviteTokenHash, bool openRegistration,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await Execute(connection, "SELECT pg_advisory_xact_lock($1)", cancellationToken, RegistrationLockKey);

        var isFirstUser = !(bool)(await Scalar(connection, "SELECT EXISTS (SELECT 1 FROM users)", cancellationToken))!;

        int? inviteId = null;
        if (!isFirstUser && !openRegistration)
        {
            if (inviteTokenHash is null)
            {
                return (RegisterOutcome.RegistrationClosed, null);
            }

            // FOR UPDATE locks the invite row until we commit.
            inviteId = (int?)await Scalar(connection, """
                SELECT id FROM invites
                WHERE token_hash = $1 AND used_at IS NULL AND expires_at > now()
                FOR UPDATE
                """, cancellationToken, inviteTokenHash);
            if (inviteId is null)
            {
                return (RegisterOutcome.InvalidInvite, null);
            }
        }

        int userId;
        try
        {
            userId = (int)(await Scalar(connection,
                "INSERT INTO users (email, password_hash, is_admin) VALUES ($1, $2, $3) RETURNING id",
                cancellationToken, email, passwordHash, isFirstUser))!;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return (RegisterOutcome.EmailTaken, null); // users_email_key (case-insensitive)
        }

        if (inviteId is not null)
        {
            await Execute(connection, "UPDATE invites SET used_by = $1, used_at = now() WHERE id = $2",
                cancellationToken, userId, inviteId.Value);
        }

        if (isFirstUser)
        {
            // The admin takes over sites from before accounts existed.
            await Execute(connection, "UPDATE sites SET owner_user_id = $1 WHERE owner_user_id IS NULL",
                cancellationToken, userId);
        }

        await transaction.CommitAsync(cancellationToken);
        return (RegisterOutcome.Created, new AppUser(userId, email, passwordHash, isFirstUser));
    }

    public async Task CreateInviteAsync(int createdBy, byte[] tokenHash, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO invites (token_hash, created_by, expires_at) VALUES ($1, $2, $3)");
        command.Parameters.AddWithValue(tokenHash);
        command.Parameters.AddWithValue(createdBy);
        command.Parameters.AddWithValue(expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Small helpers for running SQL on the transaction's connection.
    private static async Task<object?> Scalar(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters) command.Parameters.AddWithValue(p);
        var result = await command.ExecuteScalarAsync(ct);
        return result is DBNull ? null : result;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql, CancellationToken ct, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var p in parameters) command.Parameters.AddWithValue(p);
        await command.ExecuteNonQueryAsync(ct);
    }
}
