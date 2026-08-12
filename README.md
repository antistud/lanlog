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

**Windows (PowerShell):**

```powershell
$env:LOGRR_DATA_PATH = "C:\logrr-data"
$env:ASPNETCORE_URLS = "http://localhost:5199"
dotnet run --project src/Logrr.Server --no-launch-profile
```

**Linux / macOS (bash):**

```bash
LOGRR_DATA_PATH=/tmp/logrr ASPNETCORE_URLS=http://localhost:5199 \
  dotnet run --project src/Logrr.Server
```

`--no-launch-profile` makes `ASPNETCORE_URLS` win over any local `launchSettings.json`.

On first run it creates the data directory, seeds an `admin` account, and writes the
password to `FIRST-RUN-CREDENTIALS.txt` **in the data directory** — the path in
`LOGRR_DATA_PATH` above, or `C:\Logrr` (Windows) / `./data` (elsewhere) if you
don't set it. Sign in as `admin` with that password.

To stop typing passwords, link accounts to Windows identities and turn on
`Logrr:Auth:Windows:Enabled` — domain users are then signed in automatically. Setup and the
`/login?local=1` escape hatch are in [`docs/SETUP.md`](docs/SETUP.md) §8.

Send a log line (Seq-compatible CLEF) once you've created an app + token in the UI:

```bash
curl -X POST http://localhost:5199/api/events/raw \
  -H "X-Logrr-ApiKey: lg_billing_..." \
  -H "Content-Type: application/vnd.serilog.clef" \
  --data-binary '{"@t":"2026-07-23T14:02:11Z","@mt":"Payment {Amount} failed","@l":"Error","Amount":49.99}'
```

## Publish for IIS

```bash
dotnet publish src/Logrr.Server -c Release /p:PublishProfile=IIS
```

Output lands in `src/Logrr.Server/bin/Release/net10.0/publish/` — self-contained win-x64, so
**no .NET runtime install is required on the server**. Deploy *that* folder, never the
neighbouring `bin/Release/net10.0/win-x64/`: it has `Logrr.exe` and `web.config` but no
`wwwroot`, so the site starts and every stylesheet and script 404s.

On the server, `scripts/deploy-iis.ps1` does the rest (app pool, permissions, web.config,
binding) and is safe to re-run:

```powershell
.\scripts\deploy-iis.ps1 -Port 5443 -HostHeader logrr.internal
```

Full walkthrough in [`docs/SETUP.md`](docs/SETUP.md).

## Storing events in SQL Server (optional)

The default is SQLite files under the data directory, and for a single box that's the intended
option, not a lesser one. Point Logrr at SQL Server if you already back up, monitor and cluster
one and want the log data inside that perimeter:

```json
"Logrr": {
  "Storage": {
    "ConnectionString": "Server=SQL01;Database=Logrr;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;",
    "Schema": "logrr"
  }
}
```

`LOGRR_SQL_CONNECTION` overrides it — tidier, since it keeps a credential out of the published
folder — and `ConnectionStrings:Logrr` is read too. Empty means stay on SQLite. Create an empty
database and give the app pool identity `db_owner` on it (or `db_ddladmin` + `db_datareader` +
`db_datawriter`); Logrr creates its own schema and tables on first start. `Encrypt` defaults to
true in the current client, so an internal server with a self-signed certificate needs
`TrustServerCertificate=true` or the login fails outright.

Every start names the backend in `logrr-internal.log`, which is the fastest way to tell a
configuration problem from a connectivity one:

```
Logrr storage backend: SQL Server SQL01, database Logrr, schema logrr
```

The partition model carries over — an app-day is a table (`[logrr].[events_{app}_{yyyyMMdd}]`)
instead of a file, and retention still drops it whole. Two things change: **full-text search
becomes a `LIKE` scan inside the day's partition** (every term must appear, no stemming, so
`connect` won't find `connection`), and the free-disk guard no longer applies — per-app age and
size caps still do. The **data directory is still required** either way: the Data Protection
`keys\` folder and the internal log live there, and losing `keys\` still makes destination
secrets unrecoverable.

Setup steps in [`docs/SETUP.md`](docs/SETUP.md) §10, design and the full backend comparison in
[`docs/SPEC.md`](docs/SPEC.md) §4.7.

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

## Following one request across apps

Any event that carries a trace id gets a clickable trace id — in live tail, in search, and on
event detail. It opens `/traces/{traceId}`: every event under that id, **across every app you
can read**, oldest first, as a waterfall you can read a request off.

Nothing to configure. If your apps already flow a trace id — ASP.NET Core's `Activity` does
this by default, and `Serilog.Sinks.Seq`/`Logrr.Client` send it as CLEF `@tr` — the web tier's
and the worker's halves of the same request already line up on one screen.

Apps that also emit spans (`@ps` parent span, `@st` span start — what `SerilogTracing` writes)
get the extra dimension: rows nest inside the span they were logged in, and each span is drawn
at its real offset and duration. Apps that emit only a trace id get the same screen, flat.

```bash
curl "http://localhost:5199/api/v1/traces/4bf92f3577b34da6" -H "X-Logrr-ApiKey: lg_billing_..."
```

A token sees only its own app; a signed-in browser session sees every app. Add `?appId=web` to
narrow it, and `?near=2026-07-23T14:02:11Z` to point the scan at the right day.

## Collecting Windows event logs

Logrr can pull the Application/System/Security logs from this box and any other Windows
machine on the LAN — **without installing anything on them**. The server reads their event
logs over RPC, so there is still no agent to deploy.

Turn it on in **Admin → Windows events** and add the machines there — `.` for this server, a
name or FQDN for anything else. It is ordinary settings in the database, not a config file: a
save applies on the next poll, with no restart.

Each machine's events land in their own app, grouped one event type per Windows event id, so
notification rules and tickets work on them exactly as they do on application logs. Collection
starts at the tail of each log and resumes from where it left off after a restart, and the
status table shows what each channel last read and why it failed if it did. The app pool needs
an identity that can read the remote logs — setup and the permissions that catch people out are
in [`docs/SETUP.md`](docs/SETUP.md) §9, design in [`docs/SPEC.md`](docs/SPEC.md) §6.4.

**VB.NET:** worked, compiled examples live in [`clients/vb/`](clients/vb/README.md) —
direct client, `Microsoft.Extensions.Logging`, ASP.NET Framework `Global.asax`, WinForms,
and the VB-specific traps (message templates vs interpolation, root-namespace collisions,
no `Async Sub Main`). Run them with `dotnet run --project clients/vb`.

## Implementation status

**Phase 1 (usable) is implemented and tested end-to-end**, plus much of Phase 2's
backend: CLEF + plain-JSON ingest with token auth, partitioned SQLite storage with
retention and the global disk guard, the filter expression language with **both
compilation backends property-tested for exact agreement**, cursor-paged + full-text
query, the realtime broker with frame batching and honest backpressure, the rule engine
with dedupe/threshold/cooldown and every §10.7 safety valve, the durable delivery queue
with backoff/circuit-breaker/HMAC, first-run bootstrap, and the IIS publish profile.

The Blazor UI implements all §9 screens — sign-in, apps overview (live tiles with
sparklines), live tail, search, event detail, cross-app trace view, app settings, tokens, destinations (with
template editor + live preview + test), rules, deliveries, and admin — on a theme-aware
(light + dark) design system in `wwwroot/css/logrr.css`. See `docs/SPEC.md` §15 for the
full phase plan.
