using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace AnalyticsPlatform.Api.Health;

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            // `await using` returns the connection to the pool when we're done.
            await using var command = dataSource.CreateCommand("SELECT current_setting('server_version')");
            var version = await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy($"Postgres {version}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Postgres is unreachable.", ex);
        }
    }
}
