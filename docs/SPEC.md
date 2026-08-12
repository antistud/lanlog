# Logrr — Technical Specification
> Status: draft v0.2 — pre-implementation.
> Changes from v0.1: named `Logrr`; realtime promoted to a first-class subsystem;
> webhook/ticketing subsystem added.

---

## 1. Goals

A self-hosted log server that a .NET shop can stand up on an existing Windows box in
under ten minutes, watch live, and wire into their ticketing system.

**Hard requirements**

1. **Xcopy deploy to IIS.** Unzip, point a site at the folder, browse it. No runtime
   install, no external database, no separate agent, no service registration.
2. **Local storage only.** SQLite on disk. No network dependencies at runtime.
3. **Token-authenticated ingest.** One or more tokens, each bound to exactly one app.
4. **Multi-app.** Apps are first-class: separate storage, retention, tokens, views.
5. **Structured logs.** Message template + properties preserved, not flat strings.
6. **Realtime throughout.** Live tail, live counters, live health — sub-second from
   ingest to screen, with backpressure that degrades gracefully instead of melting.
7. **Outbound ticketing.** Any event can become a ticket manually; rules can raise
   tickets automatically, with deduplication and thresholds so one bad deploy makes
   one ticket rather than four thousand.

**Explicit non-goals (v1)**

- Metrics, traces, APM, dashboard-building. This is a log store, not Grafana.
- Clustering, replication, HA. One box, one process.
- Inbound ticket status sync. Logrr pushes; it does not poll Jira for resolution state.
- Cross-app correlation queries.
- Email/SMS notification channels — webhook only. Everything else has a webhook bridge.

---

## 2. Deployment model

Published as **self-contained, win-x64, single-folder** ASP.NET Core:

```
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false
```

IIS hosting via ANCM **in-process**:

- Application pool: **No Managed Code**, `ApplicationPoolIdentity`.
- App pool **AlwaysRunning** + site preload enabled. Idle timeout `0`, regular time
  interval `0`. Without this, ingest drops on idle recycle and live tail dies silently.
- **Install the IIS `WebSocket Protocol` role feature.** It is not present by default on
  Windows Server. Without it the realtime layer falls back to SSE/long-polling — which
  works, but with worse latency and more connections. Setup doc must call this out.
- `ApplicationPoolIdentity` needs Modify on the data directory only. App folder stays
  read-only.

**Directory layout on disk**

```
C:\inetpub\logrr\             (app, read-only)
  Logrr.exe
  web.config
  appsettings.json
  wwwroot\
C:\Logrr\                     (data, writable — configurable)
  control.db                  apps, tokens, users, rules, destinations, deliveries
  keys\                       ASP.NET Data Protection keys
  apps\
    {appId}\
      events-20260723.db
      events-20260722.db
  logrr-internal.log           own diagnostics, rolling file
```

Data root resolution: `LOGRR_DATA_PATH` env var → `appsettings.json` →
`C:\Logrr` (the system drive root, so a D:-booted box lands on `D:\Logrr`).
**Never** default inside the app folder — a redeploy would robocopy over the logs.

**First run bootstrap.** If `control.db` is missing: create schema, seed an `admin`
account with a generated password, write it to `{data}\FIRST-RUN-CREDENTIALS.txt` and
to the internal log, force a change on first sign-in, delete the file afterwards.

---

## 3. Solution layout

```
Logrr.sln
  src/
    Logrr.Server/        ASP.NET Core host, endpoints, SignalR hubs, Blazor UI
    Logrr.Core/          domain, filter expression compiler, templating
    Logrr.Storage/       SQLite partition manager, batch writer, reader
    Logrr.Realtime/      broker, subscriptions, frame batching
    Logrr.Notify/        rule engine, webhook dispatcher, delivery queue
    Logrr.Contracts/     DTOs shared with clients
  test/
    Logrr.Tests/
    Logrr.LoadTests/
  docs/
    SPEC.md
```

.NET 10. Central Package Management via `Directory.Packages.props`.

**Dependencies, deliberately short:** `Microsoft.Data.Sqlite`, `Serilog` (server's own
logging), `System.IO.Hashing`, `Microsoft.AspNetCore.SignalR` (in-box). No EF Core —
the write path is hot, the schema is fixed, and partitions are created from a known DDL
string rather than migrated. No templating library; the token substituter is ~150 lines
and avoids a Handlebars dependency with its own escaping semantics.

---

## 4. Storage design

### 4.1 Partitioning

One SQLite file per **app per UTC day**: `apps/{appId}/events-{yyyyMMdd}.db`.

- Retention is `File.Delete` — O(1), no `VACUUM`, no long lock, no bloat.
- Write contention is per-app. A chatty app can't stall a quiet one.
- Backup is "copy files older than today."
- Corruption costs one app-day, not the store.

Trade-off: multi-day queries touch multiple files. Mitigated by opening partitions
newest-first and stopping once the page is filled.

### 4.2 Connection settings

```
PRAGMA journal_mode = WAL;
PRAGMA synchronous = NORMAL;
PRAGMA busy_timeout = 5000;
PRAGMA temp_store = MEMORY;
PRAGMA cache_size = -8000;
```

`synchronous = NORMAL` under WAL can lose the last few transactions on hard power loss.
Correct trade for logs; document it.

Exactly **one writer connection per open partition**, owned by the writer service.
Readers pooled, opened read-only.

### 4.3 Event schema (per partition)

```sql
CREATE TABLE events (
  id          INTEGER PRIMARY KEY,   -- rowid, monotonic within partition
  ts          INTEGER NOT NULL,      -- unix micros UTC
  level       INTEGER NOT NULL,      -- 0 Verbose .. 5 Fatal
  template    TEXT,                  -- @mt
  message     TEXT NOT NULL,         -- rendered, @m
  exception   TEXT,                  -- @x
  event_type  INTEGER,               -- template hash, for grouping + dedupe
  trace_id    TEXT,
  span_id     TEXT,
  source      TEXT,
  machine     TEXT,
  properties  TEXT                   -- JSON object
);
CREATE INDEX ix_events_ts       ON events(ts DESC);
CREATE INDEX ix_events_level_ts ON events(level, ts DESC);
CREATE INDEX ix_events_type_ts  ON events(event_type, ts DESC);
CREATE INDEX ix_events_trace    ON events(trace_id) WHERE trace_id IS NOT NULL;
CREATE VIRTUAL TABLE events_fts USING fts5(
  message, exception,
  content = 'events', content_rowid = 'id',
  tokenize = 'porter unicode61'
);
```

FTS5 ships in the bundled `e_sqlite3` native library. The batch writer maintains the FTS
index explicitly (one insert per batch), not by trigger.

**Indexed properties.** Per-app config promotes property names to generated columns
(`json_extract(properties,'$.TenantId')`) with an index, applied at partition creation
from the app's config snapshot. Changing the list affects tomorrow's partition.

### 4.4 Control database

```sql
CREATE TABLE apps (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, description TEXT,
  retention_days INTEGER NOT NULL, max_size_mb INTEGER NOT NULL,
  minimum_level INTEGER NOT NULL, indexed_properties TEXT,
  is_enabled INTEGER NOT NULL, created_utc INTEGER NOT NULL
);
CREATE TABLE tokens (
  id TEXT PRIMARY KEY, app_id TEXT NOT NULL REFERENCES apps(id),
  name TEXT, prefix TEXT NOT NULL UNIQUE, hash BLOB NOT NULL,
  scopes INTEGER NOT NULL, expires_utc INTEGER, last_used_utc INTEGER,
  revoked_utc INTEGER, created_utc INTEGER NOT NULL
);
CREATE TABLE users (
  id TEXT PRIMARY KEY, username TEXT NOT NULL UNIQUE,
  password_hash BLOB, password_salt BLOB, role INTEGER NOT NULL,
  must_change_password INTEGER NOT NULL, app_access TEXT, created_utc INTEGER NOT NULL,
  windows_account TEXT                  -- §11; NULL = password-only account
);
CREATE UNIQUE INDEX ux_users_windows_account
  ON users(windows_account COLLATE NOCASE) WHERE windows_account IS NOT NULL;
CREATE TABLE destinations (...);        -- §10.1
CREATE TABLE rules (...);               -- §10.3
CREATE TABLE rule_occurrences (...);    -- §10.4
CREATE TABLE deliveries (...);          -- §10.5
CREATE TABLE ticket_links (...);        -- §10.6
CREATE TABLE winlog_settings (...);     -- §6.4; single row, the collector's knobs
CREATE TABLE winlog_sources (...);      -- §6.4; one row per collected machine, unique on machine
CREATE TABLE winlog_cursors (...);      -- §6.4; high-water mark per (machine, channel)
```

Control DB is small and low-traffic; a simple versioned migration runner (integer
`user_version` pragma, ordered SQL scripts) is fine here.

### 4.5 Write path

```
HTTP ingest → parse & validate → bounded Channel<LogEvent> (per app)
            → BatchWriter → SQLite transaction → RealtimeBroker.Publish(batch)
                                               → RuleEngine.Evaluate(batch)
```

- Channel capacity 20,000 events/app. Full → HTTP **429** with `Retry-After: 1` and a
  dropped-event counter. Never block the request thread on disk.
- Flush on **500 events or 500 ms**, whichever first, one transaction, prepared insert.
- Realtime fan-out and rule evaluation happen **after commit**, off the writer thread,
  so a slow subscriber or webhook can never stall ingest.
- Midnight rollover keyed off event timestamp, not wall clock, with a 1-hour grace
  window; later arrivals clamp to the current partition and get `_lateArrival: true`.

### 4.6 Retention

Hourly maintenance loop:

- Delete partitions older than the app's `RetentionDays`.
- If app size exceeds `MaxSizeMb`, delete oldest until under.
- If volume free space drops below `MinFreeDiskMb` (global), delete oldest partitions
  across all apps and raise a visible UI warning.

The global disk guard is non-negotiable — a log server that fills the system drive of a
production IIS box is worse than no log server.

### 4.7 Optional SQL Server backend

Everything above describes the default. Setting `Logrr:Storage:ConnectionString` (or
`LOGRR_SQL_CONNECTION`) moves both the control tables and the event partitions into SQL
Server instead. Leaving it empty — the default — keeps the zero-dependency SQLite layout,
which is the point of the product; the SQL Server option exists for shops that already back
up, monitor and HA a database server and want Logrr's data inside that perimeter.

**The partition model carries over unchanged.** An app-day is a *table* rather than a file:
`[logrr].[events_{app}_{yyyyMMdd}]`, with the app id's hyphens mapped to underscores (a slug
can never contain an underscore, so two apps cannot collide). Consequences:

- Retention still drops a partition whole — `DROP TABLE`, a metadata operation, not a mass
  delete that bloats the transaction log.
- A query still narrows to one day of one app before it filters.
- `PartitionDays` is a catalog query instead of a directory listing; partition size comes
  from `sys.allocation_units` instead of a file length.

Row ids are assigned by the writer, not by an identity column, so the composite event id
(`{yyyyMMdd}:{rowid}`) and cursor paging behave identically on both backends.

**Deliberate differences**, none of which change a query's results:

| | SQLite | SQL Server |
|---|---|---|
| Text search | FTS5 `MATCH`, with stemming | `LIKE` scan inside the day's partition; every whitespace-separated term must appear, no stemming |
| Indexed properties | expression index on `json_extract` | persisted computed column `CAST(JSON_VALUE(…) AS NVARCHAR(400))` plus an index on it |
| Batch insert | prepared per-row insert in one transaction | multi-row `INSERT … VALUES` chunked under the 2100-parameter cap, one transaction |
| Upserts | `ON CONFLICT DO UPDATE` | `UPDATE … WITH (UPDLOCK, SERIALIZABLE)` then conditional `INSERT` |
| Free-space guard | applies | not applicable — the storage is the server's to manage; the per-app age and size caps still apply |

Full-text search is the one real capability loss. SQL Server full-text would need a catalog
per partition table and populates asynchronously, so a just-written event would not be
findable — the wrong trade for a log tail.

**The filter compiler emits a different SQL flavour per backend** (§7.1). This is not
cosmetic: SQL Server agrees with the in-memory predicate on none of the defaults that
matter, so the emitter asks for each explicitly — numbers cast out of `JSON_VALUE`'s
nvarchar with `TRY_CAST`, comparisons forced to `Latin1_General_BIN2` to match
`string.CompareOrdinal`, `LIKE` forced to a case-insensitive collation, and `[` escaped
because SQL Server reads it as a character class and SQLite does not. The backend-agreement
test runs the same corpus against both.

**The data path is still required** with SQL Server: the Data Protection key ring and the
internal log live there. §11's warning about losing `keys\` is unchanged.

**`InvariantGlobalization` must stay `false`.** `Microsoft.Data.SqlClient` throws
"Globalization Invariant Mode is not supported" from `SqlConnection.Open()`, so an invariant
build would ship a working SQLite backend and a SQL Server backend that dies on first use.

---

## 5. Domain model

### 5.1 App

| Field | Notes |
|---|---|
| `Id` | slug, `[a-z0-9-]{3,32}`, used as folder name |
| `Name` / `Description` | display |
| `RetentionDays` | default 14 |
| `MaxSizeMb` | default 2048 |
| `MinimumLevel` | server-side floor; below is discarded at ingest |
| `IndexedProperties` | string[], max 8 |
| `IsEnabled` | disabled → ingest 403, data retained |

### 5.2 Token

| Field | Notes |
|---|---|
| `AppId` | a token belongs to exactly one app |
| `Prefix` | first 8 chars of secret, plaintext, for lookup |
| `Hash` | SHA-256 of full secret |
| `Scopes` | flags: `Ingest`, `Read` |
| `ExpiresUtc` / `RevokedUtc` | nullable |
| `LastUsedUtc` | updated at most once a minute, out of band |

**Format:** `lg_{appId}_{22 random base62}` — e.g. `lg_myapp_xxxxxxxxxxxxxxxxxxxxxx`.
The slug is a convenience for humans reading config files and is **not** trusted; lookup
is by prefix, verification by constant-time hash compare. Shown once at creation.

### 5.3 Level mapping

| Value | Serilog | MEL |
|---|---|---|
| 0 | Verbose | Trace |
| 1 | Debug | Debug |
| 2 | Information | Information |
| 3 | Warning | Warning |
| 4 | Error | Error |
| 5 | Fatal | Critical |

Unrecognised level strings → Information, original preserved as `_rawLevel`.

---

## 6. Ingestion API

### 6.1 Seq-compatible endpoint (primary)

```
POST /api/events/raw?clef
X-Logrr-ApiKey: lg_myapp_xxxxxxxxxxxxxxxxxxxxxx
Content-Type: application/vnd.serilog.clef
```

Also accepts `X-Seq-ApiKey` and `Authorization: Bearer {token}`.

Newline-delimited CLEF, one JSON object per line:

```json
{"@t":"2026-07-23T14:02:11.4270000Z","@mt":"Payment {Amount} failed for {UserId}","@l":"Error","@x":"System.TimeoutException: ...","Amount":49.99,"UserId":1042,"SourceContext":"Billing.Processor"}
```

Reserved fields: `@t` timestamp, `@m` rendered message, `@mt` template, `@l` level
(absent = Information), `@x` exception, `@i` event id, `@r` renderings, `@tr` trace id,
`@sp` span id, `@ps` parent span id, `@st` span start. `@@x` unescapes to a literal `@x`
property. Everything else is a property. If `@m` is absent, render `@mt` server-side.

`@tr` and `@sp` are columns; `@ps` and `@st` are not, because the partition DDL is fixed and
never migrated (§4.3) — they land in the property bag as `_parentSpanId` and `_spanStart`
alongside the other server-side markers. That is enough for the trace view (§9) to nest spans
and measure them: an event carrying `@st` *is* a completed span, and its duration is
`@t - @st`. An app that propagates only a trace id still gets a trace, just a flat one.

**Why this shape:** it is the wire format `Serilog.Sinks.Seq` already emits. Existing
apps point at Logrr by changing a URL and a key — no client package to build, version,
or support. That one decision deletes most of the client-side surface area.

| Code | Meaning |
|---|---|
| 201 | accepted (all or some) |
| 400 | envelope unparseable |
| 401 | missing/invalid token |
| 403 | app disabled or token lacks `Ingest` |
| 413 | over `MaxRequestBytes` |
| 429 | ingest buffer full |

Success body reports partial failures rather than rejecting the batch:

```json
{ "accepted": 48, "rejected": 2, "errors": ["line 12: invalid @t", "line 30: event exceeds 256 KB"] }
```

**Never** fail a whole batch over one bad line. An endpoint that discards 500 good events
because line 12 had a malformed timestamp will destroy data during exactly the incident
you needed it for.

### 6.2 Plain JSON endpoint

For legacy VB.NET / WinForms / anything-with-an-HTTP-client:

```
POST /api/v1/events
X-Logrr-ApiKey: {token}
[{ "timestamp": "2026-07-23T14:02:11Z", "level": "Error",
   "message": "Payment failed for user 1042", "exception": "...",
   "properties": { "UserId": 1042, "Amount": 49.99 } }]
```

Single object or array. `timestamp` optional → server clock. Deliberately dumb so it can
be called from a 15-line VB.NET helper.

### 6.3 Limits

| Limit | Default |
|---|---|
| Max request body | 10 MB |
| Max single event | 256 KB (message truncated, `_truncated: true`) |
| Max properties per event | 100 |
| Max property name length | 128 |
| Max nesting depth | 8 |
| Timestamp skew accepted | −30 days .. +1 hour |

### 6.4 Windows Event Log collection

The one ingestion path with no client at the other end. The server reads the Windows Event
Log itself — locally, and over RPC for other machines — so collected boxes need nothing
installed. That keeps the "no agent" promise in §1 literally true rather than merely
true-for-your-own-apps, and it is the reason this is a server feature and not a shipper.

Off by default. Machines are configured in the admin UI and stored in `winlog_settings` and
`winlog_sources` (§4.4), one row per machine, applied without a restart. `Logrr:WindowsEvents`
(§12) seeds those rows on the first run that finds them empty and is ignored afterwards.

One row per machine is enforced: the cursors below are keyed on (machine, channel), so a second
row for the same machine would share them and silently collect nothing.

**Mapping.** A record becomes an ordinary event, so search, filters, rules and tickets all
work on it unchanged:

| Windows | Logrr |
|---|---|
| `Level` 1..5 | `Level` — inverted (1 Critical → Fatal, 5 Verbose → Verbose); 0 LogAlways → Information |
| `Channel` / `Provider` / `EventId` | `Template`, as the literal text `[System/Service Control Manager 7031]` |
| `FormatDescription()` | `Message` |
| `ProviderName` | `Source` |
| `MachineName` | `Machine` |
| `ActivityId` | `TraceId` |
| record metadata | properties `Channel`, `ProviderName`, `EventId`, `RecordId`, `UserId`, `Task`, `Opcode`, `Keywords`, `Data0..n` |

The template carries the event's *identity*, not its text, because `event_type` is a hash of
the template (§4.3). Putting the description there would give every occurrence its own group;
sharing one placeholder template would collapse the whole event log into a single group. As
written, one Windows event id is one group — so dedupe, thresholds and cooldown (§10.4)
behave the same as they do for an application exception.

Publisher message resources are frequently unavailable when reading a remote machine, so a
record whose description will not render still produces a readable message synthesised from
its raw data rather than an empty one.

**Cursors.** A high-water mark per (machine, channel) in `winlog_cursors`, keyed on
`EventRecordID`. A restart resumes exactly where it left off. Clearing a log restarts
`EventRecordID` at 1, which would otherwise strand the cursor above every future record; an
empty read triggers a check of the newest id and resets the cursor when it has gone backwards.
Removing or renaming a machine in the UI deletes its cursors, so re-adding it later starts
cleanly rather than resuming a record id from months ago; a single channel can also be reset
by hand when its cursor has run ahead of the log — after a restore from backup, say.

**First run** starts at the tail. An event log holds months of history, and importing it
wholesale would bury the app and mostly be discarded by the −30 day skew bound anyway. Set an
initial backfill to pull a window instead (clamped to 720 h for the same reason).

**Filtering** is the app's `MinimumLevel` and nothing new: the collector translates it into a
`Level` ceiling in the event log query, so records below the floor are never read off the wire
rather than being fetched and discarded. Raising the floor in the UI narrows collection.

**Failure is per target.** An unreachable machine logs once, drops to debug while it stays
down, and logs again on recovery; its cursor does not move and the other machines are
unaffected. Nothing here can fail an ingest request — there is no request.

---

## 7. Query API

Read endpoints accept a `Read`-scoped token or a UI session cookie.

```
GET /api/v1/apps/{appId}/events
      ?from=…&to=…&level=Warning&q=timeout&filter=UserId = 1042
      &cursor={opaque}&limit=100
```

```json
{
  "events": [ { "id": "…", "timestamp": "…", "level": "Error", "message": "…",
                "template": "…", "exception": null, "traceId": null, "properties": {} } ],
  "nextCursor": "eyJwIjoiMjAyNjA3MjMiLCJpIjo0OTIxfQ",
  "partitionsScanned": 2
}
```

Cursor encodes `{partition date, last rowid}` — stable under concurrent writes because
rowids are monotonic and partitions are date-bounded. No `OFFSET` anywhere.

```
GET  /api/v1/apps                      list
GET  /api/v1/apps/{id}/stats           counts by level, by hour, storage size
GET  /api/v1/apps/{id}/events/{id}     single event, full detail
GET  /api/v1/traces/{traceId}          whole trace, oldest first, across apps
      ?appId=…&near=…&limit=500
GET  /api/v1/apps/{id}/stream          SSE tail (token auth, for CLI consumers)
GET  /health                           liveness, queue depth, disk free
GET  /api/v1/buildinfo                 version, commit, build date
```

### 7.1 Filter expression

```
expr       := term (('and' | 'or') term)*
term       := '(' expr ')' | 'not' term | comparison
comparison := ident op value
op         := '=' | '!=' | '>' | '>=' | '<' | '<=' | 'like' | 'is null' | 'is not null'
ident      := builtin | <property> | 'Properties.' <property>
builtin    := 'Level' | 'Message' | 'Exception' | 'Source' | 'TraceId' | 'SpanId' | 'Machine'
value      := string | number | bool | level-name
```

```
Level >= Warning and UserId = 1042
Message like '%timeout%' and not Source = 'HealthCheck'
Exception is not null
Properties.Machine = 'GIT4A'
```

A bare identifier is a built-in when it names one and a property otherwise. That is terse
for the common case but ambiguous when an event carries a property whose name collides with
a built-in — `Machine = 'x'` then means the column, matches nothing, and gives no hint why.
The `Properties.` qualifier names the property unambiguously. Click-to-filter in the UI
emits it automatically for colliding names (`FilterIdent.ForProperty`), so what the user
clicked is what the filter means.

**Two compilation targets, one parser.** This is the load-bearing design decision of the
whole project:

- `FilterExpression.ToSql()` → parameterised SQL for historical queries.
- `FilterExpression.Compile()` → `Func<LogEvent, bool>` for realtime subscriptions and
  rule evaluation, where there is no SQLite involved.

The two backends must agree exactly. Property-test them against each other over generated
events — a rule that fires in the live stream but can't be reproduced by the same filter
in search is a maddening bug to chase.

Reject anything that doesn't parse. Never fall through to string concatenation.

Not a Seq-style language with aggregates. If someone needs `group by`, they can copy the
partition file and query it in `sqlite3` — worth advertising as a feature of the format.

---

## 8. Realtime subsystem

### 8.1 Pipeline

```
BatchWriter (post-commit)
   └─► RealtimeBroker.Publish(appId, batch)
         └─► for each matching Subscription:
               predicate filter → per-sub ring buffer → frame batcher (250 ms)
                 └─► SignalR hub → browser
```

Entirely in-process. No message bus, no Redis, no backplane — single node by design.

### 8.2 Subscriptions

Client calls the hub:

```jsonc
// → Subscribe
{ "appId": "billing", "minLevel": "Debug", "filter": "TenantId = 42", "maxEventsPerSecond": 200 }
// ← returns subscriptionId
```

Server compiles `filter` once per subscription into a predicate and evaluates it in
memory. **Filtering happens server-side** — a tail filtered to errors must not ship
every debug event to the browser and hide it with CSS.

Hub methods: `Subscribe`, `Unsubscribe`, `UpdateFilter(id, filter)`, `Pause(id)`,
`Resume(id)`. Server→client: `OnFrame`.

### 8.3 Frames, not events

The single most important rule: **never push one message per log event.** A 5,000/sec
app would generate 5,000 SignalR messages per second and a browser tab that renders
nothing while pinning a core.

Each subscription accumulates matched events and emits a frame every **250 ms** or at
**200 events**, whichever first:

```json
{
  "type": "events",
  "subscriptionId": "s_8f21",
  "seq": 1041,
  "events": [ /* … */ ],
  "dropped": 1204,
  "rate": { "matched": 812, "total": 4930, "byLevel": { "Error": 3, "Warning": 41 } }
}
```

A separate `"type": "stats"` frame emits once per second regardless of event volume, so
sparklines and counters keep moving on a quiet app.

### 8.4 Backpressure

Per-subscription ring buffer, capacity 2,000. On overflow, **drop oldest** and increment
`dropped`. The UI must display this honestly — a "1,204 events not shown (rate limited)"
bar, not a silently incomplete stream. Nobody can read 5,000 events/sec; the correct
behaviour is to show a truthful sample and tell the user it's a sample.

Effective ceiling: 200 events/frame × 4 frames/sec = 800 events/sec/subscription
rendered. Beyond that, sampling engages.

Client-side: ring buffer capped at 5,000 rows, virtualized list, auto-pause when the user
scrolls away from the top (with a "N new events" pill to jump back). Pausing stops
rendering but keeps the subscription alive and counts what it skipped.

### 8.5 Connection management

- SignalR with WebSockets preferred; automatic fallback to SSE then long-polling if the
  IIS WebSocket feature is absent.
- Automatic reconnect with backoff. On reconnect, the client sends the last event id it
  rendered; the server backfills the gap **from SQLite** (bounded to 500 events) so a
  brief network blip doesn't leave a hole in the tail.
- Subscriptions are torn down on disconnect. Orphan sweep every 60 s.
- Hard cap: 50 concurrent subscriptions server-wide, 5 per user. Beyond that, reject with
  a clear message. This is an internal tool on a shared IIS box, not a public service.

### 8.6 What else is realtime

Realtime is not just the tail. Every one of these pushes rather than polls:

- App list — event counts, error counts, last-seen, all live.
- Per-app header — events/sec, error rate, storage used.
- Admin health — ingest queue depth, dropped events, disk free, writer lag.
- Webhook delivery queue — pending, retrying, dead-lettered counts.
- Rule activity — last fired, fires in the last hour.

---

## 9. Web UI

**Blazor with `InteractiveServer` render mode**, served from the same host. The SignalR
circuit that Blazor Server already maintains is reused for the realtime hub — one
transport, one reconnect story, no separate client build, no CORS, no WASM payload.

The one place this needs care: high-volume tail rendering must not round-trip DOM diffs
per event. The tail list is a virtualized component fed by the frame batcher, rendering
at most 4 times a second regardless of ingest rate.

**Screens**

1. **Sign in** — local username/password. Optional OIDC in Phase 3.
2. **Apps overview** — live tiles: name, events/sec sparkline, 24h error count, storage,
   last event. Red border when a rule has fired recently.
3. **Live tail (the main screen)** — per app. Level chips, time range, search box, filter
   expression box with inline validation. Pause/resume, clear, jump-to-live. Rows expand
   in place: properties table, formatted exception with stack frames, copy-as-JSON,
   "search for this event type", "create ticket".
4. **Search** — same grid, historical, cursor paging, shareable URL encoding the filter.
5. **Event detail** — full event, related events by `trace_id`, occurrence count for the
   event type, any linked tickets.
5b. **Trace** — `/traces/{traceId}`, reached by clicking a trace id anywhere it appears. One
   request end to end: every event carrying that trace id, **across apps**, as a waterfall —
   nested by span, positioned and sized by time, one row per event, each linking back to its
   detail. Scanning is bounded to the partitions around the event it was opened from.
6. **App settings** — retention, size cap, minimum level, indexed properties.
7. **Tokens** — create/name/scope/expiry/revoke; secret shown once in a modal with copy
   and an explicit "I've saved this" confirmation.
8. **Destinations** — webhook config, template editor with live preview, test button.
9. **Rules** — list with live fire counts; editor with filter box, threshold, dedupe,
   cooldown, destination, dry-run toggle.
10. **Deliveries** — outbound queue: pending, delivered, failed, dead-lettered. Request
    and response bodies inspectable. Manual retry.
11. **Admin** — users, global settings, disk, ingest metrics, internal log viewer.

Dark by default. Level colour coding identical on every surface. No client-side framework
beyond what Blazor ships with.

---

## 10. Webhooks & ticketing

Outbound only. Logrr posts JSON to a configured URL; whatever is on the other end —
Gitea, GitHub, Jira, Zendesk, Teams, an n8n flow, a custom endpoint — is that system's
problem. This keeps the integration surface at exactly one protocol.

### 10.1 Destination

```sql
CREATE TABLE destinations (
  id TEXT PRIMARY KEY, name TEXT NOT NULL, url TEXT NOT NULL,
  method TEXT NOT NULL DEFAULT 'POST', content_type TEXT NOT NULL DEFAULT 'application/json',
  headers TEXT,                      -- JSON dict
  auth_mode INTEGER NOT NULL,        -- 0 None, 1 Bearer, 2 Basic, 3 HmacSha256
  auth_secret BLOB,                  -- DPAPI/DataProtection-encrypted at rest
  auth_header_name TEXT,
  body_template TEXT NOT NULL,
  ticket_id_path TEXT, ticket_url_path TEXT,   -- e.g. '$.number', '$.html_url'
  timeout_seconds INTEGER NOT NULL DEFAULT 15,
  max_attempts INTEGER NOT NULL DEFAULT 6,
  rate_limit_per_hour INTEGER NOT NULL DEFAULT 60,
  is_enabled INTEGER NOT NULL,
  consecutive_failures INTEGER NOT NULL DEFAULT 0,
  circuit_open_until_utc INTEGER,
  created_utc INTEGER NOT NULL
);
```

Secrets encrypted at rest with ASP.NET Data Protection (keys already persisted to the
data dir). Never returned by the API — write-only fields.

**HMAC mode** signs the request body with the shared secret and sends
`X-Logrr-Signature: sha256=…` plus `X-Logrr-Timestamp`, so the receiver can verify
authenticity and reject replays.

**Presets** shipped as starter templates: Gitea Issues, GitHub Issues, Jira Cloud,
Zendesk, Slack, Microsoft Teams, Generic JSON. A preset is just a prefilled
URL/headers/body-template triple — no special-cased code paths per vendor.

### 10.2 Body templating

Simple `{{token}}` substitution, no logic beyond optional filters:

```
{{event.timestamp}} {{event.level}} {{event.message}} {{event.template}}
{{event.exception}} {{event.source}} {{event.traceId}} {{event.id}}
{{event.properties.TenantId}}
{{app.id}} {{app.name}}
{{rule.name}} {{rule.id}}
{{occurrence.count}} {{occurrence.firstSeen}} {{occurrence.lastSeen}} {{occurrence.key}}
{{link.event}}          → https://logrr.internal/apps/billing/events/…
{{link.search}}         → deep link to the filter that matched
{{user.name}}           → manual submissions only
{{title}} {{body}}      → manual submissions; operator-edited text
```

Filters: `| json` (JSON-string escape), `| truncate:200`, `| md` (markdown code fence for
exceptions), `| upper`, `| default:"n/a"`.

**Escaping default:** when `content_type` is JSON, every token is JSON-string-escaped
unless it appears outside a string literal. A stack trace with quotes and newlines must
not be able to produce invalid JSON — or worse, inject structure into the payload.
Validate the rendered body parses as JSON before dispatch; if not, fail the delivery with
a clear error rather than posting garbage.

Gitea example:

```json
{
  "title": "[{{app.name}}] {{event.message | truncate:120}}",
  "body": "**Level:** {{event.level}}\n**When:** {{event.timestamp}}\n**Occurrences:** {{occurrence.count}} since {{occurrence.firstSeen}}\n\n{{event.exception | md}}\n\n[View in Logrr]({{link.event}})",
  "labels": ["bug", "logrr"]
}
```

The destination editor shows a **live preview** rendered against the most recent real
event from a chosen app, so template mistakes surface before a rule fires at 3am.

### 10.3 Rules

```sql
CREATE TABLE rules (
  id TEXT PRIMARY KEY, name TEXT NOT NULL,
  app_id TEXT,                        -- NULL = all apps
  filter TEXT, minimum_level INTEGER NOT NULL,
  trigger_type INTEGER NOT NULL,      -- 0 EveryMatch, 1 Threshold
  threshold_count INTEGER, threshold_window_minutes INTEGER,
  dedupe_key_template TEXT NOT NULL DEFAULT '{{event.eventType}}',
  cooldown_minutes INTEGER NOT NULL DEFAULT 60,
  destination_id TEXT NOT NULL REFERENCES destinations(id),
  body_template_override TEXT,
  max_fires_per_hour INTEGER NOT NULL DEFAULT 20,
  is_dry_run INTEGER NOT NULL DEFAULT 0,
  is_enabled INTEGER NOT NULL,
  auto_disabled_reason TEXT,
  last_fired_utc INTEGER, created_utc INTEGER NOT NULL
);
```

Evaluation runs in the post-commit batch handler, same place as realtime fan-out, using
the **same compiled predicate** as the filter language. Ingest-to-webhook latency is
therefore sub-second, with no polling loop.

Example rules:

- `Level >= Error` on app `billing`, EveryMatch, dedupe by event type, 60 min cooldown.
- `Exception is not null and Source like 'Payments%'`, Threshold: 5 in 10 minutes.
- `Level >= Fatal` across all apps, EveryMatch, no cooldown.

### 10.4 Deduplication, thresholds, cooldown

The difference between a useful alerting system and one everybody mutes.

```sql
CREATE TABLE rule_occurrences (
  rule_id TEXT NOT NULL, dedupe_key TEXT NOT NULL,
  window_start_utc INTEGER NOT NULL, count INTEGER NOT NULL,
  first_seen_utc INTEGER NOT NULL, last_seen_utc INTEGER NOT NULL,
  sample_event TEXT,               -- JSON of a representative event
  last_fired_utc INTEGER, ticket_url TEXT,
  PRIMARY KEY (rule_id, dedupe_key)
);
```

- **Dedupe key** defaults to the event type (message template hash), so a thousand
  identical `NullReferenceException`s collapse to one ticket. Templatable — e.g.
  `{{event.eventType}}:{{event.properties.TenantId}}` files one per tenant.
- **Threshold** rules fire only when `count >= threshold_count` within a sliding window.
- **Cooldown** suppresses re-firing for the same key. Occurrences keep accumulating, so
  when the cooldown lapses the next ticket says "4,102 occurrences since 02:14" rather
  than pretending it's the first.
- Occurrence rows are pruned after `max(cooldown, window) × 3`.

### 10.5 Delivery queue

```sql
CREATE TABLE deliveries (
  id TEXT PRIMARY KEY, destination_id TEXT NOT NULL, rule_id TEXT, app_id TEXT,
  source INTEGER NOT NULL,          -- 0 Rule, 1 Manual, 2 Test
  created_utc INTEGER NOT NULL, attempt INTEGER NOT NULL DEFAULT 0,
  next_attempt_utc INTEGER, status INTEGER NOT NULL,  -- Pending/Delivered/Failed/DeadLettered
  request_body TEXT, response_status INTEGER, response_snippet TEXT,
  ticket_id TEXT, ticket_url TEXT, error TEXT
);
```

Durable — a webhook outage delays tickets, it does not lose them. Background dispatcher
with exponential backoff: **1 s, 5 s, 30 s, 2 m, 10 m, 1 h**, then dead-letter with UI
visibility and manual retry.

On success, extract `ticket_id` / `ticket_url` from the response via the destination's
configured JSON paths and write a ticket link.

**Circuit breaker:** after 5 consecutive failures a destination's circuit opens for
15 minutes; queued deliveries hold rather than hammer. Surfaced in the UI, not buried in
a log file.

### 10.6 Manual submission

From event detail or a selected row: **Create ticket** → choose destination → modal
prefilled with a title (rendered template) and body (message, exception, properties,
permalink), both editable → submit. Creates a `Manual` delivery through the same queue
and retry machinery.

```sql
CREATE TABLE ticket_links (
  id TEXT PRIMARY KEY, app_id TEXT NOT NULL, event_type INTEGER, dedupe_key TEXT,
  event_id TEXT, ticket_id TEXT, ticket_url TEXT, delivery_id TEXT,
  created_by TEXT, created_utc INTEGER NOT NULL
);
```

Any event whose `event_type` has a linked ticket shows a badge in the grid with a link.
That badge is most of the value here: it turns "has anyone looked at this yet?" into a
glance instead of a Slack thread.

### 10.7 Safety valves

An automated ticket creator wired to a firehose is a foot-gun. All of these ship in v1:

1. **Backfill protection.** Rules only evaluate events whose timestamp is within the last
   15 minutes. Replaying a month of archived logs must not open 4,000 tickets.
2. **Per-rule fire cap.** Exceeding `max_fires_per_hour` auto-disables the rule, records
   `auto_disabled_reason`, and raises a UI banner. Failing loud and stopped beats failing
   quiet and unbounded.
3. **Per-destination rate limit.** Hard cap on outbound requests/hour, shared across all
   rules targeting it.
4. **Storm collapse.** If a rule's match rate exceeds 100/sec, stop per-key evaluation and
   emit one "storm" delivery summarising the burst.
5. **Dry-run mode.** A rule can run for a day recording what it *would* have sent, with no
   delivery. Every new rule should start here.
6. **Test button.** Sends a synthetic event through the full template + transport path.

### 10.8 Endpoints

```
GET|POST      /api/v1/destinations
GET|PUT|DELETE /api/v1/destinations/{id}
POST          /api/v1/destinations/{id}/test
POST          /api/v1/destinations/{id}/preview      body: { template, sampleEventId }
GET|POST      /api/v1/rules
GET|PUT|DELETE /api/v1/rules/{id}
POST          /api/v1/rules/{id}/enable | /disable | /reset-cooldown
GET           /api/v1/deliveries?status=&destinationId=&from=&to=
POST          /api/v1/deliveries/{id}/retry
POST          /api/v1/tickets                        manual submission
GET           /api/v1/apps/{id}/tickets
```

All require `Admin` except ticket creation, which `User` may perform for apps they can
read.

---

## 11. Authentication & authorisation

Two separate systems, deliberately.

**Ingest/read tokens.** Middleware resolves prefix → token → app, with a `MemoryCache`
layer (30 s TTL) so the hot ingest path doesn't hit `control.db` per request. Revocation
propagates within one TTL; document that.

**UI sessions.** Cookie auth, own `users` table, PBKDF2 via `Rfc2898DeriveBytes` at
600k iterations. Roles `Admin` and `User`; `User` gets read access to an explicit app
list and may create tickets but not edit rules or destinations.

**Windows integrated sign-in.** Optional (`Logrr:Auth:Windows:Enabled`, off by default), and
deliberately *not* a second session type — it is one extra sign-in route. `/auth/windows`
challenges the Negotiate scheme, matches the resulting identity against `users.windows_account`
and then issues the same cookie the password form does, so the Blazor circuit, the SignalR hub,
roles and per-app access are all untouched by it. The `Microsoft.AspNetCore.Authentication.Negotiate`
handler covers both hosts: under IIS in-process it defers to the module's own Windows
authentication, under Kestrel it performs the handshake itself.

Mapping is explicit — an admin links each account to a Windows identity. An unrecognised identity
is refused rather than provisioned, and an account with a `windows_account` but no password hash
is Windows-only, since `/auth/login` rejects any account without a hash. Two accounts cannot claim
one identity (unique, case-insensitive, partial index).

The auto-redirect that makes sign-in invisible needs exactly one escape hatch to be safe:
`/login?local=1` always renders the password form and never redirects. Every Windows failure
path and sign-out land there, so a bad mapping or a misconfigured host can never lock out
every account.

Data Protection keys persisted to `{data}\keys` so cookies and encrypted destination
secrets survive an app pool recycle and a redeploy. **If that folder is lost, destination
secrets are unrecoverable** — call this out in the backup doc.

**Phase 3, optional:** external OIDC for the UI (issuer, client id, secret in
`appsettings.json`), with local accounts remaining available as a fallback so a
misconfigured IdP can't lock you out of your own log server.

---

## 12. Configuration

`appsettings.json`, overridable by env vars prefixed `LOGRR_`
(`LOGRR_Storage__DataPath`).

```json
{
  "Logrr": {
    "Storage": {
      "DataPath": "C:\\Logrr", "MinFreeDiskMb": 5120,
      "ConnectionString": "", "Schema": "logrr"
    },
    "Ingest": {
      "ChannelCapacity": 20000, "BatchSize": 500, "FlushIntervalMs": 500,
      "MaxRequestBytes": 10485760, "MaxEventBytes": 262144
    },
    "Realtime": {
      "FrameIntervalMs": 250, "MaxEventsPerFrame": 200,
      "SubscriptionBufferSize": 2000, "MaxSubscriptions": 50,
      "MaxSubscriptionsPerUser": 5, "ReconnectBackfillLimit": 500
    },
    "Notify": {
      "DispatcherConcurrency": 4, "RuleEvaluationMaxAgeMinutes": 15,
      "GlobalMaxDeliveriesPerHour": 500, "DeadLetterRetentionDays": 30
    },
    "Auth": { "Windows": { "Enabled": false, "AutoSignIn": true } },
    "Defaults": { "RetentionDays": 14, "MaxSizeMb": 2048 },
    "SelfLog": { "MinimumLevel": "Information", "RetainedFileCount": 7 }
  }
}
```

Runtime-changeable settings (per-app retention, rules, destinations) live in `control.db`
and are edited in the UI. Only infrastructure settings live in the file. Don't split the
same concern across both.

`Storage:ConnectionString` selects the SQL Server backend (§4.7); empty keeps SQLite. It
also reads from `LOGRR_SQL_CONNECTION` or `ConnectionStrings:Logrr`, so a deployment can
repoint the database without editing the published `appsettings.json` and a shop that keeps
every connection string in one place does not have to make an exception for this one.
`Schema` applies to the SQL Server backend only.

`WindowsEvents` (§6.4) is not in the sample because it is no longer a setting here. Which
machines to collect turned out to be operational state, not deployment topology: it is added to
and removed from while the server runs, exactly like a CORS origin or a notification rule, so
it lives in the control DB and is edited at `/admin/windows-events`. A `Logrr:WindowsEvents`
section left over from an earlier build is still honoured **once**: the first run that finds
`winlog_settings` empty imports it, logs that it has done so, and never reads it again. An
existing install upgrades without touching its config file, and nothing ends up with two homes.

The `MinimumLevel` on a source is used *only* to seed the app the first time it is created —
after that the app's own setting in `control.db` governs, and the collector reads it back to
narrow its query (§6.4). One concern, one home.

---

## 13. Operations

- **Health:** `/health` → `{ status, queueDepth, droppedLastHour, diskFreeMb,
  oldestUnflushedMs, activeSubscriptions, pendingDeliveries, deadLettered }`. 503 if the
  disk guard tripped or the writer faulted.
- **Self-logging:** Serilog to a rolling file in the data dir. The server must never log
  to itself — infinite loop under failure, and rules firing on Logrr's own errors would
  be a genuinely funny outage.
- **Backup:** copy any partition file that isn't today's, plus `control.db` and `keys\`.
  Document `sqlite3 .backup` for the live partition.
- **Upgrade:** stop site → deploy → start. Data dir untouched. Because IIS holds DLL
  handles, use the physical-path-swap pattern (publish to `logrr_{build}`, repoint the
  site, delete the previous folder on the next deploy) rather than robocopying over a
  live site.
- **Graceful shutdown:** on `ApplicationStopping`, drain the ingest channel with a 10 s
  budget, flush pending realtime frames, WAL-checkpoint and close all partitions. Pending
  deliveries stay in the queue and resume on next start.

---

## 14. Performance targets

Modest hardware (4 vCPU, basic SSD, alongside other IIS sites):

| Metric | Target |
|---|---|
| Sustained ingest | 5,000 events/sec, single app |
| Ingest p99 latency (HTTP, enqueue only) | < 20 ms |
| Ingest → visible in live tail | < 750 ms p95 |
| Ingest → webhook dispatched | < 2 s p95 |
| Query: last 100 events, no filter | < 50 ms |
| Query: full-text over 7 days / 10M events | < 2 s |
| Memory, steady state, 10 subscriptions | < 400 MB |
| Storage per event | ~400 bytes average |

Load harness in `Logrr.LoadTests` generating realistic CLEF batches while N browser
subscriptions tail. These are acceptance criteria, not measurements — they're hypotheses
until that harness runs.

---

## 15. Phases

**Phase 1 — usable**
CLEF + JSON ingest, token auth, partitioned SQLite storage, retention, apps + tokens
management, event grid with level/time/text filtering, **live tail with frame batching
and backpressure**, **manual ticket submission to a webhook destination with templating
and the durable delivery queue**, first-run bootstrap, IIS publish profile and setup doc.

**Phase 2 — good**
Filter expression language with both compilation backends, indexed properties, **rule
engine with dedupe/threshold/cooldown and all safety valves**, delivery dashboard with
retry, live app tiles and admin metrics, event detail with formatted exceptions and
ticket badges, per-app stats, load tests.

**Phase 3 — nice**
OIDC for the UI, event-type grouping ("this occurred 1,204 times"), OTLP logs ingest,
CSV/JSON export, per-level retention (errors 90 days, debug 3), saved searches, ticket
status sync back from the destination.

**Deferred indefinitely:** metrics, clustering, multi-node, PostgreSQL backend, mobile app,
email/SMS channels.

The trace view (§9) is the one thing lifted out of this list, and only the log-shaped half of
it: `trace_id` was already on every event and already indexed, so correlating a request's log
lines cost a reader and a screen. That is not APM — there is no sampling, no service map, no
metrics off the back of it, and a span is only ever an event an app chose to log.

---

## 16. Open decisions

1. **Read tokens in v1?** UI sessions may cover every real read case. Ship the scope flag
   in the schema; defer the code path to Phase 2 if nothing needs it.
2. **Per-day vs per-hour partitions** for very high volume apps. Start daily; add an
   automatic hourly split if a single partition exceeds ~2 GB.
3. **Own a client package?** Recommendation: don't. Ship a docs page for
   `Serilog.Sinks.Seq` config plus a copy-pasteable VB.NET helper. Revisit only if
   something genuinely can't use either.
4. **Blazor Server circuit for the tail** — the main technical risk in this spec. If the
   virtualized-list-over-circuit approach measures badly under load, the fallback is a
   plain JS `EventSource` against the existing SSE endpoint rendering into a canvas-style
   virtual list, with Blazor retained for everything else. Prototype the tail first, before
   building the rest of the UI around it.
5. **Ticket state sync.** Phase 1 is fire-and-forget. Polling destinations for resolution
   status would let the UI grey out resolved errors, but it means per-vendor code and an
   inbound integration surface. Probably worth it eventually; definitely not in v1.

---

## 17. Risks

| Risk | Mitigation |
|---|---|
| App pool recycle drops buffered events and kills tails | AlwaysRunning + preload; drain on shutdown; reconnect backfill from SQLite |
| Disk fills, takes down the host | Global free-space guard with aggressive purge + UI warning |
| Live tail melts the browser under a log storm | Frame batching, server-side filtering, ring buffer, honest "N dropped" indicator |
| WebSockets unavailable on the IIS host | SignalR auto-fallback to SSE/long-poll; setup doc requires the role feature |
| Rule storm opens thousands of tickets | Backfill age limit, dedupe, cooldown, per-rule and per-destination caps, storm collapse, dry-run |
| Webhook outage loses tickets | Durable delivery queue with backoff, dead-letter, manual retry, circuit breaker |
| Stack trace breaks the webhook JSON payload | Default JSON escaping on all tokens, rendered-body parse validation before dispatch |
| Destination secrets lost on redeploy | Data Protection keys persisted outside the app folder; documented in backup procedure |
| Filter semantics diverge between SQL and predicate backends | Single parser, two emitters, property-tested against each other |
| Logging server outage breaks the logging app | Sinks must be fire-and-forget with local buffering; document required client config |
