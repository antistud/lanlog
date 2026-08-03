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

**a. Enable it in IIS.** On the site, enable **Windows Authentication** *and* leave
**Anonymous Authentication** enabled. Both are required: anonymous keeps token ingest, the
health endpoint and the password form reachable, and Windows answers the app's challenge on
`/auth/windows`. Windows Authentication is not installed by default — add the
*Windows Authentication* role feature first, or the option will not appear.

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

## 9. Backup

Copy any partition file that isn't today's, plus `control.db` and the **`keys\`** folder.
**If `keys\` is lost, destination webhook secrets are unrecoverable** (SPEC §11, §13).
For the live partition, use `sqlite3 .backup`.

## 10. Upgrade

Publish to a new `logrr_{build}` folder, repoint the site's physical path, recycle, and
delete the previous folder on the next deploy. The data directory is untouched.
