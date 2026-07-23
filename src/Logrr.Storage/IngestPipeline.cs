using System.Collections.Concurrent;
using System.Threading.Channels;
using Logrr.Core;

namespace Logrr.Storage;

/// <summary>
/// The write path (SPEC §4.5): a bounded channel per app feeding a per-app batch-writer
/// loop that flushes on 500 events or 500 ms, commits one transaction, then fans the
/// committed batch out to observers off the writer thread. A full channel is a signal to
/// return HTTP 429 — the request thread never blocks on disk.
/// </summary>
public sealed class IngestPipeline(
    PartitionManager partitions,
    StorageOptions options,
    Func<string, IReadOnlyList<string>> indexedPropertiesFor,
    Action<CommitBatch> onCommit,
    Action<string, Exception>? onError = null)
{
    private sealed class Lane
    {
        public required Channel<LogEvent> Channel { get; init; }
        public required Task Loop { get; init; }
        public long Dropped;
    }

    private readonly ConcurrentDictionary<string, Lane> _lanes = new();
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Enqueue an event for an app. Returns false when the channel is full (caller returns
    /// 429), incrementing that app's dropped-event counter.
    /// </summary>
    public bool TryEnqueue(string appId, LogEvent e)
    {
        var lane = _lanes.GetOrAdd(appId, StartLane);
        if (lane.Channel.Writer.TryWrite(e))
        {
            return true;
        }
        Interlocked.Increment(ref lane.Dropped);
        return false;
    }

    public long DroppedTotal => _lanes.Values.Sum(l => Interlocked.Read(ref l.Dropped));

    public int QueueDepth => _lanes.Values.Sum(l => l.Channel.Reader.Count);

    private Lane StartLane(string appId)
    {
        var channel = Channel.CreateBounded<LogEvent>(new BoundedChannelOptions(options.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite, // TryWrite returns false when full
        });

        return new Lane
        {
            Channel = channel,
            Loop = Task.Run(() => RunAsync(appId, channel, _shutdown.Token)),
        };
    }

    private async Task RunAsync(string appId, Channel<LogEvent> channel, CancellationToken ct)
    {
        var reader = channel.Reader;
        var batch = new List<LogEvent>(options.BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                batch.Clear();

                // Accumulate until BatchSize events or FlushIntervalMs elapses — whichever
                // first (SPEC §4.5). A linked CTS gives the flush deadline without leaving
                // a dangling channel waiter.
                using var flush = CancellationTokenSource.CreateLinkedTokenSource(ct);
                flush.CancelAfter(options.FlushIntervalMs);

                while (batch.Count < options.BatchSize)
                {
                    try
                    {
                        batch.Add(await reader.ReadAsync(flush.Token).ConfigureAwait(false));
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        break; // flush deadline reached
                    }
                    catch (ChannelClosedException)
                    {
                        break; // writer completed and channel drained
                    }
                }

                if (batch.Count > 0)
                {
                    Flush(appId, batch);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Hard shutdown after the drain budget elapsed.
        }
        catch (Exception ex)
        {
            onError?.Invoke(appId, ex);
        }

        DrainRemaining(appId, reader);
    }

    private void DrainRemaining(string appId, ChannelReader<LogEvent> reader)
    {
        var batch = new List<LogEvent>(options.BatchSize);
        while (reader.TryRead(out var e))
        {
            batch.Add(e);
            if (batch.Count >= options.BatchSize)
            {
                Flush(appId, batch);
                batch.Clear();
            }
        }
        if (batch.Count > 0)
        {
            Flush(appId, batch);
        }
    }

    private void Flush(string appId, List<LogEvent> batch)
    {
        try
        {
            var indexed = indexedPropertiesFor(appId);
            foreach (var group in batch.GroupBy(e => StoragePaths.DayOf(e.Timestamp)))
            {
                var day = group.Key;
                var rows = partitions.GetWriter(appId, day, indexed).InsertBatch(group.ToList());
                if (rows.Count > 0)
                {
                    onCommit(new CommitBatch(appId, day, rows));
                }
            }
        }
        catch (Exception ex)
        {
            onError?.Invoke(appId, ex);
        }
    }

    /// <summary>
    /// Complete all channels and await the writer loops within a budget (SPEC §13 graceful
    /// shutdown). Remaining buffered events are drained before each loop exits.
    /// </summary>
    public async Task CompleteAndDrainAsync(TimeSpan budget)
    {
        foreach (var lane in _lanes.Values)
        {
            lane.Channel.Writer.TryComplete();
        }

        var loops = _lanes.Values.Select(l => l.Loop).ToArray();
        try
        {
            await Task.WhenAny(Task.WhenAll(loops), Task.Delay(budget)).ConfigureAwait(false);
        }
        finally
        {
            _shutdown.Cancel();
        }
    }
}
