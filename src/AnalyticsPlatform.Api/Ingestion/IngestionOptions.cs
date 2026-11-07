namespace AnalyticsPlatform.Api.Ingestion;

// Settings for buffered ingestion, bound from the "Ingestion" section of
// configuration (appsettings.json, environment variables like
// Ingestion__MaxBatchSize, or test overrides). This is the *options pattern*:
// a plain class + services.AddOptions<T>().Bind(...), injected as IOptions<T>.
public sealed class IngestionOptions
{
    // How many events may wait in memory. When full, the API answers 503 so
    // memory can't grow without limit (e.g. while ClickHouse is down).
    public int QueueCapacity { get; set; } = 100_000;

    // ClickHouse prefers few large inserts over many small ones; each insert
    // creates a "part" on disk that background merges must later combine.
    public int MaxBatchSize { get; set; } = 5_000;

    // How long to wait for a batch to fill before writing what we have.
    // This is the worst-case delay before an event shows up in the dashboard.
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Retry backoff when an insert fails: 0.5s, 1s, 2s, ... up to the max.
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMilliseconds(500);
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromSeconds(30);
}
