# Logrr

A self-hosted, structured log server for .NET/IIS shops: xcopy-deploy to an existing
Windows box, watch logs live, and raise tickets automatically. Local SQLite storage, no
runtime install, no external database, no agent.

Full design in [`docs/SPEC.md`](docs/SPEC.md); IIS install in [`docs/SETUP.md`](docs/SETUP.md).

## Solution layout

| Project | Responsibility |
|---|---|
| `Logrr.Contracts` | wire DTOs shared with clients |
| `Logrr.Core` | domain model, level mapping, CLEF parsing, templating, **filter compiler** |
| `Logrr.Storage` | partitioned SQLite, batch writer, cursor reader, control DB, retention |
| `Logrr.Realtime` | broker, subscriptions, frame batching, backpressure |
| `Logrr.Notify` | rule engine, dedupe/threshold/cooldown, delivery queue, dispatcher |
| `Logrr.Server` | ASP.NET Core host, endpoints, SignalR hub, Blazor UI, bootstrap |
| `Logrr.Client` | NuGet client — batching ingest client + `Microsoft.Extensions.Logging` provider (`netstandard2.0`) |

## Build & test

Requires the **.NET 10 SDK**.

```bash
dotnet build Logrr.slnx
dotnet test test/Logrr.Tests/Logrr.Tests.csproj
```

## Run locally

```bash
LOGRR_DATA_PATH=/tmp/logrr ASPNETCORE_URLS=http://localhost:5199 \
  dotnet run --project src/Logrr.Server
```

On first run it creates the data directory, seeds an `admin` account, and writes the
password to `FIRST-RUN-CREDENTIALS.txt` in the data path.

Send a log line (Seq-compatible CLEF) once you've created an app + token in the UI:

```bash
curl -X POST http://localhost:5199/api/events/raw \
  -H "X-Logrr-ApiKey: lg_billing_..." \
  -H "Content-Type: application/vnd.serilog.clef" \
  --data-binary '{"@t":"2026-07-23T14:02:11Z","@mt":"Payment {Amount} failed","@l":"Error","Amount":49.99}'
```

## Ingesting from other projects

Add the `Logrr.Client` package and wire it into any `Microsoft.Extensions.Logging` app:

```csharp
builder.Logging.AddLogrr("https://logrr.internal", "lg_billing_...");
```

Message templates and structured properties are preserved end to end. It batches off the
calling thread and is fire-and-forget (a Logrr outage never blocks your app). There's also
a direct `LogrrClient` for apps not using MEL. See `src/Logrr.Client/README.md`. If you
already use `Serilog.Sinks.Seq`, you can point it at Logrr instead and skip the package.

Build the package with `dotnet pack src/Logrr.Client -c Release`.

## Implementation status

**Phase 1 (usable) is implemented and tested end-to-end**, plus much of Phase 2's
backend: CLEF + plain-JSON ingest with token auth, partitioned SQLite storage with
retention and the global disk guard, the filter expression language with **both
compilation backends property-tested for exact agreement**, cursor-paged + full-text
query, the realtime broker with frame batching and honest backpressure, the rule engine
with dedupe/threshold/cooldown and every §10.7 safety valve, the durable delivery queue
with backoff/circuit-breaker/HMAC, first-run bootstrap, and the IIS publish profile.

The Blazor UI implements all §9 screens — sign-in, apps overview (live tiles with
sparklines), live tail, search, event detail, app settings, tokens, destinations (with
template editor + live preview + test), rules, deliveries, and admin — on a theme-aware
(light + dark) design system in `wwwroot/css/logrr.css`. See `docs/SPEC.md` §15 for the
full phase plan.
