#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-shot IIS deploy for Logrr. Idempotent: run it as many times as you like.

.DESCRIPTION
    Encodes the whole correct setup so none of it has to be done by hand:
      - copies the published app FLAT into the site folder (no nested \publish),
      - writes a known-good ASCII web.config (no encoding traps -> no 500.19),
      - configures the app pool (No Managed Code, AlwaysRunning, no idle recycle),
      - grants the pool identity the exact permissions it needs,
      - creates/points the site + binding, starts it,
      - then prints the app's own startup log so you SEE success or the real error.

    Defaults to out-of-process hosting, which is the most forgiving model for a
    self-contained app (ANCM just launches Logrr.exe and reverse-proxies to it).
    Switch to -HostingModel inprocess later if you want the extra bit of speed.

.EXAMPLE
    # from the repo root on the build/server box, after `dotnet publish ... /p:PublishProfile=IIS`
    .\scripts\deploy-iis.ps1 -Port 5443 -HostHeader lanticket.credit.com

.EXAMPLE
    # same, with Windows integrated sign-in (IIS site config + appsettings, both re-applied
    # on every run - see -WindowsAuth and docs/SETUP.md section 8)
    .\scripts\deploy-iis.ps1 -Port 5443 -WindowsAuth
#>
[CmdletBinding()]
param(
    # Must match PublishDir in Properties\PublishProfiles\IIS.pubxml. The sibling
    # net10.0\win-x64 folder is intermediate build output, NOT a publish. It is a
    # convincing decoy: it has Logrr.exe AND web.config, so deploying it appears to work.
    # But it has no wwwroot at all - it ships Logrr.staticwebassets.runtime.json instead,
    # which points the app at asset paths on the BUILD machine. On a server every CSS/JS
    # request and _framework\blazor.web.js then 404s: unstyled page, no Blazor, no error.
    [string]$Source       = "$PSScriptRoot\..\src\Logrr.Server\bin\Release\net10.0\publish",
    # NOT under C:\inetpub\wwwroot. That folder is Default Web Site's physical path, so a
    # child folder there is also reachable as a plain subdirectory of Default Web Site -
    # which reads this app's web.config, hits the <aspNetCore> section it may not process
    # outside an application, and returns 500.19 without ever invoking ANCM (no event log
    # entry, nothing in stdout). Keep the app off any other site's root. See docs/SETUP.md.
    [string]$SitePath     = "C:\inetpub\Logrr",
    [string]$SiteName     = "Logrr",
    [string]$PoolName     = "Logrr",
    [string]$DataPath     = "C:\Logrr",
    [int]   $Port         = 5443,
    [string]$HostHeader   = "",
    [ValidateSet("outofprocess","inprocess")][string]$HostingModel = "outofprocess",
    # Windows integrated sign-in (docs/SETUP.md section 8). It needs two things that live in two
    # places neither of which can see the other: Windows Authentication on the IIS site, and
    # Logrr:Auth:Windows:Enabled in appsettings.json. Doing it by hand goes wrong twice - the IIS
    # section is locked so web.config cannot carry it, and step 2 below mirrors the published
    # appsettings.json over the deployed one, silently reverting the edit on the next deploy.
    # So it belongs here, applied after the copy, on every run.
    [switch]$WindowsAuth,
    # $false shows a "Sign in with Windows" button instead of redirecting anonymous visitors
    # straight through the handshake. Only meaningful with -WindowsAuth.
    [bool]  $AutoSignIn   = $true
)

$ErrorActionPreference = "Stop"
Import-Module WebAdministration
function Info($m) { Write-Host $m -ForegroundColor Cyan }
function Ok($m)   { Write-Host $m -ForegroundColor Green }
function Warn($m) { Write-Host $m -ForegroundColor Yellow }

Info "== Logrr IIS deploy =="

# 0) Sanity: the published app must exist.
if (-not (Test-Path (Join-Path $Source "Logrr.exe"))) {
    throw "Logrr.exe not found under -Source '$Source'.`n" +
          "Publish first:  dotnet publish src\Logrr.Server -c Release /p:PublishProfile=IIS"
}

# 1) Stop anything currently running so files aren't locked.
if (Test-Path "IIS:\Sites\$SiteName")    { Stop-Website    -Name $SiteName -ErrorAction SilentlyContinue }
if (Test-Path "IIS:\AppPools\$PoolName") { Stop-WebAppPool -Name $PoolName -ErrorAction SilentlyContinue }
Start-Sleep -Seconds 2

# 2) Copy the app FLAT into the site folder (mirror; removes any stale nested \publish).
New-Item -ItemType Directory -Force -Path $SitePath | Out-Null
Info "Copying $Source -> $SitePath"
robocopy $Source $SitePath /MIR /NFL /NDL /NJH /NJS /NP /XD logs | Out-Null   # keep the logs dir
New-Item -ItemType Directory -Force -Path (Join-Path $SitePath "logs") | Out-Null
New-Item -ItemType Directory -Force -Path $DataPath | Out-Null

# 3) Write a guaranteed-clean, ASCII, BOM-free web.config (this is what kept biting us).
$webConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <location path="." inheritInChildApplications="false">
    <system.webServer>
      <handlers>
        <add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" />
      </handlers>
      <!-- forwardWindowsAuthToken is the default, but out-of-process hosting depends on it
           entirely: IIS completes the Windows handshake and hands the token to Logrr in the
           MS-ASPNETCORE-WINAUTHTOKEN header. Without it Windows sign-in cannot work here. -->
      <aspNetCore processPath=".\Logrr.exe" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="$HostingModel" forwardWindowsAuthToken="true">
        <environmentVariables>
          <environmentVariable name="LOGRR_DATA_PATH" value="$DataPath" />
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
      <!-- No <webSocket> element: that section is locked (overrideModeDefault="Deny")
           at server level, so setting it here means 500.19 / 0x80070021 on every request,
           raised before ANCM runs - no stdout log, no event log entry. WebSockets come
           from the 'WebSocket Protocol' role feature instead. -->
    </system.webServer>
  </location>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $SitePath "web.config"), $webConfig, (New-Object System.Text.UTF8Encoding($false)))
Ok "web.config written (ASCII, no BOM, hostingModel=$HostingModel)"

# 3b) Windows sign-in, half one: the app's own switch. Read once at startup, and the copy in
#     step 2 just replaced this file with the published one - where it ships off.
$cfgPath = Join-Path $SitePath "appsettings.json"
if ($WindowsAuth) {
    $cfg = Get-Content $cfgPath -Raw | ConvertFrom-Json
    if (-not $cfg.Logrr)      { $cfg      | Add-Member -NotePropertyName Logrr   -NotePropertyValue ([pscustomobject]@{}) }
    if (-not $cfg.Logrr.Auth) { $cfg.Logrr | Add-Member -NotePropertyName Auth   -NotePropertyValue ([pscustomobject]@{}) }
    if (-not $cfg.Logrr.Auth.Windows) { $cfg.Logrr.Auth | Add-Member -NotePropertyName Windows -NotePropertyValue ([pscustomobject]@{}) }
    $cfg.Logrr.Auth.Windows | Add-Member -NotePropertyName Enabled    -NotePropertyValue $true       -Force
    $cfg.Logrr.Auth.Windows | Add-Member -NotePropertyName AutoSignIn -NotePropertyValue $AutoSignIn -Force
    [System.IO.File]::WriteAllText($cfgPath, ($cfg | ConvertTo-Json -Depth 12), (New-Object System.Text.UTF8Encoding($false)))
    Ok "Windows sign-in enabled in appsettings.json (AutoSignIn=$AutoSignIn)"
} else {
    Warn "Windows sign-in NOT configured (no -WindowsAuth). Users sign in with a password."
    Warn "  Do not hand-edit $cfgPath - step 2 mirrors it from the publish folder on every deploy."
}

# 4) App pool: No Managed Code, AlwaysRunning, no idle/periodic recycle.
if (-not (Test-Path "IIS:\AppPools\$PoolName")) { New-WebAppPool -Name $PoolName | Out-Null }
Set-ItemProperty "IIS:\AppPools\$PoolName" -Name managedRuntimeVersion            -Value ""
Set-ItemProperty "IIS:\AppPools\$PoolName" -Name startMode                        -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.identityType        -Value "ApplicationPoolIdentity"
Set-ItemProperty "IIS:\AppPools\$PoolName" -Name processModel.idleTimeout         -Value ([TimeSpan]::Zero)
Set-ItemProperty "IIS:\AppPools\$PoolName" -Name recycling.periodicRestart.time   -Value ([TimeSpan]::Zero)
Ok "App pool '$PoolName' configured"

# 5) Permissions: pool identity needs write to data + logs, read/execute on the app.
$acct = "IIS AppPool\$PoolName"
icacls $DataPath                        /grant "${acct}:(OI)(CI)M"  /T | Out-Null
icacls (Join-Path $SitePath "logs")     /grant "${acct}:(OI)(CI)M"      | Out-Null
icacls $SitePath                        /grant "${acct}:(OI)(CI)RX" /T | Out-Null
Ok "Permissions granted to $acct"

# 6) Site + binding.
if (-not (Test-Path "IIS:\Sites\$SiteName")) {
    New-Website -Name $SiteName -PhysicalPath $SitePath -ApplicationPool $PoolName -Port $Port -HostHeader $HostHeader -Force | Out-Null
    Ok "Site '$SiteName' created on port $Port"
} else {
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name physicalPath    -Value $SitePath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name applicationPool -Value $PoolName
    Ok "Site '$SiteName' updated (physicalPath + pool)"
}

# 6b) Windows sign-in, half two: the IIS site. system.webServer/security/authentication is locked
#     at server level, so this cannot come from the web.config written above - it has to be set on
#     applicationHost.config, which is what /commit:apphost does. Both schemes are required:
#     Windows answers the app's challenge on /auth/windows, and Anonymous keeps token ingest, the
#     health endpoint and the password form reachable for everything that has no Windows identity.
if ($WindowsAuth) {
    $appcmd = Join-Path $env:windir "system32\inetsrv\appcmd.exe"
    foreach ($section in "windowsAuthentication", "anonymousAuthentication") {
        & $appcmd set config "$SiteName" -section:"system.webServer/security/authentication/$section" /enabled:true /commit:apphost | Out-Null
        if ($LASTEXITCODE -ne 0) {
            # The usual cause for windowsAuthentication: the role feature is not installed, so IIS
            # does not know the section at all. Nothing later can work, so stop here and say so.
            throw "appcmd could not enable $section on site '$SiteName' (exit $LASTEXITCODE).`n" +
                  "If this is windowsAuthentication, install the role feature first, then re-run:`n" +
                  "  Windows Server: Install-WindowsFeature Web-Windows-Auth`n" +
                  "  Windows client: Enable-WindowsOptionalFeature -Online -FeatureName IIS-WindowsAuthentication"
        }
    }
    Ok "IIS Windows + Anonymous Authentication enabled on site '$SiteName'"
}

Start-WebAppPool -Name $PoolName
Start-Website    -Name $SiteName
Ok "Started."

# 7) Validation: give the app a moment, then show its OWN startup output.
Info "`n== Startup log (this is the truth) =="
Start-Sleep -Seconds 6
$stdout   = Get-ChildItem (Join-Path $SitePath "logs") -Filter "stdout_*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
$internal = Get-ChildItem $DataPath -Filter "logrr-internal*.log"             -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1

if ($stdout)   { Warn "--- ANCM stdout: $($stdout.FullName) ---";  Get-Content $stdout.FullName -Tail 30 }
if ($internal) { Warn "--- Logrr internal log: $($internal.FullName) ---"; Get-Content $internal.FullName -Tail 30 }
if (-not $stdout -and -not $internal) {
    Warn "No log written yet. Browse the site once to start the app, then re-run this script's tail:"
    Warn "  Get-Content '$SitePath\logs\stdout_*.log' -Tail 30"
}

Info "`nNow browse:  https://$(if($HostHeader){$HostHeader}else{'localhost'}):$Port/"
Info "First sign-in:  admin  /  the password in $DataPath\FIRST-RUN-CREDENTIALS.txt"

if ($WindowsAuth) {
    Info "`nWindows sign-in is on, and still admits nobody until accounts are linked:"
    Info "  Admin -> Users -> Windows account, exactly as Windows reports it (CONTOSO\jrhoades)."
    Info "  /login?local=1&diag=1 reports what this server can see, including the exact identity."
    if ($HostHeader -and $HostHeader.Contains(".")) {
        Warn "  '$HostHeader' is a dotted name, so browsers do not put it in the Local intranet zone:"
        Warn "  users get a credential prompt instead of a silent sign-in. Reach the server by its"
        Warn "  short hostname, or add the site to that zone (by Group Policy for everyone)."
    }
}
