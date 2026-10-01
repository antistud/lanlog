# Logrr — IIS setup

Ten-minute stand-up on an existing Windows box. See `docs/SPEC.md` for the full design.

## 1. Publish

On a build machine with the .NET 10 SDK:

```
dotnet publish src/Logrr.Server -c Release /p:PublishProfile=IIS
```

Output lands in `src/Logrr.Server/bin/Release/net10.0/publish/`. It is self-contained —
**no .NET runtime install is required on the server.**

**Copy the `publish` folder — never the neighbouring `bin/Release/net10.0/win-x64/`.**
That one is intermediate build output and a convincing decoy: it contains both `Logrr.exe`
and `web.config`, so a site pointed at it starts and serves HTML. But it has no `wwwroot`
at all. It ships `Logrr.staticwebassets.runtime.json`, which resolves static assets to
paths on the *build* machine, so on a server every stylesheet, script and
`_framework/blazor.web.js` 404s — an unstyled page with no interactivity and no error to
explain it. Prefer the publish profile over passing `-r win-x64 --self-contained` by hand,
which puts output in a third location (`bin/Release/net10.0/win-x64/publish/`).

## 2. Copy to the server

Xcopy the publish folder to a versioned directory so upgrades are a path swap (SPEC §13):

```
C:\inetpub\logrr_{build}\
```

Create the writable **data** directory (kept separate from the app folder so a redeploy
never overwrites your logs):

```
C:\Logrr\
```

## 3. Prerequisites on the server

- **IIS** with the **ASP.NET Core Module V2** (installed by the
  *.NET Hosting Bundle* — but since Logrr is self-contained you only need the module,
  which the Hosting Bundle provides).
- **Install the IIS `WebSocket Protocol` role feature.** It is *not* present by default on
  Windows Server. Without it the realtime layer still works, but falls back to
  SSE/long-polling with worse latency (SPEC §2, §8.5).

  Installing the feature is all that is required — WebSockets are enabled at server level
  by default. **Do not add `<webSocket enabled="true" />` to `web.config`.** IIS locks the
  `system.webServer/webSocket` section (`overrideModeDefault="Deny"`), so a `web.config`
  that sets it is rejected with **HTTP 500.19, code 0x80070021, on every request** — and
  because that happens while IIS parses config, *before* the ASP.NET Core Module runs,
  there is no ANCM stdout log and no Windows event log entry to explain it. The app appears
  completely dead while `Logrr.exe` run by hand works perfectly. If you ever see a bare
  500.19 with an empty `logs\` folder, check `web.config` for a locked section first.

## 4. Create the site and app pool

- Application pool: **No Managed Code**, identity **ApplicationPoolIdentity**.
- Set the pool **Start Mode = AlwaysRunning**, **Idle Time-out = 0**, and
  **Regular Time Interval = 0** (disable the periodic recycle). Enable **site preload**.
  Without this, ingest drops on idle recycle and live tail dies silently.
- Point the site's physical path at `C:\inetpub\logrr_{build}`.

## 5. Permissions

Grant the app pool identity **Modify** on the data directory only:

```
icacls "C:\Logrr" /grant "IIS AppPool\<YourPoolName>:(OI)(CI)M"
```

The app folder can stay read-only.

## 6. Point the data path

`web.config` sets `LOGRR_DATA_PATH=C:\Logrr`. Override there if you use a
different location. Resolution order: `LOGRR_DATA_PATH` → `appsettings.json`
(`Logrr:Storage:DataPath`) → `C:\Logrr`.

## 7. First run

Browse the site. On first start Logrr:

- creates `control.db` and the Data Protection `keys\` folder,
- seeds an `admin` account and writes the password to
  `C:\Logrr\FIRST-RUN-CREDENTIALS.txt` (also emitted to
  `logrr-internal*.log`),
- forces a password change on first sign-in and deletes the credentials file afterwards.

Sign in, create an app and an ingest token, then point a Serilog Seq sink at it:

```csharp
Log.Logger = new LoggerConfiguration()
    .WriteTo.Seq("http://your-server/", apiKey: "lg_billing_...")
    .CreateLogger();
```

(The endpoint is Seq-compatible — no Logrr-specific client package to install.)

## 8. Windows integrated sign-in (optional)

Signs domain users in automatically, so nobody types a Logrr password. It is a *sign-in
route*, not a second session type: the Windows identity is matched to an existing Logrr
account and the normal session cookie is issued, so roles and per-app access are unchanged.

**Only accounts you have linked can sign in this way.** An unrecognised Windows identity is
refused and sent to the password form — Logrr never provisions an account from a domain
identity on its own.

Steps a and b are exactly what `deploy-iis.ps1 -WindowsAuth` does, and it re-applies them on
every run — which matters, because step 2 of that script mirrors the published
`appsettings.json` over the deployed one and would otherwise revert step b. Do them by hand
only if you are not deploying with the script.

**a. Enable it in IIS.** On the site, enable **Windows Authentication** *and* leave
**Anonymous Authentication** enabled. Both are required: anonymous keeps token ingest, the
health endpoint and the password form reachable, and Windows answers the app's challenge on
`/auth/windows`. Windows Authentication is not installed by default — add the
*Windows Authentication* role feature first, or the option will not appear.

Anonymous Authentication does not make Logrr's API anonymous. It only lets the request pass
through IIS; Logrr then validates `X-Logrr-ApiKey`, `X-Seq-ApiKey`, or a bearer token itself. If
Anonymous Authentication is disabled, IIS returns a Windows-authentication challenge before
Logrr can see even a valid API key.

The `system.webServer/security/authentication` section is locked by IIS, so this **cannot**
be set from `web.config` (same 500.19 trap as `<webSocket>` in §3). Set it on the site:

```
%windir%\system32\inetsrv\appcmd set config "Logrr" -section:system.webServer/security/authentication/windowsAuthentication /enabled:true /commit:apphost
%windir%\system32\inetsrv\appcmd set config "Logrr" -section:system.webServer/security/authentication/anonymousAuthentication /enabled:true /commit:apphost
```

**b. Turn it on in Logrr** (`appsettings.json`):

```json
"Logrr": {
  "Auth": { "Windows": { "Enabled": true, "AutoSignIn": true } }
}
```

`AutoSignIn` sends anonymous visitors straight through the handshake. Set it to `false` to
show the sign-in page with a *Sign in with Windows* button instead.

**c. Link each account.** Admin → Users → **Windows account**, entered exactly as Windows
reports it — `CONTOSO\jrhoades`. Case does not matter. Leaving a new user's password blank
makes the account Windows-only, which is the point: there is no password to leak or rotate.

### If it goes wrong

`/login?local=1` **always** shows the password form and never auto-redirects — that URL is
the way back in when Windows sign-in misbehaves, so a mistyped account name cannot lock you
out. Signing out lands there too, otherwise "sign out" would immediately sign you back in.

- *"…is not linked to a Logrr account"* — the account name does not match. `logrr-internal*.log`
  records the exact string the server saw; copy it into the Windows account field verbatim.
- *A browser credential prompt, then a blank 401* — Windows Authentication is not enabled on
  the site (step a). The browser has nothing to answer the challenge with.
- *`The Negotiate Authentication handler cannot be used on a server that directly supports
  Windows Authentication`* — `Enabled` is true but IIS Windows Authentication is off. Same
  fix: step a.
- *Prompted for credentials instead of being signed in silently* — the site is not in the
  browser's Local intranet zone. Use the server's short hostname, not an IP or an external
  FQDN, or add the site to that zone.
- *Nothing happens at all over `https://`, but it works on `http://localhost`* — only when
  Logrr runs its own listener rather than sitting behind IIS. Negotiate is a connection-level
  handshake and does nothing above HTTP/1.1: the challenge goes out with no `WWW-Authenticate`
  header, so the browser has nothing to answer. Logrr caps its endpoints at HTTP/1.1 whenever
  Windows sign-in is enabled *at startup*, so this means the process started with the setting
  off — restart it. The diagnostics report the protocol of the request that reached them.

The startup line in `logrr-internal*.log` states what the running process actually has —
whether Windows sign-in is on, and whether the host is providing the handshake — which
settles steps a and b without a browser.

## 9. Windows Event Log collection (optional)

Pulls the Application/System/Security logs from this box and any other Windows machine on the
LAN into Logrr. **Nothing is installed on the collected machines** — the server reads their
event logs over RPC (SPEC §6.4).

**a. Turn it on** in **Admin → Windows events** (`/admin/windows-events`). Tick *Collect Windows
events*, then add one entry per machine — `.` is this server, anything else is a name or FQDN.
There is no config file to edit and no restart: a save applies on the next poll, and shortening
the poll interval takes effect immediately.

| Field | What it does |
|---|---|
| Machine | `.` for this server, else `WEB01` or `web01.contoso.com`. One entry per machine. |
| App id | The app the events land in, created on first poll. Slug: `[a-z0-9-]{3,32}`. |
| Channels | Comma-separated; empty means `Application, System`. |
| Minimum level at creation | Seeds the new app's floor only — **Warning** by default, because Application and System are chatty at Information. |
| Collect from this machine | Untick to pause a machine without losing its entry or its cursors. |

After the app exists, its floor is changed in Admin → Apps; the collector reads that setting
back and stops pulling below it.

> Upgrading from a build that configured this in `appsettings.json`? The `Logrr:WindowsEvents`
> section is imported into the database on the first start after the upgrade — collection
> carries on unchanged — and is ignored from then on. Edit the machines in the UI; further
> edits to the file do nothing. `logrr-internal.log` records the import.

**b. Give the app pool an identity that can read the logs.** This is the step that catches
people out. `ApplicationPoolIdentity` is a *local* account and cannot authenticate to another
machine, so remote collection needs a real domain account:

- Set the app pool identity to a domain service account (Application Pools → Logrr →
  Advanced Settings → Identity), then redo the `keys\` and data-folder permissions from §5 for
  that account.
- On **each collected machine**, add that account to the local **Event Log Readers** group
  (`net localgroup "Event Log Readers" CONTOSO\svc_logrr /add`), or do it once via Group Policy
  for the whole fleet.
- The **Security** channel needs more than Event Log Readers on most builds — grant it
  explicitly or leave that channel out.
- Local-only collection (`"Machine": "."`) needs none of this; `ApplicationPoolIdentity` can
  read Application and System already.

**c. Firewall.** Remote reads use RPC — enable the **Remote Event Log Management** inbound
rules on the collected machines (`netsh advfirewall firewall set rule group="Remote Event Log
Management" new enable=yes`).

### What to expect

Collection starts at the **tail** of each log: only events written from then on appear. Set
*Initial backfill* on the same page to pull recent history instead (max 720 h — ingest rejects
anything older than 30 days).

The status table on that page is the first place to look: one row per machine and channel, with
what it last read and what went wrong if anything did. **Collect now** polls immediately rather
than waiting out the interval — the thing you want right after fixing a permission. **Reset**
forgets a channel's high-water mark so the next poll starts again from the tail; you need it
only if a cursor has run ahead of the log, which a restore from backup can do.

`logrr-internal.log` carries the same story. `Windows event collection starting at the tail
of …` means it is working. A warning naming a machine and channel means it could not read that
one — almost always (b) or (c) above; it retries every poll and logs again once it recovers,
and the other machines keep collecting meanwhile.

Removing a machine stops collection and forgets its cursors; the events already collected stay
in their app. To pause one instead, untick *Collect from this machine* — the entry and its
cursors survive, so it resumes where it stopped.

## 10. SQL Server storage (optional)

By default Logrr keeps everything in SQLite files under the data directory, which is why the
steps above never mention a database server. Point it at SQL Server instead if you already
back up, monitor and cluster one and want Logrr's data inside that perimeter. **You do not
need this** — the default is not a lesser option, it is the intended one for a single box.

**a. Create an empty database and grant rights.** Logrr creates its own schema and tables on
first start; it needs `db_ddladmin` + `db_datareader` + `db_datawriter`, or simply
`db_owner`, on that database and nothing at server level:

```sql
CREATE DATABASE Logrr;
GO
USE Logrr;
CREATE USER [IIS APPPOOL\Logrr] FOR LOGIN [IIS APPPOOL\Logrr];
ALTER ROLE db_owner ADD MEMBER [IIS APPPOOL\Logrr];
```

Under IIS the app pool connects as the machine account (`DOMAIN\SERVER01$`) when it reaches
another box, and as `IIS APPPOOL\<pool>` only for a local instance — create the login for
whichever applies. A SQL login works too; put it in the connection string.

**b. Set the connection string** (`appsettings.json`):

```json
"Logrr": {
  "Storage": {
    "ConnectionString": "Server=SQL01;Database=Logrr;Integrated Security=true;Encrypt=true;TrustServerCertificate=true;",
    "Schema": "logrr"
  }
}
```

`LOGRR_SQL_CONNECTION` overrides it, which is the tidier option since it keeps a credential
out of the published folder. `ConnectionStrings:Logrr` is read as well.

**`Encrypt` defaults to true** in the current client. Against an internal SQL Server with a
self-signed certificate that fails the connection outright with *"A connection was
successfully established … but then an error occurred during the login process"* — add
`TrustServerCertificate=true` (or install a trusted certificate on the server).

**c. Restart and check the log.** `logrr-internal.log` names the backend on every start:

```
Logrr storage backend: SQL Server SQL01, database Logrr, schema logrr
```

If it says *SQLite files under C:\Logrr*, the connection string did not reach the app — that
line is the fastest way to tell configuration from connectivity.

### What changes

- Events live in one table per app per UTC day, `[logrr].[events_{app}_{yyyyMMdd}]`.
  Retention still drops whole tables, so it stays cheap.
- **Full-text search is a `LIKE` scan, not FTS5.** Every whitespace-separated term must
  appear, and there is no stemming — searching `connect` will not find `connection`. Scans
  are bounded to one app-day, so this is fine at LAN volumes and would not be at scale.
- The **data directory is still required** and still needs write access: the Data Protection
  `keys\` folder and `logrr-internal.log` live there regardless. §5 still applies.
- The free-disk guard no longer applies — the database server's storage is yours to watch.
  Per-app retention and size caps work as before.

### Backup

The database is your backup unit; the data directory still holds `keys\`, and **losing
`keys\` still makes destination secrets unrecoverable**. Back up both.

## 11. Backup

Copy any partition file that isn't today's, plus `control.db` and the **`keys\`** folder.
**If `keys\` is lost, destination webhook secrets are unrecoverable** (SPEC §11, §13).
For the live partition, use `sqlite3 .backup`.

(With the SQL Server backend of §10, back up the database instead — but still copy `keys\`.)

## 12. Upgrade

Publish to a new `logrr_{build}` folder, repoint the site's physical path, recycle, and
delete the previous folder on the next deploy. The data directory is untouched.
