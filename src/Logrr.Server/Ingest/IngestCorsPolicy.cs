using Logrr.Storage.Control;

namespace Logrr.Server.Ingest;

/// <summary>
/// Decides which browser origins may post logs cross-origin. The effective allowlist is the
/// union of static config (<c>Ingest:AllowedOrigins</c>) and origins managed in the admin UI.
/// When the list is empty, any origin is allowed — the ingest token in the header is the gate.
///
/// The allowlist is cached in memory (the CORS delegate runs on the ingest hot path) and
/// refreshed whenever the UI adds or removes an origin.
/// </summary>
public sealed class IngestCorsPolicy
{
    private readonly CorsOriginStore _store;
    private readonly Func<DateTimeOffset> _clock;
    private volatile HashSet<string> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Origins pinned via configuration. Read-only in the UI.</summary>
    public IReadOnlyList<string> ConfigOrigins { get; }

    public IngestCorsPolicy(CorsOriginStore store, IConfiguration config, Func<DateTimeOffset> clock)
    {
        _store = store;
        _clock = clock;
        ConfigOrigins = (config.GetSection("Ingest:AllowedOrigins").Get<string[]>() ?? [])
            .Select(CorsOriginStore.Normalize)
            .Where(o => o is not null)
            .Select(o => o!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        Reload();
    }

    /// <summary>No origins configured anywhere → allow any (token-gated).</summary>
    public bool AllowAny => _cache.Count == 0;

    /// <summary>The CORS gate. Runs per request against the in-memory cache.</summary>
    public bool IsAllowed(string origin) => AllowAny || _cache.Contains(origin);

    /// <summary>Origins managed in the admin UI (excludes config-pinned ones).</summary>
    public IReadOnlyList<CorsOrigin> Managed() => _store.List();

    /// <summary>Add a UI-managed origin. Returns the normalised value, or null if it was invalid.</summary>
    public string? Add(string input)
    {
        var origin = CorsOriginStore.Normalize(input);
        if (origin is null)
        {
            return null;
        }
        _store.Add(origin, _clock());
        Reload();
        return origin;
    }

    public void Remove(string origin)
    {
        _store.Remove(origin);
        Reload();
    }

    private void Reload()
    {
        var set = new HashSet<string>(ConfigOrigins, StringComparer.OrdinalIgnoreCase);
        foreach (var o in _store.List())
        {
            set.Add(o.Origin);
        }
        _cache = set;
    }
}
