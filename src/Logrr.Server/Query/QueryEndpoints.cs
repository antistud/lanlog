using Logrr.Contracts;
using Logrr.Core.Filters;
using Logrr.Notify;
using Logrr.Server.Auth;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Server.Query;

public static class QueryEndpoints
{
    public static void MapQueryEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/apps", (HttpContext http, TokenAuthenticator auth, AppStore apps) =>
            RequireRead(http, auth, null, () =>
                Results.Ok(apps.List().Select(ToDto))));

        app.MapGet("/api/v1/apps/{appId}/events", (
            string appId, HttpContext http, TokenAuthenticator auth, EventReader reader,
            string? from, string? to, string? level, string? q, string? filter, string? cursor, int? limit) =>
            RequireRead(http, auth, appId, () =>
            {
                var query = new EventQuery
                {
                    AppId = appId,
                    From = ParseTime(from),
                    To = ParseTime(to),
                    MinLevel = level is not null && Logrr.Core.LevelMap.TryParse(level, out var lvl) ? lvl : null,
                    Text = q,
                    Filter = filter,
                    Cursor = cursor,
                    Limit = limit ?? 100,
                };
                try
                {
                    return Results.Ok(reader.Query(query));
                }
                catch (FilterParseException ex)
                {
                    return Results.BadRequest(new { error = ex.Message, position = ex.Position });
                }
            }));

        app.MapGet("/api/v1/apps/{appId}/events/{eventId}", (
            string appId, string eventId, HttpContext http, TokenAuthenticator auth, EventReader reader) =>
            RequireRead(http, auth, appId, () =>
            {
                var ev = reader.GetById(appId, eventId);
                return ev is null ? Results.NotFound() : Results.Ok(ev);
            }));

        app.MapGet("/api/v1/apps/{appId}/stats", (
            string appId, HttpContext http, TokenAuthenticator auth, StatsReader stats) =>
            RequireRead(http, auth, appId, () =>
                Results.Ok(stats.GetStats(appId, DateTimeOffset.UtcNow))));

        app.MapGet("/api/v1/apps/{appId}/tickets", (
            string appId, HttpContext http, TokenAuthenticator auth, TicketLinkStore links) =>
            RequireRead(http, auth, appId, () => Results.Ok(links.ListByApp(appId))));

        // Liveness — anonymous (SPEC §13).
        app.MapGet("/health", (
            IngestPipeline ingest, Logrr.Realtime.RealtimeBroker broker, DeliveryStore deliveries,
            StoragePaths paths, Microsoft.Extensions.Options.IOptions<ServerOptions> opts) =>
        {
            var diskFreeMb = DiskFreeMb(paths.DataRoot);
            var deadLettered = deliveries.CountByStatus(DeliveryStatus.DeadLettered);
            var pending = deliveries.CountByStatus(DeliveryStatus.Pending);
            var guardTripped = diskFreeMb < opts.Value.MinFreeDiskMb;

            var dto = new HealthDto
            {
                Status = guardTripped ? "degraded" : "ok",
                QueueDepth = ingest.QueueDepth,
                DroppedLastHour = ingest.DroppedTotal,
                DiskFreeMb = diskFreeMb,
                OldestUnflushedMs = 0,
                ActiveSubscriptions = broker.ActiveCount,
                PendingDeliveries = pending,
                DeadLettered = deadLettered,
            };
            return guardTripped
                ? Results.Json(dto, statusCode: StatusCodes.Status503ServiceUnavailable)
                : Results.Ok(dto);
        });

        app.MapGet("/api/v1/buildinfo", () => Results.Ok(new BuildInfoDto
        {
            Version = typeof(QueryEndpoints).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            Commit = Environment.GetEnvironmentVariable("LOGRR_COMMIT"),
            BuildDate = null,
        }));
    }

    /// <summary>Read requires a Read-scoped token for the app, or a UI session (SPEC §7).</summary>
    private static IResult RequireRead(HttpContext http, TokenAuthenticator auth, string? appId, Func<IResult> handler)
    {
        var hasTokenHeader = http.Request.Headers.ContainsKey("X-Logrr-ApiKey")
            || http.Request.Headers.ContainsKey("X-Seq-ApiKey")
            || http.Request.Headers.ContainsKey("Authorization");

        if (hasTokenHeader)
        {
            var result = auth.Authenticate(http, TokenScopes.Read);
            if (!result.Ok)
            {
                return result.Failure == AuthFailure.Forbidden
                    ? Results.StatusCode(StatusCodes.Status403Forbidden)
                    : Results.StatusCode(StatusCodes.Status401Unauthorized);
            }
            if (appId is not null && result.Context!.App.Id != appId)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            return handler();
        }

        if (http.User.Identity?.IsAuthenticated == true)
        {
            return handler();
        }

        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    private static DateTimeOffset? ParseTime(string? s) =>
        DateTimeOffset.TryParse(s, out var t) ? t : null;

    private static long DiskFreeMb(string dataRoot)
    {
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(dataRoot));
            return string.IsNullOrEmpty(root) ? 0 : new DriveInfo(root).AvailableFreeSpace / (1024 * 1024);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return long.MaxValue;
        }
    }

    private static AppDto ToDto(AppRecord a) => new()
    {
        Id = a.Id,
        Name = a.Name,
        Description = a.Description,
        RetentionDays = a.RetentionDays,
        MaxSizeMb = a.MaxSizeMb,
        MinimumLevel = a.MinimumLevel,
        IndexedProperties = a.IndexedProperties,
        IsEnabled = a.IsEnabled,
        CreatedUtc = a.CreatedUtc,
    };
}
