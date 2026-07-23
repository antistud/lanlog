using System;

namespace Logrr.Client
{
    /// <summary>Configuration for <see cref="LogrrClient"/> and the logging provider.</summary>
    public sealed class LogrrClientOptions
    {
        /// <summary>Base URL of the Logrr server, e.g. <c>https://logrr.internal</c>.</summary>
        public string Endpoint { get; set; } = "";

        /// <summary>An ingest-scoped token: <c>lg_{appId}_{secret}</c>.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Events below this level are dropped client-side.</summary>
        public LogrrLevel MinimumLevel { get; set; } = LogrrLevel.Verbose;

        /// <summary>Flush when this many events are buffered…</summary>
        public int BatchSizeLimit { get; set; } = 500;

        /// <summary>…or after this interval, whichever comes first.</summary>
        public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);

        /// <summary>
        /// Hard cap on the in-memory buffer. Beyond it, new events are dropped rather than
        /// growing unbounded — logging must never take down the host app.
        /// </summary>
        public int QueueLimit { get; set; } = 100_000;

        /// <summary>Per-request timeout when the client owns its own <see cref="System.Net.Http.HttpClient"/>.</summary>
        public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

        internal string IngestUrl => Endpoint.TrimEnd('/') + "/api/events/raw";

        internal void Validate()
        {
            if (string.IsNullOrWhiteSpace(Endpoint))
            {
                throw new ArgumentException("Logrr endpoint is required.", nameof(Endpoint));
            }
            if (string.IsNullOrWhiteSpace(ApiKey))
            {
                throw new ArgumentException("Logrr API key is required.", nameof(ApiKey));
            }
        }
    }
}
