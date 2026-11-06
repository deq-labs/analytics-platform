using ClickHouse.Driver;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnalyticsPlatform.Api.Health;

// The ClickHouseClient singleton is passed in through the constructor
// (a "primary constructor"), which is how dependency injection hands it over.
public sealed class ClickHouseHealthCheck(ClickHouseClient client) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var version = await client.ExecuteScalarAsync("SELECT version()");
            return HealthCheckResult.Healthy($"ClickHouse {version}");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("ClickHouse is unreachable.", ex);
        }
    }
}
