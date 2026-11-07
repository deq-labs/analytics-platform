using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace AnalyticsPlatform.Api.Ingestion;

// The in-memory buffer between the HTTP endpoint (many writers, one per
// request) and EventBatchWriter (one reader).
//
// Channel<T> is .NET's built-in thread-safe producer/consumer queue. "Bounded"
// means it has a fixed capacity: when full, TryWrite returns false instead of
// growing, and the endpoint turns that into a 503.
public sealed class EventQueue
{
    private readonly Channel<EventRow> _channel;

    public EventQueue(IOptions<IngestionOptions> options)
    {
        _channel = Channel.CreateBounded<EventRow>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait, // TryWrite fails when full; nothing is silently dropped
            SingleReader = true,                    // lets the channel use a faster implementation
            SingleWriter = false,
        });
    }

    // Never blocks: true if queued, false if the queue is full or shut down.
    public bool TryEnqueue(EventRow row) => _channel.Writer.TryWrite(row);

    // Events waiting to be written.
    public int Count => _channel.Reader.Count;

    public ChannelReader<EventRow> Reader => _channel.Reader;

    // Called at shutdown: no new events; the reader drains what's left and then stops.
    public void Complete() => _channel.Writer.TryComplete();
}
