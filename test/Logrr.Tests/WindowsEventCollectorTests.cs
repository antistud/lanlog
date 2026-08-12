using Logrr.Contracts;
using Logrr.Server.Ingest;
using Logrr.Server.WindowsEvents;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Tests;

/// <summary>
/// The Windows Event Log collector (SPEC §6.4). Everything here runs off Windows: the collector
/// talks to <see cref="IWindowsEventSource"/>, so the cursor, catch-up and log-clear behaviour
/// is exercised without an event log.
/// </summary>
public class WindowsEventCollectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-winevt-" + Guid.NewGuid().ToString("N"));
    private DateTimeOffset _now = new(2026, 8, 6, 12, 0, 0, TimeSpan.Zero);

    // ---- Fake source -------------------------------------------------------------------

    private sealed class FakeSource : IWindowsEventSource
    {
        public List<WindowsEventRecord> Records { get; set; } = [];
        public List<WindowsEventQuery> Queries { get; } = [];
        public long? NewestOverride { get; set; }
        public Exception? Throw { get; set; }

        public IReadOnlyList<WindowsEventRecord> Read(WindowsEventQuery query)
        {
            Queries.Add(query);
            if (Throw is not null)
            {
                throw Throw;
            }
            var matching = Records
                .Where(r => query.AfterRecordId is not { } after || r.RecordId > after)
                .Where(r => query.Since is not { } since || r.TimeCreated >= since)
                .Where(r => query.MaxWindowsLevel is not { } max || r.Level is null or 0 || r.Level <= max)
                .OrderBy(r => r.RecordId)
                .Take(query.MaxEvents)
                .ToList();
            return matching;
        }

        public long? NewestRecordId(string machine, string channel) =>
            NewestOverride ?? (Records.Count == 0 ? null : Records.Max(r => r.RecordId));
    }

    private static WindowsEventRecord Rec(long id, byte level = 2, string provider = "Service Control Manager",
        string? description = "The Print Spooler service terminated unexpectedly.", int eventId = 7031,
        string channel = "System") => new()
        {
            RecordId = id,
            TimeCreated = new DateTimeOffset(2026, 8, 6, 11, 0, 0, TimeSpan.Zero).AddSeconds(id),
            Channel = channel,
            ProviderName = provider,
            EventId = eventId,
            Level = level,
            Description = description,
            MachineName = "WEB01",
        };

    // ---- Harness -----------------------------------------------------------------------

    private sealed record Harness(
        WindowsEventCollector Collector, IWindowsEventSource Source, WinlogCursorStore Cursors,
        AppStore Apps, EventReader Reader, IngestPipeline Pipeline);

    private Harness NewHarness(WindowsEventOptions options, IWindowsEventSource? source = null)
    {
        var paths = new StoragePaths(_root);
        paths.EnsureRootDirectories();
        var db = new ControlDatabase(paths);
        db.Initialize();

        var apps = new AppStore(db);
        var cursors = new WinlogCursorStore(db);
        var partitions = new PartitionManager(paths);
        var storage = new StorageOptions { DataPath = _root, BatchSize = 1, FlushIntervalMs = 10 };
        var pipeline = new IngestPipeline(partitions, storage, _ => [], _ => { });
        var ingest = new IngestService(pipeline, new IngestLimits(), () => _now);
        var fake = source ?? new FakeSource();

        // Options arrive through a delegate because they are editable in the admin UI; the
        // harness hands over a fixed snapshot, which is what every test here wants.
        var collector = new WindowsEventCollector(
            fake, cursors, apps, ingest, () => options, () => _now,
            NullLogger<WindowsEventCollector>.Instance);

        return new Harness(collector, fake, cursors, apps, new EventReader(partitions), pipeline);
    }

    private static WindowsEventOptions Options(int backfillHours = 0, int maxPerPoll = 500,
        LogLevel minimum = LogLevel.Verbose) => new()
        {
            Enabled = true,
            InitialBackfillHours = backfillHours,
            MaxEventsPerPoll = maxPerPoll,
            Sources =
            [
                new WindowsEventSourceOptions
                {
                    Machine = "WEB01",
                    AppId = "windows",
                    Channels = ["System"],
                    MinimumLevel = minimum,
                },
            ],
        };

    private static async Task<IReadOnlyList<LogEventDto>> Drain(Harness h)
    {
        await h.Pipeline.CompleteAndDrainAsync(TimeSpan.FromSeconds(5));
        return h.Reader.Query(new EventQuery { AppId = "windows", Limit = 100 }).Events;
    }

    // ---- Mapping -----------------------------------------------------------------------

    [Fact]
    public void Maps_windows_level_onto_the_inverted_logrr_scale()
    {
        Assert.Equal(LogLevel.Fatal, WindowsEventMapper.MapLevel(1));       // Critical
        Assert.Equal(LogLevel.Error, WindowsEventMapper.MapLevel(2));
        Assert.Equal(LogLevel.Warning, WindowsEventMapper.MapLevel(3));
        Assert.Equal(LogLevel.Information, WindowsEventMapper.MapLevel(4));
        Assert.Equal(LogLevel.Verbose, WindowsEventMapper.MapLevel(5));
        // LogAlways is a routing directive, not a severity.
        Assert.Equal(LogLevel.Information, WindowsEventMapper.MapLevel(0));
        Assert.Equal(LogLevel.Information, WindowsEventMapper.MapLevel(null));
    }

    [Fact]
    public void Groups_by_channel_provider_and_event_id_not_by_message()
    {
        // The whole point of the template: two occurrences of the same Windows event share an
        // event type even though their descriptions differ, and a different event id does not.
        var a = WindowsEventMapper.ToLogEvent(Rec(1, description: "Spooler died once"), "WEB01");
        var b = WindowsEventMapper.ToLogEvent(Rec(2, description: "Spooler died again"), "WEB01");
        var c = WindowsEventMapper.ToLogEvent(Rec(3, eventId: 7036), "WEB01");

        Assert.Equal("[System/Service Control Manager 7031]", a.Template);
        Assert.Equal(a.EventType, b.EventType);
        Assert.NotEqual(a.EventType, c.EventType);
        Assert.Equal("Spooler died once", a.Message);
    }

    [Fact]
    public void Fills_machine_source_and_properties()
    {
        var ev = WindowsEventMapper.ToLogEvent(Rec(42), "WEB01");

        Assert.Equal("WEB01", ev.Machine);
        Assert.Equal("Service Control Manager", ev.Source);
        Assert.Equal("WEB01", ev.Properties["MachineName"]);
        Assert.Equal(7031L, ev.Properties["EventId"]);
        Assert.Equal(42L, ev.Properties["RecordId"]);
        Assert.Equal("System", ev.Properties["Channel"]);
    }

    [Fact]
    public void Synthesises_a_message_when_the_publisher_metadata_is_missing()
    {
        // Reading a remote box whose software isn't installed locally: FormatDescription() gives
        // nothing, but the event must still be readable rather than blank.
        var record = Rec(1, description: null) with { Data = ["Spooler", "%%1053"] };
        var ev = WindowsEventMapper.ToLogEvent(record, "WEB01");

        Assert.Equal("Service Control Manager event 7031: Spooler | %%1053", ev.Message);
        Assert.Equal("Spooler", ev.Properties["Data0"]);
    }

    // ---- Collection --------------------------------------------------------------------

    [Fact]
    public async Task Collects_events_into_the_app_and_advances_the_cursor()
    {
        var source = new FakeSource { Records = [Rec(1), Rec(2), Rec(3)] };
        var h = NewHarness(Options(), source);

        // A fresh cursor starts at the tail, so seed one first and then let new events arrive.
        h.Cursors.Set("WEB01", "System", 0, _now);
        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(3, h.Cursors.Get("WEB01", "System"));
        var events = await Drain(h);
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal("WEB01", e.Machine));
    }

    [Fact]
    public void Second_poll_ships_nothing_when_no_new_records_arrived()
    {
        var source = new FakeSource { Records = [Rec(1), Rec(2)] };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);
        var afterFirst = source.Queries.Count;
        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(2, h.Cursors.Get("WEB01", "System"));
        // The second poll queried again but had nothing past the cursor to return.
        Assert.True(source.Queries.Count > afterFirst);
        Assert.All(source.Queries.Skip(afterFirst), q => Assert.Equal(2, q.AfterRecordId));
    }

    [Fact]
    public void Starts_at_the_tail_on_first_sight_of_a_channel()
    {
        // An event log holds months of history; importing it wholesale on first start would
        // bury the app and mostly fall foul of the 30-day skew bound anyway.
        var source = new FakeSource { Records = [Rec(1), Rec(2), Rec(3)] };
        var h = NewHarness(Options(), source);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(3, h.Cursors.Get("WEB01", "System"));
        Assert.DoesNotContain(source.Queries, q => q.Since is not null);
    }

    [Fact]
    public async Task Backfills_a_window_when_configured_to()
    {
        var source = new FakeSource { Records = [Rec(1), Rec(2)] };
        var h = NewHarness(Options(backfillHours: 6), source);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Contains(source.Queries, q => q.Since == _now.AddHours(-6));
        Assert.Equal(2, h.Cursors.Get("WEB01", "System"));
        Assert.Equal(2, (await Drain(h)).Count);
    }

    [Fact]
    public void Recovers_when_the_log_has_been_cleared()
    {
        // Clearing a log restarts EventRecordID at 1, so every new record sits below the cursor
        // and would be skipped forever if the cursor were not reset.
        var source = new FakeSource { Records = [] };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 5000, _now);

        source.NewestOverride = 12; // log cleared and refilled
        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(0, h.Cursors.Get("WEB01", "System"));
    }

    [Fact]
    public void Leaves_the_cursor_alone_when_a_quiet_channel_simply_has_nothing_new()
    {
        var source = new FakeSource { Records = [Rec(1), Rec(2)] };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 2, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(2, h.Cursors.Get("WEB01", "System"));
    }

    [Fact]
    public void Chases_a_backlog_across_batches_within_one_poll()
    {
        var source = new FakeSource { Records = Enumerable.Range(1, 25).Select(i => Rec(i)).ToList() };
        var h = NewHarness(Options(maxPerPoll: 10), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Equal(25, h.Cursors.Get("WEB01", "System"));
        Assert.Equal(3, source.Queries.Count); // 10, 10, 5 — stops on the short batch
    }

    [Fact]
    public void Narrows_the_query_to_the_apps_minimum_level()
    {
        // The floor is pushed into the event log query, so filtered-out records are never read
        // off the wire rather than being fetched and discarded.
        var source = new FakeSource { Records = [Rec(1)] };
        var h = NewHarness(Options(minimum: LogLevel.Error), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.NotEmpty(source.Queries);
        Assert.All(source.Queries, q => Assert.Equal(2, q.MaxWindowsLevel));
    }

    [Fact]
    public void Reads_everything_when_the_app_floor_is_verbose()
    {
        var source = new FakeSource { Records = [Rec(1)] };
        var h = NewHarness(Options(minimum: LogLevel.Verbose), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.NotEmpty(source.Queries);
        Assert.All(source.Queries, q => Assert.Null(q.MaxWindowsLevel));
    }

    // ---- Resilience --------------------------------------------------------------------

    [Fact]
    public void An_unreachable_machine_does_not_throw_or_move_the_cursor()
    {
        var source = new FakeSource { Throw = new InvalidOperationException("RPC server unavailable") };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 7, _now);

        h.Collector.CollectOnce(CancellationToken.None); // must not throw

        Assert.Equal(7, h.Cursors.Get("WEB01", "System"));
    }

    [Fact]
    public void Creates_the_target_app_on_first_use()
    {
        var h = NewHarness(Options(minimum: LogLevel.Warning), new FakeSource());

        h.Collector.CollectOnce(CancellationToken.None);

        var app = h.Apps.Get("windows");
        Assert.NotNull(app);
        Assert.Equal(LogLevel.Warning, app!.MinimumLevel);
    }

    [Fact]
    public void Skips_a_source_whose_app_id_is_not_a_valid_slug()
    {
        var options = new WindowsEventOptions
        {
            Enabled = true,
            Sources = [new WindowsEventSourceOptions { Machine = "WEB01", AppId = "Not A Slug!" }],
        };
        var source = new FakeSource { Records = [Rec(1)] };
        var h = NewHarness(options, source);

        h.Collector.CollectOnce(CancellationToken.None); // must not throw

        Assert.Empty(h.Apps.List());
        Assert.Empty(source.Queries);
    }

    [Fact]
    public void Skips_a_disabled_app_without_reading_anything()
    {
        var source = new FakeSource { Records = [Rec(1)] };
        var h = NewHarness(Options(), source);
        h.Apps.Create(new AppRecord
        {
            Id = "windows",
            Name = "windows",
            IsEnabled = false,
            CreatedUtc = _now,
        });

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Empty(source.Queries);
    }

    // ---- Channel resolution ------------------------------------------------------------

    [Fact]
    public void Configured_channels_replace_the_defaults_rather_than_adding_to_them()
    {
        // The configuration binder appends to a collection property's existing contents, so a
        // non-empty default on Channels would leave a configured source collecting the defaults
        // too — every channel polled twice and listed twice on the status page.
        var src = new WindowsEventSourceOptions { Channels = ["Application", "System"] };

        Assert.Equal(["Application", "System"], src.EffectiveChannels);
    }

    [Fact]
    public void An_unset_channel_list_falls_back_to_the_default_pair()
    {
        Assert.Equal(["Application", "System"], new WindowsEventSourceOptions().EffectiveChannels);
    }

    [Fact]
    public void Duplicate_and_blank_channels_are_collapsed()
    {
        var src = new WindowsEventSourceOptions { Channels = ["System", " system ", "", "Application"] };

        Assert.Equal(["System", "Application"], src.EffectiveChannels);
    }

    [Fact]
    public void Each_channel_is_polled_and_listed_exactly_once()
    {
        var options = new WindowsEventOptions
        {
            Enabled = true,
            Sources =
            [
                new WindowsEventSourceOptions
                {
                    Machine = "WEB01", AppId = "windows", Channels = ["System", "System"],
                },
            ],
        };
        var source = new FakeSource { Records = [Rec(1)] };
        var h = NewHarness(options, source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        Assert.Single(h.Collector.Status());
        // One read that returned the record, one that came back short and ended the loop.
        Assert.All(source.Queries, q => Assert.Equal("System", q.Channel));
        Assert.Single(source.Queries);
    }

    // ---- Status (admin page) -----------------------------------------------------------

    [Fact]
    public void Reports_a_row_for_every_configured_target_before_anything_is_polled()
    {
        // A machine that has never been reachable must still appear — that is the row you need
        // to see, and building status from config rather than from cursors is what guarantees it.
        var h = NewHarness(Options(), new FakeSource());

        var status = h.Collector.Status();

        var row = Assert.Single(status);
        Assert.Equal("WEB01", row.Machine);
        Assert.Equal("System", row.Channel);
        Assert.Equal("windows", row.AppId);
        Assert.True(row.IsPending);
        Assert.False(row.IsFailing);
        Assert.Null(row.LastRecordId);
    }

    [Fact]
    public void Status_reports_cursor_counts_and_recovery()
    {
        var source = new FakeSource { Records = [Rec(1), Rec(2), Rec(3)] };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        h.Collector.CollectOnce(CancellationToken.None);

        var ok = Assert.Single(h.Collector.Status());
        Assert.False(ok.IsFailing);
        Assert.False(ok.IsPending);
        Assert.Equal(3, ok.LastRecordId);
        Assert.Equal(3, ok.EventsCollected);
        Assert.NotNull(ok.LastSuccessUtc);
        Assert.NotNull(h.Collector.LastPollUtc);

        // Machine goes away…
        source.Throw = new InvalidOperationException("RPC server unavailable");
        h.Collector.CollectOnce(CancellationToken.None);
        var bad = Assert.Single(h.Collector.Status());
        Assert.True(bad.IsFailing);
        Assert.Equal(1, bad.ConsecutiveFailures);
        Assert.Equal("RPC server unavailable", bad.LastError);
        Assert.Equal(3, bad.LastRecordId); // cursor held

        // …and comes back.
        source.Throw = null;
        h.Collector.CollectOnce(CancellationToken.None);
        var recovered = Assert.Single(h.Collector.Status());
        Assert.False(recovered.IsFailing);
        Assert.Equal(0, recovered.ConsecutiveFailures);
        Assert.Null(recovered.LastError);
    }

    [Fact]
    public void Status_resolves_the_local_machine_alias_for_display()
    {
        var options = new WindowsEventOptions
        {
            Enabled = true,
            Sources = [new WindowsEventSourceOptions { Machine = ".", AppId = "windows", Channels = ["System"] }],
        };
        var h = NewHarness(options, new FakeSource());

        var row = Assert.Single(h.Collector.Status());

        Assert.Equal(Environment.MachineName, row.Machine);
        Assert.Equal(".", row.ConfiguredMachine); // still findable in the config file
    }

    [Fact]
    public async Task A_concurrent_poll_is_skipped_rather_than_double_reading_the_cursor()
    {
        // "Collect now" in the admin UI and the background timer can fire together. Two passes
        // at once would read the same cursor and ship every event between them twice.
        var gate = new ManualResetEventSlim(false);
        var entered = new ManualResetEventSlim(false);
        var source = new BlockingSource(gate, entered) { Records = [Rec(1)] };
        var h = NewHarness(Options(), source);
        h.Cursors.Set("WEB01", "System", 0, _now);

        var first = Task.Run(() => h.Collector.CollectOnce(CancellationToken.None));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));

        h.Collector.CollectOnce(CancellationToken.None); // must return immediately, doing nothing
        Assert.True(h.Collector.IsCollecting);

        gate.Set();
        await first;

        Assert.Equal(1, source.ReadCount); // the second call did not read
        Assert.Equal(1, h.Cursors.Get("WEB01", "System"));
    }

    /// <summary>A source that parks inside the first read so a second poll overlaps it.</summary>
    private sealed class BlockingSource(ManualResetEventSlim release, ManualResetEventSlim entered)
        : IWindowsEventSource
    {
        public List<WindowsEventRecord> Records { get; init; } = [];
        public int ReadCount;

        public IReadOnlyList<WindowsEventRecord> Read(WindowsEventQuery query)
        {
            if (Interlocked.Increment(ref ReadCount) == 1)
            {
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                return Records;
            }
            return [];
        }

        public long? NewestRecordId(string machine, string channel) =>
            Records.Count == 0 ? null : Records.Max(r => r.RecordId);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Temp dir cleanup is best-effort.
        }
    }
}
