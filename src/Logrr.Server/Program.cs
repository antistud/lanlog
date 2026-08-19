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
using Logrr.Server.WindowsEvents;
using Logrr.Storage;
using Logrr.Storage.Control;
using Logrr.Storage.Sql;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Serve static web assets — including the framework's _framework/blazor.web.js, which the
// interactive Blazor circuit needs — in every environment. Without this the host only wires
// it up in Development, so running the build output as Production (e.g.
// `dotnet run --no-launch-profile`) silently loses interactivity. Harmless once published,
// where the assets are copied into wwwroot.
// This reads Logrr.staticwebassets.runtime.json, which exists ONLY in build output and
// hardcodes absolute paths on the build machine. A real publish has no such file and this
// is a no-op. But deploy the build folder (bin\Release\net10.0\win-x64) by mistake and the
// file comes along, PhysicalFileProvider throws DirectoryNotFoundException on paths that
// exist only on the build box, and the process dies here - before Serilog is configured
// below, so nothing is logged anywhere. Under IIS that is a bare 500.30 with no
// diagnostics. Translate it into an error that names the actual mistake.
try
{
    builder.WebHost.UseStaticWebAssets();
}
catch (DirectoryNotFoundException ex)
{
    throw new InvalidOperationException(
        $"Static web asset root '{ex.Message.Trim()}' does not exist. This almost always means " +
        "the app was deployed from the BUILD folder (bin\\Release\\net10.0\\win-x64) instead of " +
        "the PUBLISH folder (bin\\Release\\net10.0\\publish). Redeploy from the publish folder - " +
        "see docs/SETUP.md.", ex);
}

// ---- Data path resolution (SPEC §2): env → appsettings → C:\Logrr (never in the app folder).
var dataPath = DataPath.Resolve(builder.Configuration, builder.Environment.ContentRootPath);
var paths = new StoragePaths(dataPath);
paths.EnsureRootDirectories();

// ---- Self-logging: a rolling file in the data dir (SPEC §13), plus the console so startup
// success/failure is visible when run directly AND in IIS's ASP.NET Core Module stdout log.
// Without the console sink a startup crash is silent under IIS - which is a debugging trap.
builder.Host.UseSerilog((ctx, cfg) => cfg
    .MinimumLevel.Information()
    .WriteTo.Console()
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
    // Empty keeps everything in SQLite files under the data path; set it and both the control
    // tables and the event partitions move into SQL Server instead (SPEC §4.7).
    ConnectionString = StorageConnection.Resolve(builder.Configuration),
    Schema = cfgRoot.GetValue("Storage:Schema", "logrr")!,
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
// One dialect object decides where everything lands; the stores and readers above it are
// backend-agnostic. Resolved once here so a bad connection string fails at startup, in the
// console and the internal log, rather than on the first ingest request.
var dialect = SqlDialect.Create(storageOptions, paths);

builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(dialect);
builder.Services.AddSingleton(sp => new ControlDatabase(sp.GetRequiredService<SqlDialect>()));
builder.Services.AddSingleton(sp => new PartitionManager(sp.GetRequiredService<SqlDialect>()));
builder.Services.AddSingleton<AppStore>();
builder.Services.AddSingleton<TokenStore>();
builder.Services.AddSingleton<WinlogCursorStore>();
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<SavedSearchStore>();
builder.Services.AddSingleton<AckStore>();
builder.Services.AddSingleton<EventReader>();
builder.Services.AddSingleton<TraceReader>();
builder.Services.AddSingleton<StatsReader>();
builder.Services.AddSingleton<RetentionMaintenance>();

// ---- Realtime ----
builder.Services.AddSingleton<RealtimeBroker>();
builder.Services.AddSingleton<ConnectionSubscriptions>();
builder.Services.AddSingleton<LiveSignals>();

// ---- Notify ----
builder.Services.AddSingleton<ISecretProtector, DataProtectionSecretProtector>();
builder.Services.AddSingleton<ISmtpSender, MailKitSmtpSender>();
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
    clock,
    sp.GetRequiredService<ISmtpSender>()));

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

var windowsAuth = new WindowsAuthOptions
{
    Enabled = cfgRoot.GetValue("Auth:Windows:Enabled", false),
    AutoSignIn = cfgRoot.GetValue("Auth:Windows:AutoSignIn", true),
};
builder.Services.AddSingleton(windowsAuth);
builder.Services.AddScoped<WindowsAuthDiagnostics>();

// The cookie stays the one and only session mechanism. Windows auth is a *sign-in route*, not a
// second way to be authenticated: /auth/windows challenges Negotiate, maps the Windows identity
// onto a Logrr account and issues the same cookie. Everything downstream - the Blazor circuit,
// the SignalR hub, roles, per-app access - is untouched by this, which is the whole point.
var authBuilder = builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.AccessDeniedPath = "/login";
    });
if (windowsAuth.Enabled)
{
    // Under IIS in-process the module has already done the handshake, and this handler forwards
    // to the server's "Windows" scheme instead of running its own - so no extra config per host.
    authBuilder.AddNegotiate();
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("Admin", p => p.RequireRole("Admin"));

// ---- Web ----
builder.Services.AddSignalR();
builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();

// Let browser code post logs directly. Access is gated by the ingest token in a header
// (not cookies). The allowed origins are managed in the admin UI (union with any pinned in
// Ingest:AllowedOrigins); an empty list means any origin, since the token is the real gate.
builder.Services.AddSingleton<CorsOriginStore>();
builder.Services.AddSingleton<IngestCorsPolicy>();
builder.Services.AddCors();
builder.Services.AddOptions<CorsOptions>().Configure<IngestCorsPolicy>((cors, policy) =>
    cors.AddPolicy(IngestEndpoints.CorsPolicy, p => p
        .SetIsOriginAllowed(policy.IsAllowed)
        .WithMethods("POST", "OPTIONS")
        .WithHeaders("Content-Type", "X-Logrr-ApiKey", "X-Seq-ApiKey", "Authorization")));

var maxRequestBytes = cfgRoot.GetValue("Ingest:MaxRequestBytes", 10_485_760L);
builder.WebHost.ConfigureKestrel(k =>
{
    k.Limits.MaxRequestBodySize = maxRequestBytes;

    // Negotiate is a connection-level handshake and the handler simply does nothing on HTTP/2 or
    // HTTP/3 - no WWW-Authenticate header, so the browser gets a bare 401 and the user never sees
    // a sign-in. Over HTTPS Kestrel negotiates HTTP/2 by default, which is exactly the LAN case:
    // works on http://localhost, silently dead over https://server. Cap the endpoints at HTTP/1.1
    // whenever Windows sign-in is on. Nothing here needs HTTP/2 (Blazor's circuit is a WebSocket,
    // itself HTTP/1.1), and under IIS this whole callback is moot - IIS is the server there.
    if (windowsAuth.Enabled)
    {
        k.ConfigureEndpointDefaults(o => o.Protocols = HttpProtocols.Http1);
    }
});
// Under IIS in-process hosting Kestrel is not the server, so the line above is ignored and
// IIS's own 30 MB default applies instead. Mirror the limit onto the IIS server so raising
// Ingest:MaxRequestBytes past 30 MB doesn't start rejecting batches only when hosted.
builder.Services.Configure<IISServerOptions>(o => o.MaxRequestBodySize = maxRequestBytes);

// ---- Hosted services ----
builder.Services.AddHostedService<DispatcherService>();
builder.Services.AddHostedService<RetentionService>();
builder.Services.AddHostedService<IngestDrainService>();

// ---- Agentless Windows Event Log collection (SPEC §6.4) ----
// The server reads the event log itself, locally or over RPC, so the collected machines need
// nothing installed - which is the "no agent" promise in SPEC §1 taken literally. Which
// machines to read is control-DB state edited at /admin/windows-events, so the collector is
// registered unconditionally on Windows and idles until it is switched on; Logrr:WindowsEvents
// only seeds those rows on the first run after the upgrade.
builder.Services.AddSingleton<WinlogConfigStore>();
builder.Services.AddSingleton<WindowsEventSettings>();
if (OperatingSystem.IsWindows())
{
    WindowsEventRegistration.Add(builder.Services);
}
else
{
    builder.Services.AddHostedService<WindowsEventUnavailableService>();
}

var app = builder.Build();

// Which backend is live is the first thing you want to know from a support log — it explains
// where the data went and which half of the setup guide applies.
app.Logger.LogInformation("Logrr storage backend: {Backend}", dialect.Describe());

// Windows sign-in spans three places that cannot see each other - the host's own configuration,
// this setting, and the per-user link - and a failure in any of them looks identical from the
// browser. State what this process actually came up with, so the support log answers "why am I
// still looking at a password form?" without anyone having to reach the diagnostics page.
if (windowsAuth.Enabled)
{
    var hostAuth = app.Services.GetServices<IServerIntegratedAuth>().LastOrDefault();
    app.Logger.LogInformation("Windows sign-in enabled (auto sign-in {Auto}); host integrated auth: {Host}",
        windowsAuth.AutoSignIn ? "on" : "off",
        hostAuth is null
            ? "none - this process performs the Negotiate handshake itself, over HTTP/1.1"
            : hostAuth.IsEnabled
                ? $"provided by the host under scheme '{hostAuth.AuthenticationScheme}'"
                : "the host owns Windows authentication and it is TURNED OFF - the handshake cannot complete");
}
else
{
    // The most common cause of "Windows sign-in does nothing", and invisible from the browser.
    app.Logger.LogInformation("Windows sign-in is off (Logrr:Auth:Windows:Enabled = false)");
}

// First-run bootstrap before serving.
using (var scope = app.Services.CreateScope())
{
    scope.ServiceProvider.GetRequiredService<FirstRunBootstrapper>().Run();
    // After the migrations, and before the collector's timer starts: loads the Windows event
    // settings, importing them from Logrr:WindowsEvents the first time the tables are empty.
    scope.ServiceProvider.GetRequiredService<WindowsEventSettings>().Initialize();
    // Warm the ingest pipeline singleton so writes are ready immediately.
    scope.ServiceProvider.GetRequiredService<IngestPipeline>();
}

app.UseStaticFiles();
app.UseCors();
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

/// <summary>
/// SQL Server connection-string resolution (SPEC §4.7). Same precedence as the data path:
/// environment first, so a deployment can point at a different database without editing the
/// published <c>appsettings.json</c>. An empty result means "stay on SQLite".
/// </summary>
internal static class StorageConnection
{
    public static string Resolve(IConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("LOGRR_SQL_CONNECTION");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env;
        }
        var configured = config["Logrr:Storage:ConnectionString"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }
        // Also honour the conventional slot, so a shop that keeps every connection string in
        // ConnectionStrings does not have to make an exception for this one.
        return config.GetConnectionString("Logrr") ?? "";
    }
}

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
            // C:\Logrr rather than %ProgramData%\Logrr: the data root is something operators
            // back up, restore and point tooling at by hand (SPEC §13), and a hidden-by-default
            // system folder makes that needlessly awkward. Derived from the system drive rather
            // than hardcoded so a box that boots from D: still lands somewhere sane.
            var systemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System))
                              ?? "C:\\";
            return Path.Combine(systemDrive, "Logrr");
        }
        // Non-Windows dev/CI: never inside the app folder's published output.
        return Path.Combine(contentRoot, "data");
    }
}

// Exposed for WebApplicationFactory-based integration tests.
public partial class Program;
