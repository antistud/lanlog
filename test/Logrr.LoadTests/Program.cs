// Logrr.LoadTests — CLEF ingest load generator (SPEC §14).
//
// Drives a target event rate at a running Logrr server, then reports achieved
// throughput and HTTP enqueue latency percentiles against the acceptance targets.
//
//   dotnet run -c Release --project test/Logrr.LoadTests -- \
//       --url http://localhost:5199 --key lg_billing_... \
//       --rate 5000 --seconds 15 --batch 100 --workers 16
//
// The --key must be an existing ingest token; create an app + token first.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using Logrr.Contracts;

var opts = LoadOptions.Parse(args);
if (opts is null)
{
    return 1;
}

Console.WriteLine($"Logrr load test → {opts.Url}");
Console.WriteLine($"  target {opts.Rate:N0} events/sec for {opts.Seconds}s, batch {opts.Batch}, {opts.Workers} workers\n");

using var http = new HttpClient(new SocketsHttpHandler { MaxConnectionsPerServer = opts.Workers * 2 })
{
    BaseAddress = new Uri(opts.Url),
    Timeout = TimeSpan.FromSeconds(30),
};
http.DefaultRequestHeaders.Add("X-Logrr-ApiKey", opts.Key);

var gen = new EventGenerator();
var latenciesMs = new ConcurrentQueue<double>();
long accepted = 0, rejected = 0, http429 = 0, httpError = 0;

var totalEvents = (long)opts.Rate * opts.Seconds;
var totalBatches = (int)Math.Ceiling(totalEvents / (double)opts.Batch);
var interval = TimeSpan.FromSeconds(opts.Batch / (double)opts.Rate); // pace between batch sends

using var gate = new SemaphoreSlim(opts.Workers);
var inFlight = new List<Task>(totalBatches);
var runClock = Stopwatch.StartNew();

for (var i = 0; i < totalBatches; i++)
{
    // Pace to the target rate.
    var due = TimeSpan.FromTicks(interval.Ticks * i);
    var wait = due - runClock.Elapsed;
    if (wait > TimeSpan.Zero)
    {
        await Task.Delay(wait);
    }

    await gate.WaitAsync();
    var payload = gen.NextBatch(opts.Batch);
    inFlight.Add(Task.Run(async () =>
    {
        try
        {
            var sw = Stopwatch.StartNew();
            using var content = new ByteArrayContent(payload);
            content.Headers.TryAddWithoutValidation("Content-Type", "application/vnd.serilog.clef");
            using var resp = await http.PostAsync("/api/events/raw", content);
            sw.Stop();
            latenciesMs.Enqueue(sw.Elapsed.TotalMilliseconds);

            if ((int)resp.StatusCode == 429)
            {
                Interlocked.Add(ref http429, opts.Batch);
            }
            else if (!resp.IsSuccessStatusCode)
            {
                Interlocked.Add(ref httpError, opts.Batch);
            }
            else
            {
                var result = await resp.Content.ReadFromJsonAsync<IngestResult>();
                Interlocked.Add(ref accepted, result?.Accepted ?? 0);
                Interlocked.Add(ref rejected, result?.Rejected ?? 0);
            }
        }
        catch
        {
            Interlocked.Add(ref httpError, opts.Batch);
        }
        finally
        {
            gate.Release();
        }
    }));
}

await Task.WhenAll(inFlight);
runClock.Stop();

var elapsed = runClock.Elapsed.TotalSeconds;
var lat = latenciesMs.ToArray();
Array.Sort(lat);
double Pct(double p) => lat.Length == 0 ? 0 : lat[(int)Math.Min(lat.Length - 1, Math.Round(p / 100.0 * (lat.Length - 1)))];

var achieved = accepted / elapsed;

Console.WriteLine("Results");
Console.WriteLine($"  duration            {elapsed:N1} s");
Console.WriteLine($"  accepted            {accepted:N0} events");
Console.WriteLine($"  rejected (bad line) {rejected:N0}");
Console.WriteLine($"  429 (buffer full)   {http429:N0}");
Console.WriteLine($"  transport errors    {httpError:N0}");
Console.WriteLine();
Console.WriteLine($"  sustained ingest    {achieved:N0} events/sec   {Verdict(achieved >= opts.Rate * 0.95)}  (target {opts.Rate:N0})");
Console.WriteLine($"  HTTP enqueue p50    {Pct(50):N1} ms");
Console.WriteLine($"  HTTP enqueue p99    {Pct(99):N1} ms          {Verdict(Pct(99) < 20)}  (target < 20 ms)");
Console.WriteLine($"  HTTP enqueue max    {(lat.Length == 0 ? 0 : lat[^1]):N1} ms");

return 0;

static string Verdict(bool ok) => ok ? "PASS" : "MISS";

/// <summary>Parsed command-line options.</summary>
sealed record LoadOptions(string Url, string Key, int Rate, int Seconds, int Batch, int Workers)
{
    public static LoadOptions? Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            map[args[i].TrimStart('-')] = args[i + 1];
        }

        var url = map.GetValueOrDefault("url", "http://localhost:5199");
        var key = map.GetValueOrDefault("key", "");
        if (string.IsNullOrEmpty(key))
        {
            Console.Error.WriteLine("--key <ingest token> is required. Create an app + token in Logrr first.");
            return null;
        }
        int Int(string name, int def) => int.TryParse(map.GetValueOrDefault(name), out var v) ? v : def;
        return new LoadOptions(url, key, Int("rate", 5000), Int("seconds", 15), Int("batch", 100), Int("workers", 16));
    }
}

/// <summary>Generates realistic CLEF batches with a weighted level mix and structured props.</summary>
sealed class EventGenerator
{
    private static readonly (string Mt, string Level)[] Templates =
    [
        ("Request {Method} {Path} returned {Status} in {Ms}ms", "Information"),
        ("Cache {Result} for {Key}", "Debug"),
        ("User {UserId} signed in from {Ip}", "Information"),
        ("Payment {Amount} failed for {UserId}", "Error"),
        ("Retrying {Operation} (attempt {Attempt})", "Warning"),
        ("Enter {Method}", "Verbose"),
    ];

    private readonly Random _rnd = new(20260723);
    private long _seq;

    public byte[] NextBatch(int count)
    {
        var sb = new StringBuilder(count * 160);
        for (var i = 0; i < count; i++)
        {
            var (mt, level) = Templates[_rnd.Next(Templates.Length)];
            var n = Interlocked.Increment(ref _seq);
            sb.Append('{')
              .Append("\"@t\":\"").Append(DateTimeOffset.UtcNow.ToString("o")).Append("\",")
              .Append("\"@mt\":\"").Append(mt).Append("\",")
              .Append("\"@l\":\"").Append(level).Append("\",")
              .Append("\"UserId\":").Append(1000 + _rnd.Next(500)).Append(',')
              .Append("\"Status\":").Append(_rnd.Next(2) == 0 ? 200 : 500).Append(',')
              .Append("\"Ms\":").Append(_rnd.Next(500)).Append(',')
              .Append("\"Seq\":").Append(n);
            if (level == "Error")
            {
                sb.Append(",\"@x\":\"System.TimeoutException: the operation timed out\\n  at Worker.Run()\"");
            }
            sb.Append('}');
            if (i < count - 1)
            {
                sb.Append('\n');
            }
        }
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
