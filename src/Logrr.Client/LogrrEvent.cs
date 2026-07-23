using System;
using System.Collections.Generic;

namespace Logrr.Client
{
    /// <summary>
    /// A single log event to ship to Logrr. Preserves the message template and structured
    /// properties (not a flattened string), matching Logrr's CLEF ingest.
    /// </summary>
    public sealed class LogrrEvent
    {
        /// <summary>UTC timestamp. Defaults to now when constructed via the helper overloads.</summary>
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;

        public LogrrLevel Level { get; set; } = LogrrLevel.Information;

        /// <summary>Message template with named holes, e.g. "Payment {Amount} failed for {UserId}".</summary>
        public string? MessageTemplate { get; set; }

        /// <summary>Optional pre-rendered message. When null, Logrr renders the template server-side.</summary>
        public string? RenderedMessage { get; set; }

        /// <summary>Exception text (typically <see cref="Exception.ToString"/>).</summary>
        public string? Exception { get; set; }

        public string? TraceId { get; set; }

        public string? SpanId { get; set; }

        /// <summary>Logical source; emitted as the <c>SourceContext</c> property.</summary>
        public string? Source { get; set; }

        /// <summary>Structured properties, preserved as first-class fields.</summary>
        public IReadOnlyDictionary<string, object?>? Properties { get; set; }
    }
}
