using Logrr.Contracts;
using Logrr.Core;
using Logrr.Storage;
using Logrr.Storage.Control;
using Xunit;

namespace Logrr.Tests;

public class AckTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-ack-" + Guid.NewGuid().ToString("N"));
    private readonly ControlDatabase _db;
    private readonly AckStore _acks;
    private readonly PartitionManager _pm;
    private readonly StatsReader _stats;
    private readonly DateTimeOffset _t0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public AckTests()
    {
        var paths = new StoragePaths(_root);
        _db = new ControlDatabase(paths);
        _db.Initialize();
        _acks = new AckStore(_db);
        _pm = new PartitionManager(paths);
        _stats = new StatsReader(_pm, _acks);
    }

    private static LogEvent Ev(DateTimeOffset ts, LogLevel level, string message) => new()
    {
        Timestamp = ts, Level = level, Message = message,
        EventType = EventTypeHash.Compute(null, message),
        Properties = new Dictionary<string, object?>(),
    };

    private void Write(params LogEvent[] events) =>
        _pm.GetWriter("billing", DateOnly.FromDateTime(_t0.UtcDateTime), []).InsertBatch(events);

    private long Unacked() => _stats.GetStats("billing", _t0.AddHours(2)).UnacknowledgedErrors;

    private void Acknowledge(string scope, DateTimeOffset through) =>
        _acks.Acknowledge(new Ack("billing", scope, EventPartition.UnixMicros(through), null, "admin", through));

    [Fact]
    public void Errors_are_unacknowledged_until_an_ack_covers_them()
    {
        Write(Ev(_t0, LogLevel.Information, "fine"),
              Ev(_t0.AddMinutes(1), LogLevel.Error, "boom"),
              Ev(_t0.AddMinutes(2), LogLevel.Fatal, "worse"));

        Assert.Equal(2, Unacked()); // Error + Fatal, no acks yet

        Acknowledge(Ack.AllScope, _t0.AddMinutes(5));
        Assert.Equal(0, Unacked());
    }

    [Fact]
    public void A_recurrence_after_the_ack_alerts_again()
    {
        Write(Ev(_t0.AddMinutes(1), LogLevel.Error, "boom"));
        Acknowledge(Ack.AllScope, _t0.AddMinutes(5));
        Assert.Equal(0, Unacked());

        Write(Ev(_t0.AddMinutes(10), LogLevel.Error, "boom"));
        Assert.Equal(1, Unacked());
    }

    [Fact]
    public void An_event_type_ack_leaves_other_error_types_alerting()
    {
        Write(Ev(_t0.AddMinutes(1), LogLevel.Error, "boom"),
              Ev(_t0.AddMinutes(2), LogLevel.Error, "other"));

        Acknowledge(Ack.ScopeFor(EventTypeHash.Compute(null, "boom")), _t0.AddMinutes(5));

        Assert.Equal(1, Unacked()); // only "other" still alerts
    }

    [Fact]
    public void Clearing_an_ack_brings_its_errors_back()
    {
        Write(Ev(_t0.AddMinutes(1), LogLevel.Error, "boom"));
        var scope = Ack.ScopeFor(EventTypeHash.Compute(null, "boom"));
        Acknowledge(scope, _t0.AddMinutes(5));
        Assert.Equal(0, Unacked());

        _acks.Clear("billing", scope);
        Assert.Equal(1, Unacked());
    }

    [Fact]
    public void Re_acknowledging_only_moves_the_watermark_forward()
    {
        Write(Ev(_t0.AddMinutes(1), LogLevel.Error, "boom"));
        Acknowledge(Ack.AllScope, _t0.AddMinutes(5));
        Acknowledge(Ack.AllScope, _t0.AddMinutes(2)); // stale request, earlier watermark

        Assert.Equal(EventPartition.UnixMicros(_t0.AddMinutes(5)), _acks.Find("billing", Ack.AllScope)!.ThroughTs);
        Assert.Equal(0, Unacked());
    }

    [Fact]
    public void Snapshot_reports_app_wide_and_per_type_coverage()
    {
        var type = EventTypeHash.Compute(null, "boom");
        Acknowledge(Ack.ScopeFor(type), _t0.AddMinutes(5));

        var snapshot = _acks.Snapshot("billing");
        Assert.False(snapshot.IsEmpty);
        Assert.Equal(0, snapshot.AppWideThroughTs);
        Assert.True(snapshot.Covers(type, EventPartition.UnixMicros(_t0)));
        Assert.False(snapshot.Covers(type, EventPartition.UnixMicros(_t0.AddMinutes(10))));
        Assert.False(snapshot.Covers(eventType: null, EventPartition.UnixMicros(_t0)));
        Assert.True(AckSnapshot.Empty.IsEmpty);
    }

    public void Dispose()
    {
        _pm.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }
}
