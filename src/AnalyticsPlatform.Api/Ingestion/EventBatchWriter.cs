using Microsoft.Extensions.Options;

namespace AnalyticsPlatform.Api.Ingestion;

// Drains EventQueue into ClickHouse in batches, on a background task that runs
// for the life of the app. A BackgroundService is .NET's base class for that:
// the host calls StartAsync when the app starts and StopAsync when it shuts down.
//
// Guarantees (see EventBatchWriterTests):
//   * a batch is written when it reaches MaxBatchSize, or FlushInterval after
//     its first event -- whichever comes first;
//   * a failed insert is retried with exponential backoff, keeping the same
//     batch, so events aren't lost while ClickHouse is briefly down;
//   * on shutdown, everything still queued is written before the app exits,
//     unless the host's shutdown deadline passes first (then the loss is logged).
//
// Delivery is "at least once": if ClickHouse stored a batch but the reply was
// lost (e.g. a network timeout), the retry inserts it again. Rare; fine for v1.
public sealed class EventBatchWriter(
    EventQueue queue,
    IEventStore store,
    IOptions<IngestionOptions> options,
    ILogger<EventBatchWriter> logger) : BackgroundService
{
    private readonly IngestionOptions _options = options.Value;

    // Cancelled only when the host's shutdown deadline passes ("give up now").
    // Not the same as BackgroundService's stoppingToken, which fires as soon as
    // shutdown *starts* -- too early, because we still want to drain the queue.
    private readonly CancellationTokenSource _giveUp = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<EventRow>(_options.MaxBatchSize);
        try
        {
            // WaitToReadAsync returns false once the queue is completed (shutdown)
            // AND empty -- so this loop keeps draining during shutdown.
            while (await queue.Reader.WaitToReadAsync(_giveUp.Token))
            {
                await FillBatchAsync(batch);
                await InsertWithRetryAsync(batch);
                batch.Clear();
            }
        }
        // Any exception once we've been told to give up -- a cancelled delay, or
        // the store failing one last time -- means "stop and report the loss".
        catch (Exception) when (_giveUp.IsCancellationRequested)
        {
            var lost = batch.Count + queue.Count;
            if (lost > 0)
            {
                logger.LogError("Shutdown deadline reached: {Count} buffered events were not written.", lost);
            }
        }
    }

    // Collects events until the batch is full, the flush interval has passed
    // since this batch started, or the queue is shut down and empty.
    private async Task FillBatchAsync(List<EventRow> batch)
    {
        var reader = queue.Reader;
        using var flushTimer = CancellationTokenSource.CreateLinkedTokenSource(_giveUp.Token);
        flushTimer.CancelAfter(_options.FlushInterval);

        try
        {
            while (true)
            {
                // Take everything that's already waiting (no awaiting = fast).
                while (batch.Count < _options.MaxBatchSize && reader.TryRead(out var row))
                {
                    batch.Add(row);
                }

                if (batch.Count >= _options.MaxBatchSize || !await reader.WaitToReadAsync(flushTimer.Token))
                {
                    return; // full, or queue completed and drained
                }
            }
        }
        catch (OperationCanceledException) when (!_giveUp.IsCancellationRequested)
        {
            // Flush interval elapsed: write the partial batch.
        }
    }

    private async Task InsertWithRetryAsync(List<EventRow> batch)
    {
        var delay = _options.InitialRetryDelay;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await store.InsertAsync(batch, _giveUp.Token);
                return;
            }
            catch (Exception ex) when (!_giveUp.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Inserting {Count} events failed (attempt {Attempt}); retrying in {Delay}. {Queued} more waiting.",
                    batch.Count, attempt, delay, queue.Count);

                await Task.Delay(delay, _giveUp.Token);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
            }
        }
    }

    // `cancellationToken` here is the host's shutdown deadline.
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete(); // stop accepting; ExecuteAsync drains the rest and exits

        // base.StopAsync returns when ExecuteAsync finishes OR the deadline passes.
        // If it was the deadline, tell ExecuteAsync to give up now.
        //
        // (Don't do this with a `using var` token registration: token callbacks
        // run newest-first, so base.StopAsync's own callback completes this
        // method -- disposing our registration -- before ours ever runs. That
        // left the writer retrying forever; EventBatchWriterTests caught it.)
        await base.StopAsync(cancellationToken);
        if (cancellationToken.IsCancellationRequested)
        {
            await _giveUp.CancelAsync();
        }
    }

    public override void Dispose()
    {
        _giveUp.Dispose();
        base.Dispose();
    }
}
