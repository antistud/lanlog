# Logrr.Client

Ship structured logs to a [Logrr](https://github.com/antistud/lanlog) server from any .NET
app. Targets `netstandard2.0`, so it runs on .NET Framework 4.6.1+ through .NET 10.

## Install

```bash
dotnet add package Logrr.Client
```

## Use it with Microsoft.Extensions.Logging (ASP.NET Core, Worker, Generic Host)

One line in your logging setup:

```csharp
builder.Logging.AddLogrr("https://logrr.internal", "lg_billing_7Kq2xR9mNp4vB1sT6wZaLd");
```

Message templates and structured properties are preserved end to end:

```csharp
logger.LogError(ex, "Payment {Amount} failed for {UserId}", 49.99, 1042);
// → template, Amount, UserId, and the exception all arrive as first-class fields.
```

Full options:

```csharp
builder.Logging.AddLogrr(o =>
{
    o.Endpoint = "https://logrr.internal";
    o.ApiKey = "lg_billing_...";
    o.MinimumLevel = LogrrLevel.Information;
    o.BatchSizeLimit = 500;
    o.FlushInterval = TimeSpan.FromSeconds(2);
});
```

## Use it directly (no logging framework)

```csharp
using var client = new LogrrClient(new LogrrClientOptions
{
    Endpoint = "https://logrr.internal",
    ApiKey = "lg_billing_...",
});

client.Log(LogrrLevel.Warning, "Cache miss for {Key}", properties:
    new Dictionary<string, object?> { ["Key"] = "user:1042" });
```

## Behaviour

- **Batching, off-thread.** Events are buffered and flushed on a size (500) or time (2s)
  trigger — `Emit`/`Log` return immediately.
- **Fire-and-forget.** A Logrr outage delays and (past `QueueLimit`) drops events; it never
  blocks or throws into your app. `DroppedCount` exposes how many were shed.
- **Seq-compatible wire format.** Posts newline-delimited CLEF to `/api/events/raw`. If you
  already use `Serilog.Sinks.Seq`, you can point it at Logrr instead and skip this package
  entirely — this is the convenience option, not a requirement.

Dispose the client (or the host) on shutdown for a best-effort final flush.
