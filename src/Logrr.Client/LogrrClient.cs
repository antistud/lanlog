using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Logrr.Client
{
    /// <summary>
    /// A batching, fire-and-forget ingest client. Events are buffered and flushed on a size
    /// or time trigger, off the calling thread. A server outage delays and (past the buffer
    /// cap) drops events — it never blocks or throws into the host app.
    /// </summary>
    public sealed class LogrrClient : IDisposable
    {
        private readonly LogrrClientOptions _options;
        private readonly HttpClient _http;
        private readonly bool _ownsHttp;
        private readonly ConcurrentQueue<LogrrEvent> _queue = new ConcurrentQueue<LogrrEvent>();
        private readonly SemaphoreSlim _flushGate = new SemaphoreSlim(1, 1);
        private readonly Timer _timer;
        private int _count;
        private long _dropped;
        private int _disposed;

        public LogrrClient(LogrrClientOptions options, HttpClient? httpClient = null)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _options.Validate();

            if (httpClient != null)
            {
                _http = httpClient;
                _ownsHttp = false;
            }
            else
            {
                _http = new HttpClient { Timeout = _options.RequestTimeout };
                _ownsHttp = true;
            }

            _timer = new Timer(_ => FireAndForgetFlush(), null, _options.FlushInterval, _options.FlushInterval);
        }

        /// <summary>Events dropped so far because the buffer was full or a send failed.</summary>
        public long DroppedCount => Interlocked.Read(ref _dropped);

        /// <summary>Buffer and (eventually) ship an event. Returns immediately.</summary>
        public void Emit(LogrrEvent logEvent)
        {
            if (logEvent == null || logEvent.Level < _options.MinimumLevel || _disposed != 0)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _count, 0, 0) >= _options.QueueLimit)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }

            _queue.Enqueue(logEvent);
            if (Interlocked.Increment(ref _count) >= _options.BatchSizeLimit)
            {
                FireAndForgetFlush();
            }
        }

        /// <summary>Convenience overload for callers not using Microsoft.Extensions.Logging.</summary>
        public void Log(LogrrLevel level, string messageTemplate, Exception? exception = null,
            IReadOnlyDictionary<string, object?>? properties = null)
        {
            Emit(new LogrrEvent
            {
                Timestamp = DateTimeOffset.UtcNow,
                Level = level,
                MessageTemplate = messageTemplate,
                Exception = exception?.ToString(),
                Properties = properties,
            });
        }

        private void FireAndForgetFlush()
        {
            // Deliberately not awaited: buffering thread must never block on the network.
            _ = FlushAsync();
        }

        /// <summary>Send all buffered events now. Safe to call concurrently.</summary>
        public async Task FlushAsync()
        {
            await _flushGate.WaitAsync().ConfigureAwait(false);
            try
            {
                while (true)
                {
                    var batch = Drain(_options.BatchSizeLimit);
                    if (batch.Count == 0)
                    {
                        return;
                    }
                    await SendAsync(batch).ConfigureAwait(false);
                }
            }
            finally
            {
                _flushGate.Release();
            }
        }

        private List<LogrrEvent> Drain(int max)
        {
            var batch = new List<LogrrEvent>(Math.Min(max, 64));
            while (batch.Count < max && _queue.TryDequeue(out var e))
            {
                Interlocked.Decrement(ref _count);
                batch.Add(e);
            }
            return batch;
        }

        private async Task SendAsync(List<LogrrEvent> batch)
        {
            try
            {
                var payload = Clef.Serialize(batch);
                using (var content = new ByteArrayContent(payload))
                {
                    content.Headers.ContentType = new MediaTypeHeaderValue("application/vnd.serilog.clef");
                    using (var request = new HttpRequestMessage(HttpMethod.Post, _options.IngestUrl))
                    {
                        request.Content = content;
                        request.Headers.TryAddWithoutValidation("X-Logrr-ApiKey", _options.ApiKey);
                        using (var response = await _http.SendAsync(request).ConfigureAwait(false))
                        {
                            // A rejected batch is dropped — retrying a bad payload forever helps no one.
                            if (!response.IsSuccessStatusCode)
                            {
                                Interlocked.Add(ref _dropped, batch.Count);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Network/transport failure: drop this batch. Logging is fire-and-forget.
                Interlocked.Add(ref _dropped, batch.Count);
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _timer.Dispose();
            try
            {
                // Best-effort final drain, bounded so shutdown can't hang.
                FlushAsync().Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
                // Ignore on shutdown.
            }

            _flushGate.Dispose();
            if (_ownsHttp)
            {
                _http.Dispose();
            }
        }
    }
}
