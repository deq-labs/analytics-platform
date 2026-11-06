using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnalyticsPlatform.Api.Health;

// By default /health only returns "Healthy" or "Unhealthy". This writes a JSON
// body showing each check's result so you can see which database is down.
// Exception details are deliberately left out so connection info never leaks.
public static class HealthResponseWriter
{
    public static Task WriteJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";

        var body = new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = Math.Round(entry.Value.Duration.TotalMilliseconds, 1),
                }),
        };

        return context.Response.WriteAsync(JsonSerializer.Serialize(body));
    }
}
