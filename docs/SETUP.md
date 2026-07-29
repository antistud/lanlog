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

## 8. Backup

Copy any partition file that isn't today's, plus `control.db` and the **`keys\`** folder.
**If `keys\` is lost, destination webhook secrets are unrecoverable** (SPEC §11, §13).
For the live partition, use `sqlite3 .backup`.

## 9. Upgrade

Publish to a new `logrr_{build}` folder, repoint the site's physical path, recycle, and
delete the previous folder on the next deploy. The data directory is untouched.
