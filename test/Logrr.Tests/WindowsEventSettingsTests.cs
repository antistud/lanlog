using Logrr.Server.WindowsEvents;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using LogLevel = Logrr.Contracts.LogLevel;

namespace Logrr.Tests;

/// <summary>
/// Windows event collection as editable state (SPEC §6.4): the machines and knobs live in the
/// control DB and change while the server runs, with <c>Logrr:WindowsEvents</c> surviving only
/// as a first-run seed. These tests are the "no restart" promise.
/// </summary>
public class WindowsEventSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "logrr-winset-" + Guid.NewGuid().ToString("N"));
    private readonly ControlDatabase _db;
    private readonly WinlogConfigStore _store;
    private readonly WinlogCursorStore _cursors;
    private readonly DateTimeOffset _now = new(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);

    public WindowsEventSettingsTests()
    {
        _db = new ControlDatabase(new StoragePaths(_root));
        _db.Initialize();
        _store = new WinlogConfigStore(_db);
        _cursors = new WinlogCursorStore(_db);
    }

    private WindowsEventSettings NewSettings(params (string Key, string Value)[] config)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(config.Select(c => new KeyValuePair<string, string?>(c.Key, c.Value)))
            .Build();
        return new WindowsEventSettings(_store, _cursors, configuration, () => _now,
            NullLogger<WindowsEventSettings>.Instance);
    }

    private static WinlogSource Source(string machine, string appId, params string[] channels) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Machine = machine,
        AppId = appId,
        Channels = channels,
        IsEnabled = true,
    };

    // ---- Seeding from configuration ----------------------------------------------------

    [Fact]
    public void Imports_the_configuration_section_on_the_first_run_that_finds_no_settings()
    {
        // An install that was collecting before the feature moved into the DB must keep
        // collecting exactly the same machines across the upgrade, without being touched.
        var settings = NewSettings(
            ("Logrr:WindowsEvents:Enabled", "true"),
            ("Logrr:WindowsEvents:PollIntervalSeconds", "30"),
            ("Logrr:WindowsEvents:Sources:0:Machine", "WEB01"),
            ("Logrr:WindowsEvents:Sources:0:AppId", "windows-web01"),
            ("Logrr:WindowsEvents:Sources:0:Channels:0", "System"),
            ("Logrr:WindowsEvents:Sources:0:MinimumLevel", "Warning"));

        settings.Initialize();

        Assert.True(settings.Current.Enabled);
        Assert.Equal(30, settings.Current.PollIntervalSeconds);
        var source = Assert.Single(settings.Current.Sources);
        Assert.Equal("WEB01", source.Machine);
        Assert.Equal("windows-web01", source.AppId);
        Assert.Equal(["System"], source.EffectiveChannels);
        Assert.Equal(LogLevel.Warning, source.MinimumLevel);
    }

    [Fact]
    public void The_configuration_section_is_ignored_once_the_tables_have_been_seeded()
    {
        // Two homes for one setting is the split the spec warns against: after the import the DB
        // is the only source of truth, so a machine removed in the UI must stay removed.
        var config = new (string, string)[]
        {
            ("Logrr:WindowsEvents:Enabled", "true"),
            ("Logrr:WindowsEvents:Sources:0:Machine", "WEB01"),
            ("Logrr:WindowsEvents:Sources:0:AppId", "windows-web01"),
        };
        var first = NewSettings(config);
        first.Initialize();
        first.DeleteSource(_store.ListSources()[0].Id);

        var restarted = NewSettings(config);
        restarted.Initialize();

        Assert.Empty(restarted.Current.Sources);
    }

    [Fact]
    public void A_configured_source_with_an_unusable_app_id_is_skipped_rather_than_imported()
    {
        var settings = NewSettings(
            ("Logrr:WindowsEvents:Enabled", "true"),
            ("Logrr:WindowsEvents:Sources:0:Machine", "WEB01"),
            ("Logrr:WindowsEvents:Sources:0:AppId", "Not A Slug!"),
            ("Logrr:WindowsEvents:Sources:1:Machine", "WEB02"),
            ("Logrr:WindowsEvents:Sources:1:AppId", "windows-web02"));

        settings.Initialize();

        var kept = Assert.Single(settings.Current.Sources);
        Assert.Equal("WEB02", kept.Machine);
    }

    [Fact]
    public void An_absent_configuration_section_seeds_collection_switched_off()
    {
        var settings = NewSettings();

        settings.Initialize();

        Assert.False(settings.Current.Enabled);
        Assert.Empty(settings.Current.Sources);
        Assert.NotNull(_store.GetSettings()); // seeded, so the next start does not re-import
    }

    // ---- Editing -----------------------------------------------------------------------

    [Fact]
    public void Adding_a_machine_shows_up_in_the_snapshot_without_a_restart()
    {
        var settings = NewSettings();
        settings.Initialize();

        Assert.Null(settings.SaveSource(Source("WEB01", "windows-web01", "System"), isNew: true));

        var source = Assert.Single(settings.Current.Sources);
        Assert.Equal("WEB01", source.Machine);
    }

    [Fact]
    public void A_paused_machine_stays_in_the_list_but_leaves_the_collector_view()
    {
        var settings = NewSettings();
        settings.Initialize();
        settings.SaveSource(Source("WEB01", "windows-web01") with { IsEnabled = false }, isNew: true);

        Assert.Single(settings.Sources());
        Assert.Empty(settings.Current.Sources);
    }

    [Fact]
    public void The_same_machine_cannot_be_collected_twice()
    {
        // Two rows would share the machine's per-channel cursors, so whichever polled second
        // would silently collect nothing at all.
        var settings = NewSettings();
        settings.Initialize();
        settings.SaveSource(Source("WEB01", "windows-web01"), isNew: true);

        var error = settings.SaveSource(Source("web01", "windows-other"), isNew: true);

        Assert.NotNull(error);
        Assert.Single(settings.Current.Sources);
    }

    [Fact]
    public void Editing_a_machine_in_place_is_not_a_duplicate_of_itself()
    {
        var settings = NewSettings();
        settings.Initialize();
        settings.SaveSource(Source("WEB01", "windows-web01"), isNew: true);
        var stored = settings.Sources()[0];

        var error = settings.SaveSource(stored with { AppId = "windows-renamed" }, isNew: false);

        Assert.Null(error);
        Assert.Equal("windows-renamed", settings.Current.Sources[0].AppId);
    }

    [Theory]
    [InlineData("", "windows-web01")]
    [InlineData("CONTOSO\\WEB01", "windows-web01")]
    [InlineData("WEB01", "Not A Slug!")]
    [InlineData("WEB01", "ab")]
    public void A_source_the_collector_would_skip_cannot_be_saved(string machine, string appId)
    {
        var settings = NewSettings();
        settings.Initialize();

        Assert.NotNull(settings.SaveSource(Source(machine, appId), isNew: true));
        Assert.Empty(settings.Current.Sources);
    }

    [Fact]
    public void Removing_a_machine_forgets_its_cursors()
    {
        // Otherwise re-adding it later would resume from a record id set months ago, and every
        // event written in between would be skipped for good.
        var settings = NewSettings();
        settings.Initialize();
        settings.SaveSource(Source("WEB01", "windows-web01"), isNew: true);
        _cursors.Set("WEB01", "System", 4200, _now);

        settings.DeleteSource(settings.Sources()[0].Id);

        Assert.Null(_cursors.Get("WEB01", "System"));
    }

    [Fact]
    public void Renaming_the_machine_of_an_existing_entry_forgets_the_old_cursors()
    {
        var settings = NewSettings();
        settings.Initialize();
        settings.SaveSource(Source("WEB01", "windows-web01"), isNew: true);
        _cursors.Set("WEB01", "System", 4200, _now);

        settings.SaveSource(settings.Sources()[0] with { Machine = "WEB02" }, isNew: false);

        Assert.Null(_cursors.Get("WEB01", "System"));
        Assert.Equal("WEB02", settings.Current.Sources[0].Machine);
    }

    [Fact]
    public void Resetting_one_channel_leaves_the_others_alone()
    {
        var settings = NewSettings();
        settings.Initialize();
        _cursors.Set("WEB01", "System", 10, _now);
        _cursors.Set("WEB01", "Application", 20, _now);

        settings.ResetCursor("WEB01", "System");

        Assert.Null(_cursors.Get("WEB01", "System"));
        Assert.Equal(20, _cursors.Get("WEB01", "Application"));
    }

    [Fact]
    public void Settings_are_clamped_on_the_way_in_so_the_page_shows_what_the_collector_will_do()
    {
        var settings = NewSettings();
        settings.Initialize();

        settings.SaveSettings(new WinlogSettings
        {
            Enabled = true,
            PollIntervalSeconds = 1,
            MaxEventsPerPoll = 0,
            MaxBatchesPerPoll = 0,
            InitialBackfillHours = 5000,
        });

        var stored = settings.Settings();
        Assert.Equal(WindowsEventValidation.MinPollIntervalSeconds, stored.PollIntervalSeconds);
        Assert.Equal(1, stored.MaxEventsPerPoll);
        Assert.Equal(1, stored.MaxBatchesPerPoll);
        Assert.Equal(WindowsEventOptions.MaxInitialBackfillHours, stored.InitialBackfillHours);
        Assert.Equal(stored.PollIntervalSeconds, settings.Current.PollIntervalSeconds);
    }

    [Fact]
    public void A_save_raises_the_change_signal_the_collectors_timer_waits_on()
    {
        // Without this a shortened poll interval - or switching collection on - would not take
        // effect until the sleep that was already in flight expired, up to an hour later.
        var settings = NewSettings();
        settings.Initialize();
        var woken = 0;
        settings.Changed += () => woken++;

        settings.SaveSettings(new WinlogSettings { Enabled = true });
        settings.SaveSource(Source("WEB01", "windows-web01"), isNew: true);

        Assert.Equal(2, woken);
    }

    // ---- Store round trip ---------------------------------------------------------------

    [Fact]
    public void Sources_survive_a_round_trip_through_the_control_db()
    {
        _store.CreateSource(new WinlogSource
        {
            Id = "abc",
            Machine = ".",
            AppId = "windows-logrr",
            AppName = "This server",
            Channels = ["Application", "System"],
            MinimumLevel = LogLevel.Error,
            IsEnabled = false,
            CreatedUtc = _now,
        });

        var loaded = Assert.Single(_store.ListSources());
        Assert.Equal(".", loaded.Machine);
        Assert.Equal("windows-logrr", loaded.AppId);
        Assert.Equal("This server", loaded.AppName);
        Assert.Equal(["Application", "System"], loaded.Channels);
        Assert.Equal(LogLevel.Error, loaded.MinimumLevel);
        Assert.False(loaded.IsEnabled);
        Assert.Equal(_now, loaded.CreatedUtc);
    }

    [Fact]
    public void Saving_settings_twice_updates_the_single_row_rather_than_inserting()
    {
        _store.SaveSettings(new WinlogSettings { Enabled = true, PollIntervalSeconds = 30 }, _now);
        _store.SaveSettings(new WinlogSettings { Enabled = false, PollIntervalSeconds = 90 }, _now);

        var stored = _store.GetSettings();
        Assert.NotNull(stored);
        Assert.False(stored!.Enabled);
        Assert.Equal(90, stored.PollIntervalSeconds);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
