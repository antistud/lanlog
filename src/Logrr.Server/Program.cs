using Logrr.Notify;
using Logrr.Realtime;
using Logrr.Server;
using Logrr.Server.Admin;
using Logrr.Server.Auth;
using Logrr.Server.Bootstrap;
using Logrr.Server.Components;
using Logrr.Server.Hosting;
using Logrr.Server.Ingest;
using Logrr.Server.Query;
using Logrr.Server.Realtime;
using Logrr.Server.Security;
using Logrr.Storage;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serve static web assets — including the framework's _framework/blazor.web.js, which the
// interactive Blazor circuit needs — in every environment. Without this the host only wires
// it up in Development, so running the build output as Production (e.g.
// `dotnet run --no-launch-profile`) silently loses interactivity. Harmless once published,
// where the assets are copied into wwwroot.
builder.WebHost.UseStaticWebAssets();

// ---- Data path resolution (SPEC §2): env → appsettings → %ProgramData% (never in app folder).
var dataPath = DataPath.Resolve(builder.Configuration, builder.Environment.ContentRootPath);
var paths = new StoragePaths(dataPath);
paths.EnsureRootDirectories();

// ---- Self-logging to a rolling file in the data dir (SPEC §13). Never logs to itself.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .WriteTo.File(Path.Combine(dataPath, "logrr-internal.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 7));

// ---- Options ----
var cfgRoot = builder.Configuration.GetSection("Logrr");
builder.Services.Configure<ServerOptions>(o =>
{
    o.MinFreeDiskMb = cfgRoot.GetValue("Storage:MinFreeDiskMb", 5120L);
    o.MaxRequestBytes = cfgRoot.GetValue("Ingest:MaxRequestBytes", 10_485_760L);
    o.PublicBaseUrl = cfgRoot.GetValue("Notify:PublicBaseUrl", "https://logrr.internal")!;
});

var storageOptions = new StorageOptions
{
    DataPath = dataPath,
    MinFreeDiskMb = cfgRoot.GetValue("Storage:MinFreeDiskMb", 5120L),
    ChannelCapacity = cfgRoot.GetValue("Ingest:ChannelCapacity", 20_000),
    BatchSize = cfgRoot.GetValue("Ingest:BatchSize", 500),
    FlushIntervalMs = cfgRoot.GetValue("Ingest:FlushIntervalMs", 500),
};
var realtimeOptions = new RealtimeOptions
{
    FrameIntervalMs = cfgRoot.GetValue("Realtime:FrameIntervalMs", 250),
    MaxEventsPerFrame = cfgRoot.GetValue("Realtime:MaxEventsPerFrame", 200),
    SubscriptionBufferSize = cfgRoot.GetValue("Realtime:SubscriptionBufferSize", 2000),
    MaxSubscriptions = cfgRoot.GetValue("Realtime:MaxSubscriptions", 50),
    MaxSubscriptionsPerUser = cfgRoot.GetValue("Realtime:MaxSubscriptionsPerUser", 5),
    ReconnectBackfillLimit = cfgRoot.GetValue("Realtime:ReconnectBackfillLimit", 500),
};
var notifyOptions = new NotifyOptions
{
    DispatcherConcurrency = cfgRoot.GetValue("Notify:DispatcherConcurrency", 4),
    RuleEvaluationMaxAgeMinutes = cfgRoot.GetValue("Notify:RuleEvaluationMaxAgeMinutes", 15),
    GlobalMaxDeliveriesPerHour = cfgRoot.GetValue("Notify:GlobalMaxDeliveriesPerHour", 500),
    DeadLetterRetentionDays = cfgRoot.GetValue("Notify:DeadLetterRetentionDays", 30),
    PublicBaseUrl = cfgRoot.GetValue("Notify:PublicBaseUrl", "https://logrr.internal")!,
};
var ingestLimits = new IngestLimits
{
    MaxEventBytes = cfgRoot.GetValue("Ingest:MaxEventBytes", 262_144),
};

builder.Services.AddSingleton(storageOptions);
builder.Services.AddSingleton(realtimeOptions);
builder.Services.AddSingleton(notifyOptions);
builder.Services.AddSingleton(ingestLimits);

// ---- Infrastructure ----
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("logrr-webhooks");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(paths.KeysDir));

Func<DateTimeOffset> clock = () => DateTimeOffset.UtcNow;
builder.Services.AddSingleton(clock);

// ---- Storage ----
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton<ControlDatabase>();
builder.Services.AddSingleton<PartitionManager>();
builder.Services.AddSingleton<AppStore>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<EventReader>();
builder.Services.AddSingleton<StatsReader>();
builder.Services.AddSingleton<RetentionMaintenance>();

// ---- Realtime ----
builder.Services.AddSingleton<RealtimeBroker>();
builder.Services.AddSingleton<ConnectionSubscriptions>();
builder.Services.AddSingleton<LiveSignals>();

// ---- Notify ----
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<DestinationStore>();
builder.Services.AddSingleton<RuleStore>();
builder.Services.AddSingleton<DeliveryStore>();
builder.Services.AddSingleton<OccurrenceStore>();
builder.Services.AddSingleton<TicketLinkStore>();
builder.Services.AddSingleton<ManualTicketService>();
builder.Services.AddSingleton(sp => new RuleEngine(
    sp.GetRequiredService<RuleStore>(),
    sp.GetRequiredService<DestinationStore>(),
    sp.GetRequiredService<OccurrenceStore>(),
    sp.GetRequiredService<DeliveryStore>(),
    sp.GetRequiredService<NotifyOptions>(),
    appId => sp.GetRequiredService<AppStore>().Get(appId)?.Name,
    clock));
builder.Services.AddSingleton(sp => new DeliveryDispatcher(
    sp.GetRequiredService<DeliveryStore>(),
    sp.GetRequiredService<DestinationStore>(),
    sp.GetRequiredService<TicketLinkStore>(),
    sp.GetRequiredService<ISecretProtector>(),
    sp.GetRequiredService<NotifyOptions>(),
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("logrr-webhooks"),
    clock));

// ---- Ingest pipeline (post-commit fans out to realtime + rules) ----
builder.Services.AddSingleton(sp =>
{
    var pm = sp.GetRequiredService<PartitionManager>();
    var appStore = sp.GetRequiredService<AppStore>();
    var broker = sp.GetRequiredService<RealtimeBroker>();
    var rules = sp.GetRequiredService<RuleEngine>();
    var signals = sp.GetRequiredService<LiveSignals>();
    var log = sp.GetRequiredService<ILoggerFactory>().CreateLogger("Ingest");
    return new IngestPipeline(
        pm, storageOptions,
        appId => appStore.Get(appId)?.IndexedProperties ?? [],
        commit =>
        {
            broker.Publish(commit.AppId, commit.Day, commit.Rows);
            rules.Evaluate(commit);
            signals.RaiseAppActivity(commit.AppId);
        },
        (appId, ex) => log.LogError(ex, "Ingest write failed for app {App}", appId));
});
builder.Services.AddSingleton<IngestService>();

// ---- Auth ----
builder.Services.AddSingleton<TokenAuthenticator>();
builder.Services.AddSingleton<FirstRunBootstrapper>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

// ---- Web ----
builder.Services.AddSignalR();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize =
    cfgRoot.GetValue("Ingest:MaxRequestBytes", 10_485_760L));

// ---- Hosted services ----
builder.Services.AddHostedService<DispatcherService>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<IngestDrainService>();

var app = builder.Build();

// First-run bootstrap before serving.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<FirstRunBootstrapper>().Run();
    // Warm the ingest pipeline singleton so writes are ready immediately.
    scope.ServiceProvider.GetRequiredService<IngestPipeline>();
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapIngestEndpoints(cfgRoot.GetValue("Ingest:MaxRequestBytes", 10_485_760L));
app.MapQueryEndpoints();
app.MapStreamEndpoints();
app.MapAdminEndpoints();
app.MapAuthEndpoints();
app.MapHub<TailHub>("/hubs/tail");
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();

/// <summary>Data root resolution per SPEC §2.</summary>
internal static class DataPath
{
    public static string Resolve(IConfiguration config, string contentRoot)
    {
        var env = Environment.GetEnvironmentVariable("LOGRR_DATA_PATH");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        var configured = config["Logrr:Storage:DataPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        if (OperatingSystem.IsWindows())
        {
            var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            return Path.Combine(programData, "Logrr");
        }
        // Non-Windows dev/CI: never inside the app folder's published output.
        return Path.Combine(contentRoot, "data");
    }
}

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
