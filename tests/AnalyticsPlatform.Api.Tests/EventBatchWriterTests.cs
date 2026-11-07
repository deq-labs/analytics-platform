using AnalyticsPlatform.Api.Enrichment;
using AnalyticsPlatform.Api.Ingestion;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AnalyticsPlatform.Api.Tests;

// Phase 5: events go into an in-memory queue and a background writer inserts
// them into ClickHouse in batches. These tests pin down the guarantees:
// bounded batches, timely flushes, retries without loss or duplicates, a final
// flush on shutdown, and a bounded queue.
public class EventBatchWriterTests : IAsyncDisposable
{
    // A stand-in for ClickHouse that records every batch and can be told to fail.
    private sealed class FakeStore : IEventStore
    {
        private readonly Lock _lock = new();
        private readonly List<IReadOnlyList<EventRow>> _batches = [];

        public int FailuresToSimulate { get; set; }
        public bool AlwaysFail { get; set; }
        public int Attempts { get; private set; }

        public IReadOnlyList<IReadOnlyList<EventRow>> Batches
        {
            get { lock (_lock) return [.. _batches]; }
        }

        public IReadOnlyList<EventRow> AllRows => [.. Batches.SelectMany(b => b)];

        public Task InsertAsync(IReadOnlyList<EventRow> batch, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                Attempts++;
                if (AlwaysFail || FailuresToSimulate > 0)
                {
                    FailuresToSimulate--;
                    throw new InvalidOperationException("ClickHouse is down (simulated).");
                }

                _batches.Add([.. batch]); // copy: the writer may reuse its list
            }

            return Task.CompletedTask;
        }
    }

    private readonly FakeStore _store = new();
    private EventQueue _queue = null!;
    private EventBatchWriter _writer = null!;

    private void Create(
        int maxBatchSize = 1_000,
        TimeSpan? flushInterval = null,
        int queueCapacity = 10_000)
    {
        var options = Options.Create(new IngestionOptions
        {
            MaxBatchSize = maxBatchSize,
            FlushInterval = flushInterval ?? TimeSpan.FromMilliseconds(50),
            QueueCapacity = queueCapacity,
            InitialRetryDelay = TimeSpan.FromMilliseconds(10),
            MaxRetryDelay = TimeSpan.FromMilliseconds(50),
        });
        _queue = new EventQueue(options);
        _writer = new EventBatchWriter(_queue, _store, options, NullLogger<EventBatchWriter>.Instance);
    }

    private static EventRow Row(int i) => new(
        SiteId: 1, EventType: "pageview", EventName: "", Timestamp: DateTime.UtcNow,
        SessionId: Guid.NewGuid(), VisitorId: "h:test", Url: $"https://site.test/{i}", Path: $"/{i}",
        Hostname: "site.test", Referrer: "", ReferrerDomain: "", UserAgent: "", ScreenWidth: 0,
        ScreenHeight: 0, IpHash: "", Properties: [],
        Client: new("Chrome", "140", "Windows", "10", "desktop", IsBot: false), Utm: UtmTags.None, Geo: GeoLocation.Unknown);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // Polls until the condition holds (the writer runs on a background task).
    private static async Task Eventually(Func<bool> condition, int timeoutMs = 5_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the background writer.");
            await Task.Delay(10, Ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is not null) await _writer.StopAsync(CancellationToken.None);
        _writer?.Dispose();
    }

    [Fact]
    public async Task Batches_never_exceed_the_maximum_size()
    {
        // A long flush interval, so only "batch full" and shutdown trigger writes.
        Create(maxBatchSize: 5, flushInterval: TimeSpan.FromMinutes(10));
        for (var i = 0; i < 12; i++) Assert.True(_queue.TryEnqueue(Row(i)));

        await _writer.StartAsync(Ct);
        await Eventually(() => _store.Batches.Count >= 2);
        await _writer.StopAsync(Ct); // flushes the last, partial batch

        Assert.Equal([5, 5, 2], _store.Batches.Select(b => b.Count));
        Assert.Equal(Enumerable.Range(0, 12).Select(i => $"/{i}"), _store.AllRows.Select(r => r.Path));
    }

    [Fact]
    public async Task A_partial_batch_is_written_after_the_flush_interval()
    {
        Create(maxBatchSize: 1_000, flushInterval: TimeSpan.FromMilliseconds(100));
        await _writer.StartAsync(Ct);

        _queue.TryEnqueue(Row(1));
        _queue.TryEnqueue(Row(2));
        _queue.TryEnqueue(Row(3));

        await Eventually(() => _store.AllRows.Count == 3);
        Assert.Single(_store.Batches);
    }

    [Fact]
    public async Task Failed_inserts_are_retried_without_losing_or_duplicating_events()
    {
        Create();
        _store.FailuresToSimulate = 2;
        await _writer.StartAsync(Ct);

        for (var i = 0; i < 5; i++) _queue.TryEnqueue(Row(i));

        await Eventually(() => _store.AllRows.Count == 5);
        Assert.Equal(3, _store.Attempts); // 2 failures + 1 success
        Assert.Equal(Enumerable.Range(0, 5).Select(i => $"/{i}"), _store.AllRows.Select(r => r.Path));
    }

    [Fact]
    public async Task Stopping_writes_everything_still_buffered()
    {
        Create(flushInterval: TimeSpan.FromHours(1)); // would never flush on its own
        await _writer.StartAsync(Ct);
        for (var i = 0; i < 3; i++) _queue.TryEnqueue(Row(i));

        await Task.Delay(100, Ct);
        Assert.Empty(_store.AllRows); // still buffered

        await _writer.StopAsync(Ct);

        Assert.Equal(3, _store.AllRows.Count);
    }

    [Fact]
    public async Task A_full_queue_rejects_new_events()
    {
        Create(queueCapacity: 2); // writer not started, so nothing drains

        Assert.True(_queue.TryEnqueue(Row(1)));
        Assert.True(_queue.TryEnqueue(Row(2)));
        Assert.False(_queue.TryEnqueue(Row(3)));
        Assert.Equal(2, _queue.Count);

        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_stopped_queue_rejects_new_events()
    {
        Create();
        await _writer.StartAsync(Ct);
        await _writer.StopAsync(Ct);

        Assert.False(_queue.TryEnqueue(Row(1)));
    }

    [Fact]
    public async Task Shutdown_gives_up_at_the_deadline_if_the_store_stays_down()
    {
        Create();
        _store.AlwaysFail = true;
        await _writer.StartAsync(Ct);
        _queue.TryEnqueue(Row(1));
        await Eventually(() => _store.Attempts >= 1);

        // The host gives hosted services a deadline to stop; here 300 ms.
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var stopping = _writer.StopAsync(deadline.Token);

        var finished = await Task.WhenAny(stopping, Task.Delay(TimeSpan.FromSeconds(5), Ct));
        Assert.Same(stopping, finished); // didn't hang
        Assert.Empty(_store.AllRows);
    }
}
