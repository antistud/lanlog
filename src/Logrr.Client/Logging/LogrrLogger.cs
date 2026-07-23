using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace Logrr.Client.Logging
{
    /// <summary>
    /// An <see cref="ILogger"/> that forwards to a <see cref="LogrrClient"/>, preserving the
    /// message template (<c>{OriginalFormat}</c>) and structured state as properties.
    /// </summary>
    internal sealed class LogrrLogger : ILogger
    {
        private readonly LogrrClient _client;
        private readonly string _category;
        private readonly LogLevel _minLevel;

        public LogrrLogger(LogrrClient client, string category, LogLevel minLevel)
        {
            _client = client;
            _category = category;
            _minLevel = minLevel;
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string? template = null;
            Dictionary<string, object?>? properties = null;

            if (state is IReadOnlyList<KeyValuePair<string, object>> pairs)
            {
                for (var i = 0; i < pairs.Count; i++)
                {
                    var pair = pairs[i];
                    if (pair.Key == "{OriginalFormat}")
                    {
                        template = pair.Value as string;
                    }
                    else
                    {
                        (properties ??= new Dictionary<string, object?>())[pair.Key] = pair.Value;
                    }
                }
            }

            if (eventId.Id != 0 || !string.IsNullOrEmpty(eventId.Name))
            {
                (properties ??= new Dictionary<string, object?>())["EventId"] = eventId.Id;
            }

            var rendered = formatter != null ? formatter(state, exception) : state?.ToString();

            _client.Emit(new LogrrEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = MapLevel(logLevel),
                MessageTemplate = template ?? rendered,
                RenderedMessage = template != null ? rendered : null,
                Exception = exception?.ToString(),
                Source = _category,
                Properties = properties,
            });
        }

        private static LogrrLevel MapLevel(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Trace: return LogrrLevel.Verbose;
                case LogLevel.Debug: return LogrrLevel.Debug;
                case LogLevel.Information: return LogrrLevel.Information;
                case LogLevel.Warning: return LogrrLevel.Warning;
                case LogLevel.Error: return LogrrLevel.Error;
                case LogLevel.Critical: return LogrrLevel.Fatal;
                default: return LogrrLevel.Information;
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new NullScope();
            public void Dispose() { }
        }
    }
}
