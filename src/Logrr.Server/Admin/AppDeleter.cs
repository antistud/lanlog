using Logrr.Notify;
using Logrr.Server.Auth;
using Logrr.Server.WindowsEvents;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Server.Admin;

/// <summary>
/// Removes an app and everything that hangs off it (SPEC §7): its partitions, tokens, rules,
/// saved searches, acknowledgements, ticket links and delivery history. Irreversible — the
/// events are dropped whole, not archived.
/// </summary>
/// <remarks>
/// Order matters. Windows event sources go first (the collector re-creates an app it is still
/// configured to collect into, so deleting the app alone would have it reappear on the next
/// poll), then the tokens and the app row so nothing new can be ingested, and only then the
/// partitions — a write racing the delete would otherwise re-create the file it just removed.
/// All-apps rules (a NULL <c>app_id</c>) are left alone: they outlive any one app.
/// </remarks>
public sealed class AppDeleter(
    AppStore apps,
    TokenStore tokens,
    SavedSearchStore searches,
    AckStore acks,
    RuleStore rules,
    OccurrenceStore occurrences,
    TicketLinkStore ticketLinks,
    DeliveryStore deliveries,
    WindowsEventSettings winlog,
    PartitionManager partitions,
    StoragePaths paths,
    TokenAuthenticator auth,
    ILogger<AppDeleter> logger)
{
    /// <summary>What the delete actually removed, for the confirmation the UI shows.</summary>
    public sealed record Result(
        string AppId, int Partitions, int Tokens, int Rules, int SavedSearches, int WindowsSources);

    /// <summary>Delete an app, or null when there is no such app.</summary>
    public Result? Delete(string appId)
    {
        if (apps.Get(appId) is null)
        {
            return null;
        }

        // 1. Stop the Windows event collector re-creating the app on its next poll. Through the
        //    settings service rather than the store, so the snapshot the collector actually polls
        //    against is rebuilt — and the machine's cursors are dropped with the source.
        var sources = winlog.Sources().Where(s => s.AppId == appId).ToList();
        foreach (var source in sources)
        {
            winlog.DeleteSource(source.Id);
        }

        // 2. Close the ingest door: no valid token, no app record, no cached resolution of either.
        var tokenCount = tokens.DeleteByApp(appId);
        apps.Delete(appId);
        auth.InvalidateAll();

        // 3. Alerting state scoped to the app. The occurrence rollups are keyed by rule, so they
        //    have to be cleared while the rule ids are still known.
        var appRules = rules.ListByApp(appId);
        foreach (var rule in appRules)
        {
            occurrences.ResetForRule(rule.Id);
        }
        rules.DeleteByApp(appId);
        ticketLinks.DeleteByApp(appId);
        deliveries.DeleteByApp(appId);

        // 4. Explore state.
        var searchCount = searches.DeleteByApp(appId);
        acks.ClearAll(appId);

        // 5. The events themselves, one partition at a time.
        var partitionCount = 0;
        foreach (var day in partitions.ExistingDaysDescending(appId))
        {
            partitions.ClosePartition(appId, day);
            if (partitions.Dialect.DeletePartition(appId, day))
            {
                partitionCount++;
            }
        }
        RemoveAppDirectory(appId);

        logger.LogWarning(
            "Deleted app {App}: {Partitions} partition(s), {Tokens} token(s), {Rules} rule(s), " +
            "{Searches} saved search(es), {Sources} Windows event source(s)",
            appId, partitionCount, tokenCount, appRules.Count, searchCount, sources.Count);

        return new Result(appId, partitionCount, tokenCount, appRules.Count, searchCount, sources.Count);
    }

    /// <summary>
    /// Tidy up the now-empty <c>apps\{id}</c> folder on the SQLite backend. On SQL Server there
    /// is no such folder and this is a no-op. Best-effort: a leftover directory is cosmetic,
    /// and a locked file must not fail a delete whose real work is already done.
    /// </summary>
    private void RemoveAppDirectory(string appId)
    {
        var dir = paths.AppDir(appId);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not remove the folder for deleted app {App} at {Dir}", appId, dir);
        }
    }
}
