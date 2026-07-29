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
#>
[CmdletBinding()]
param(
    [string]$Source       = "$PSScriptRoot\..\src\Logrr.Server\bin\Release\net10.0\win-x64\publish",
    [string]$SitePath     = "C:\inetpub\wwwroot\Logrr",
    [string]$SiteName     = "Logrr",
    [string]$PoolName     = "Logrr",
    [string]$DataPath     = "C:\ProgramData\Logrr",
    [int]   $Port         = 5443,
    [string]$HostHeader   = "",
    [ValidateSet("outofprocess","inprocess")][string]$HostingModel = "outofprocess"
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
      <aspNetCore processPath=".\Logrr.exe" stdoutLogEnabled="true" stdoutLogFile=".\logs\stdout" hostingModel="$HostingModel">
        <environmentVariables>
          <environmentVariable name="LOGRR_DATA_PATH" value="$DataPath" />
          <environmentVariable name="ASPNETCORE_ENVIRONMENT" value="Production" />
        </environmentVariables>
      </aspNetCore>
      <webSocket enabled="true" />
    </system.webServer>
  </location>
</configuration>
"@
[System.IO.File]::WriteAllText((Join-Path $SitePath "web.config"), $webConfig, (New-Object System.Text.UTF8Encoding($false)))
Ok "web.config written (ASCII, no BOM, hostingModel=$HostingModel)"

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
