using Logrr.Contracts;
using Logrr.Core;
using Logrr.Notify;
using Logrr.Storage;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;
using Xunit;

namespace Logrr.Tests;

/// <summary>
/// The SQL Server backend end to end against a real server (SPEC §4.7): schema creation,
/// ingest, query, aggregation, retention and the control stores. Skipped when no server is
/// reachable — see <see cref="SqlServerTestDatabase"/>.
/// </summary>
public class SqlServerBackendTests : IDisposable
{
    private readonly SqlServerTestDatabase? _db;
    private readonly SqlDialect? _dialect;
    private readonly DateTimeOffset _t0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);
    private readonly DateOnly _day = new(2026, 7, 23);

    public SqlServerBackendTests()
    {
        if (!SqlServerTestDatabase.IsAvailable)
        {
            return;
        }
        _db = new SqlServerTestDatabase();
        _dialect = new SqlServerDialect(_db.ConnectionString, "logrr");
    }

    private SqlDialect Dialect => _dialect!;

    private static LogEvent Ev(DateTimeOffset ts, LogLevel level, string message,
        string? exception = null, string? source = null, params (string, object?)[] props) => new()
    {
        Timestamp = ts,
        Level = level,
        Message = message,
        Exception = exception,
        Source = source,
        EventType = EventTypeHash.Compute(null, message),
        Properties = props.ToDictionary(p => p.Item1, p => p.Item2),
    };

    private (PartitionManager Pm, EventReader Reader) NewStore()
    {
        var pm = new PartitionManager(Dialect);
        return (pm, new EventReader(pm));
    }

    // ---- Partitions and queries -------------------------------------------------------

    [SqlServerFact]
    public void Writes_and_reads_back_events()
    {
        var (pm, reader) = NewStore();
        pm.GetWriter("billing", _day, []).InsertBatch(
        [
            Ev(_t0, LogLevel.Information, "user login ok", props: ("UserId", 1L)),
            Ev(_t0.AddSeconds(1), LogLevel.Error, "payment failed", "System.TimeoutException",
                props: ("UserId", 1042L)),
            Ev(_t0.AddSeconds(2), LogLevel.Warning, "cache miss timeout"),
        ]);

        var page = reader.Query(new EventQuery { AppId = "billing", Limit = 100 });

        Assert.Equal(3, page.Events.Count);
        Assert.Equal("cache miss timeout", page.Events[0].Message); // newest first
        Assert.Equal(LogLevel.Error, page.Events[1].Level);
        Assert.Equal("System.TimeoutException", page.Events[1].Exception);
        Assert.Equal(1L, page.Events[2].Properties!.Value.GetProperty("UserId").GetInt64());
        pm.Dispose();
    }

    [SqlServerFact]
    public void Filters_and_text_search_apply()
    {
        var (pm, reader) = NewStore();
        pm.GetWriter("billing", _day, ["UserId"]).InsertBatch(
        [
            Ev(_t0, LogLevel.Information, "user login ok", props: ("UserId", 1L)),
            Ev(_t0.AddSeconds(1), LogLevel.Error, "payment timeout", "boom", props: ("UserId", 1042L)),
            Ev(_t0.AddSeconds(2), LogLevel.Error, "payment ok", props: ("UserId", 7L)),
        ]);

        var filtered = reader.Query(new EventQuery
        {
            AppId = "billing",
            Filter = "Level >= Error and UserId = 1042",
        });
        Assert.Single(filtered.Events);
        Assert.Equal("payment timeout", filtered.Events[0].Message);

        var searched = reader.Query(new EventQuery { AppId = "billing", Text = "timeout" });
        Assert.Single(searched.Events);
        Assert.Equal("payment timeout", searched.Events[0].Message);

        // Every term must appear, as in a bare FTS5 query.
        Assert.Empty(reader.Query(new EventQuery { AppId = "billing", Text = "timeout nonesuch" }).Events);
        pm.Dispose();
    }

    /// <summary>
    /// Ids come out of the writer, not an identity column, so paging behaves exactly as it does
    /// on SQLite: contiguous, newest-first, no dupes and no gaps across pages.
    /// </summary>
    [SqlServerFact]
    public void Cursor_paging_is_stable_and_complete()
    {
        var (pm, reader) = NewStore();
        pm.GetWriter("billing", _day, []).InsertBatch(
            Enumerable.Range(0, 25)
                .Select(i => Ev(_t0.AddSeconds(i), LogLevel.Information, $"event {i}"))
                .ToList());

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = reader.Query(new EventQuery { AppId = "billing", Limit = 10, Cursor = cursor });
            seen.AddRange(page.Events.Select(e => e.Message));
            cursor = page.NextCursor;
            pages++;
        } while (cursor is not null && pages < 10);

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
        pm.Dispose();
    }

    /// <summary>Batches larger than one multi-row INSERT still get contiguous ids.</summary>
    [SqlServerFact]
    public void A_batch_larger_than_one_insert_statement_commits_whole()
    {
        var (pm, reader) = NewStore();
        var batch = Enumerable.Range(0, 500)
            .Select(i => Ev(_t0.AddSeconds(i), LogLevel.Information, $"event {i}"))
            .ToList();

        var rows = pm.GetWriter("billing", _day, []).InsertBatch(batch);

        Assert.Equal(500, rows.Count);
        Assert.Equal(Enumerable.Range(1, 500).Select(i => (long)i), rows.Select(r => r.Rowid));
        Assert.Equal(500, reader.Query(new EventQuery { AppId = "billing", Limit = 1000 }).Events.Count);
        pm.Dispose();
    }

    [SqlServerFact]
    public void Get_by_id_round_trips_the_composite_event_id()
    {
        var (pm, reader) = NewStore();
        pm.GetWriter("billing", _day, []).InsertBatch([Ev(_t0, LogLevel.Error, "boom")]);

        var listed = reader.Query(new EventQuery { AppId = "billing" }).Events.Single();
        var fetched = reader.GetById("billing", listed.Id);

        Assert.NotNull(fetched);
        Assert.Equal("boom", fetched.Message);
        Assert.Equal(listed.Timestamp, fetched.Timestamp);
        pm.Dispose();
    }

    [SqlServerFact]
    public void Histogram_and_facets_match_the_results_table()
    {
        var (pm, reader) = NewStore();
        pm.GetWriter("billing", _day, []).InsertBatch(
        [
            Ev(_t0.AddMinutes(1), LogLevel.Information, "a", source: "Api"),
            Ev(_t0.AddMinutes(2), LogLevel.Information, "b", source: "Api"),
            Ev(_t0.AddMinutes(3), LogLevel.Error, "c", source: "Worker"),
            Ev(_t0.AddMinutes(50), LogLevel.Warning, "d", source: "Api"),
        ]);

        var hist = reader.Histogram(
            new EventQuery { AppId = "billing", From = _t0, To = _t0.AddHours(1) }, buckets: 60);
        Assert.Equal(4, hist.Total);
        Assert.Equal(1, hist.Buckets[1].Counts[LogLevel.Information]);
        Assert.Equal(1, hist.Buckets[3].Counts[LogLevel.Error]);
        Assert.Equal(1, hist.Buckets[50].Counts[LogLevel.Warning]);

        var filtered = reader.Histogram(
            new EventQuery { AppId = "billing", From = _t0, To = _t0.AddHours(1), MinLevel = LogLevel.Warning },
            buckets: 60);
        Assert.Equal(2, filtered.Total);

        var facets = reader.Facets(new EventQuery { AppId = "billing" });
        var levels = facets.Levels.ToDictionary(l => l.Level, l => l.Count);
        Assert.Equal(2, levels[LogLevel.Information]);
        Assert.Equal("Api", facets.Sources[0].Value);
        Assert.Equal(3, facets.Sources[0].Count);
        pm.Dispose();
    }

    /// <summary>Exercises the OPENJSON path in the unacknowledged-error count.</summary>
    [SqlServerFact]
    public void Stats_report_size_and_honour_acknowledgements()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();
        var acks = new AckStore(control);

        var (pm, _) = NewStore();
        var boom = Ev(_t0, LogLevel.Error, "boom");
        var bang = Ev(_t0.AddSeconds(1), LogLevel.Error, "bang");
        pm.GetWriter("billing", _day, []).InsertBatch([boom, bang]);

        var stats = new StatsReader(pm, acks);
        var before = stats.GetStats("billing", _t0.AddHours(1));
        Assert.Equal(2, before.TotalEvents);
        Assert.Equal(2, before.UnacknowledgedErrors);
        Assert.True(before.StorageBytes > 0);

        acks.Acknowledge(new Ack("billing", Ack.ScopeFor(boom.EventType),
            EventPartition.UnixMicros(_t0), "handled", "tester", _t0));

        var after = stats.GetStats("billing", _t0.AddHours(1));
        Assert.Equal(1, after.UnacknowledgedErrors);
        pm.Dispose();
    }

    /// <summary>Retention drops the whole partition table rather than deleting rows.</summary>
    [SqlServerFact]
    public void Retention_drops_partitions_past_the_age_limit()
    {
        var (pm, _) = NewStore();
        var old = _day.AddDays(-30);
        pm.GetWriter("billing", old, []).InsertBatch([Ev(_t0.AddDays(-30), LogLevel.Information, "old")]);
        pm.GetWriter("billing", _day, []).InsertBatch([Ev(_t0, LogLevel.Information, "new")]);
        Assert.Equal(2, Dialect.PartitionDays("billing").Count);

        var maintenance = new RetentionMaintenance(pm, new StorageOptions());
        var result = maintenance.Run(
            [new AppRecord { Id = "billing", Name = "Billing", RetentionDays = 14, CreatedUtc = _t0 }],
            _day);

        Assert.Equal(1, result.PartitionsDeleted);
        Assert.False(result.DiskGuardTripped); // the local-disk guard does not apply to SQL Server
        Assert.Equal([_day], Dialect.PartitionDays("billing"));
        pm.Dispose();
    }

    // ---- Control stores ----------------------------------------------------------------

    [SqlServerFact]
    public void Migrations_run_to_the_current_version()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();
        control.Initialize(); // idempotent

        using var conn = control.Open();
        Assert.Equal(Dialect.ControlMigrations.Count, Dialect.GetSchemaVersion(conn));
        Assert.True(control.Exists());
    }

    [SqlServerFact]
    public void Users_round_trip_and_windows_accounts_match_case_insensitively()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();
        var users = new UserStore(control);

        Assert.False(users.AnyExist());
        users.Create(new UserRecord
        {
            Id = "u1",
            Username = "admin",
            PasswordHash = [1, 2, 3],
            PasswordSalt = [4, 5, 6],
            WindowsAccount = @"CONTOSO\jrhoades",
            Role = UserRole.Admin,
            MustChangePassword = true,
            AppAccess = ["billing"],
            CreatedUtc = _t0,
        });

        Assert.True(users.AnyExist());
        var loaded = users.GetByUsername("admin")!;
        Assert.Equal(UserRole.Admin, loaded.Role);
        Assert.True(loaded.MustChangePassword);
        Assert.Equal([1, 2, 3], loaded.PasswordHash);
        Assert.Equal(["billing"], loaded.AppAccess);
        Assert.NotNull(users.GetByWindowsAccount(@"contoso\JRHOADES"));

        users.UpdatePassword("u1", [9], [8], mustChange: false);
        Assert.False(users.GetByUsername("admin")!.MustChangePassword);
    }

    /// <summary>The upsert must move the watermark forward only — never backwards.</summary>
    [SqlServerFact]
    public void Acknowledging_twice_keeps_the_later_watermark()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();
        var acks = new AckStore(control);

        acks.Acknowledge(new Ack("billing", "*", 2000, "later", "tester", _t0));
        acks.Acknowledge(new Ack("billing", "*", 1000, "earlier", "tester", _t0));

        Assert.Equal(2000, acks.Find("billing", "*")!.ThroughTs);
        Assert.Equal(2000, acks.Snapshot("billing").AppWideThroughTs);

        acks.ClearAll("billing");
        Assert.True(acks.Snapshot("billing").IsEmpty);
    }

    [SqlServerFact]
    public void Adding_a_cors_origin_is_idempotent()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();
        var origins = new CorsOriginStore(control);

        Assert.True(origins.Add("https://app.internal", _t0));
        Assert.False(origins.Add("https://app.internal", _t0));
        Assert.Single(origins.List());

        origins.Remove("https://app.internal");
        Assert.Empty(origins.List());
    }

    [SqlServerFact]
    public void Apps_tokens_and_saved_searches_round_trip()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();

        var apps = new AppStore(control);
        apps.Create(new AppRecord
        {
            Id = "billing", Name = "Billing", RetentionDays = 7, MaxSizeMb = 512,
            MinimumLevel = LogLevel.Debug, IndexedProperties = ["UserId"], IsEnabled = true,
            CreatedUtc = _t0,
        });
        var app = apps.Get("billing")!;
        Assert.Equal(["UserId"], app.IndexedProperties);
        Assert.True(app.IsEnabled);

        apps.Update(app with { IsEnabled = false, RetentionDays = 30 });
        Assert.False(apps.Get("billing")!.IsEnabled);
        Assert.Equal(30, apps.Get("billing")!.RetentionDays);

        var tokens = new TokenStore(control);
        tokens.Create(new TokenRecord
        {
            Id = "t1", AppId = "billing", Name = "ci", Prefix = "lg_bill",
            Hash = [1, 2, 3], Scopes = TokenScopes.Ingest, CreatedUtc = _t0,
        });
        var token = tokens.FindByPrefix("lg_bill")!;
        Assert.Equal("billing", token.AppId);
        Assert.Null(token.RevokedUtc);
        tokens.Revoke("t1", _t0);
        Assert.NotNull(tokens.FindByPrefix("lg_bill")!.RevokedUtc);

        var searches = new SavedSearchStore(control);
        searches.Create(new SavedSearch("s1", "billing", "errors", "Level >= Error", "tester", _t0));
        Assert.Equal("Level >= Error", searches.ListByApp("billing").Single().Query);
    }

    [SqlServerFact]
    public void Delivery_and_occurrence_upserts_replace_rather_than_duplicate()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();

        var destinations = new DestinationStore(control);
        destinations.Create(new Destination
        {
            Id = "d1", Name = "Ops", Url = "https://hooks.internal/x",
            BodyTemplate = "{}", IsEnabled = true, CreatedUtc = _t0,
        });
        Assert.Equal("Ops", destinations.Get("d1")!.Name);

        var deliveries = new DeliveryStore(control);
        var delivery = new Delivery
        {
            Id = "dl1", DestinationId = "d1", AppId = "billing", Source = DeliverySource.Rule,
            CreatedUtc = _t0, Status = DeliveryStatus.Pending, RequestBody = "{}",
        };
        deliveries.Enqueue(delivery);
        deliveries.Update(delivery with { Attempt = 1, Status = DeliveryStatus.Failed, Error = "503" });

        var stored = deliveries.Get("dl1")!;
        Assert.Equal(1, stored.Attempt);
        Assert.Equal(DeliveryStatus.Failed, stored.Status);
        Assert.Equal("503", stored.Error);
        Assert.Equal(1, deliveries.CountSince(_t0.AddMinutes(-1)));
        Assert.Single(deliveries.Query(DeliveryStatus.Failed, null, null, null));

        var occurrences = new OccurrenceStore(control);
        var occurrence = new Occurrence
        {
            RuleId = "r1", DedupeKey = "k1", WindowStartUtc = _t0, Count = 1,
            FirstSeenUtc = _t0, LastSeenUtc = _t0,
        };
        occurrences.Upsert(occurrence);
        occurrences.Upsert(occurrence with { Count = 5, LastSeenUtc = _t0.AddMinutes(1) });
        Assert.Equal(5, occurrences.Get("r1", "k1")!.Count);
        Assert.Equal(1, occurrences.ResetForRule("r1"));
    }

    /// <summary>
    /// Rules carry three <c>BIT</c> columns that every store binds as the integers 0 and 1, and
    /// <c>EnabledForApp</c> filters on <c>is_enabled = 1</c> — the place where a SQLite-shaped
    /// boolean would quietly stop matching on SQL Server.
    /// </summary>
    [SqlServerFact]
    public void Rules_round_trip_their_boolean_columns()
    {
        var control = new ControlDatabase(Dialect);
        control.Initialize();

        new DestinationStore(control).Create(new Destination
        {
            Id = "d1", Name = "Ops", Url = "https://hooks.internal/x",
            BodyTemplate = "{}", IsEnabled = true, CreatedUtc = _t0,
        });

        var rules = new RuleStore(control);
        var rule = new Rule
        {
            Id = "r1", Name = "Errors", AppId = "billing", Filter = "Level >= Error",
            MinimumLevel = LogLevel.Error, TriggerType = TriggerType.EveryMatch,
            DedupeKeyTemplate = "{{event.eventType}}", CooldownMinutes = 60,
            DestinationId = "d1", MaxFiresPerHour = 20,
            IsDryRun = true, IsEnabled = true, CreatedUtc = _t0,
        };
        rules.Create(rule);

        var loaded = rules.Get("r1")!;
        Assert.True(loaded.IsDryRun);
        Assert.True(loaded.IsEnabled);
        Assert.Equal("Level >= Error", loaded.Filter);
        Assert.Single(rules.EnabledForApp("billing"));

        // An all-apps rule applies to every app; a disabled one applies to none.
        rules.Update(rule with { AppId = null });
        Assert.Single(rules.EnabledForApp("anything-else"));

        rules.AutoDisable("r1", "blew the hourly cap");
        Assert.Empty(rules.EnabledForApp("billing"));
        Assert.Equal("blew the hourly cap", rules.Get("r1")!.AutoDisabledReason);

        rules.SetEnabled("r1", true);
        Assert.Null(rules.Get("r1")!.AutoDisabledReason);
        Assert.Single(rules.List());
    }

    public void Dispose() => _db?.Dispose();
}
