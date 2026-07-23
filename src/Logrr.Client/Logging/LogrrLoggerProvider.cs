using System;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Logrr.Client.Logging
{
    /// <summary>
    /// An <see cref="ILoggerProvider"/> that ships Microsoft.Extensions.Logging output to a
    /// Logrr server. Owns a single shared <see cref="LogrrClient"/>.
    /// </summary>
    public sealed class LogrrLoggerProvider : ILoggerProvider
    {
        private readonly LogrrClient _client;
        private readonly LogLevel _minLevel;
        private readonly ConcurrentDictionary<string, LogrrLogger> _loggers =
            new ConcurrentDictionary<string, LogrrLogger>();

        public LogrrLoggerProvider(LogrrClientOptions options)
            : this(options, null)
        {
        }

        // Test seam: inject an HttpClient (e.g. over a stub handler).
        internal LogrrLoggerProvider(LogrrClientOptions options, System.Net.Http.HttpClient? httpClient)
        {
            _client = new LogrrClient(options, httpClient);
            _minLevel = ToLogLevel(options.MinimumLevel);
        }

        internal System.Threading.Tasks.Task FlushAsync() => _client.FlushAsync();

        public ILogger CreateLogger(string categoryName) =>
            _loggers.GetOrAdd(categoryName, name => new LogrrLogger(_client, name, _minLevel));

        public void Dispose() => _client.Dispose();

        private static LogLevel ToLogLevel(LogrrLevel level)
        {
            switch (level)
            {
                case LogrrLevel.Verbose: return LogLevel.Trace;
                case LogrrLevel.Debug: return LogLevel.Debug;
                case LogrrLevel.Warning: return LogLevel.Warning;
                case LogrrLevel.Error: return LogLevel.Error;
                case LogrrLevel.Fatal: return LogLevel.Critical;
                default: return LogLevel.Information;
            }
        }
    }
}
