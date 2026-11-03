# Installs ETS2-ATS GPS for the current user (no administrator rights): copies the exe to %LOCALAPPDATA%\Programs\ETS2 GPS
# and adds a Start Menu shortcut. Asks whether to start it with Windows and whether to start it now;
# use -NoPrompt to skip the questions, -Autostart / -Start to answer yes without being asked.
param([switch]$Autostart, [switch]$Start, [switch]$NoPrompt)
$ErrorActionPreference = "Stop"
$exe = Join-Path $PSScriptRoot "publish\Ets2Gps.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot "Ets2Gps.exe" }
if (-not (Test-Path $exe)) { throw "Ets2Gps.exe not found: run build.ps1 first (or put the exe next to this script)." }
$dir = Join-Path $env:LOCALAPPDATA "Programs\ETS2 GPS"
New-Item -ItemType Directory -Force $dir | Out-Null

# Copies a file next to its destination first, checks the copy and only then moves it into place, so an interrupted install can never
# leave a half-written (damaged) program behind.
function Install-File($from, $toDir) {
    $to = Join-Path $toDir (Split-Path $from -Leaf)
    $tmp = $to + ".new"
    Copy-Item $from $tmp -Force
    if ((Get-FileHash $from).Hash -ne (Get-FileHash $tmp).Hash) { Remove-Item $tmp -Force; throw "Copy of $from did not verify" }
    Move-Item $tmp $to -Force
}
$target = Join-Path $dir "Ets2Gps.exe"
# a running copy locks its file: close it (it saves its settings on closing), install, and start it again afterwards
$wasRunning = @(Get-Process Ets2Gps -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $target })
foreach ($p in $wasRunning) { try { $p.CloseMainWindow() | Out-Null } catch { } }
foreach ($p in $wasRunning) { if (-not $p.WaitForExit(8000)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }
if ($wasRunning.Count -gt 0) { Start-Sleep -Milliseconds 500 }
Install-File $exe $dir
$sh = New-Object -ComObject WScript.Shell
function Shortcut($path) { $s = $sh.CreateShortcut($path); $s.TargetPath = $target; $s.WorkingDirectory = $dir; $s.Description = "ETS2-ATS GPS"; $s.Save() }
# the app was called "ETS2 GPS" before: replace its old-named shortcuts (keeping autostart if it was on)
foreach ($k in "Programs", "Startup") {
    $oldLnk = Join-Path ([Environment]::GetFolderPath($k)) "ETS2 GPS.lnk"
    if (Test-Path $oldLnk) { Remove-Item $oldLnk -Force; if ($k -eq "Startup") { $Autostart = $true } }
}
Shortcut (Join-Path ([Environment]::GetFolderPath("Programs")) "ETS2-ATS GPS.lnk")
Write-Host "Installed to $dir (Start Menu: ETS2-ATS GPS)"
if (-not $NoPrompt -and -not $Autostart) {
    if ((Read-Host "Also start it automatically when you log in to Windows? (y/n)") -eq "y") { $Autostart = $true }
}
if ($Autostart) { Shortcut (Join-Path ([Environment]::GetFolderPath("Startup")) "ETS2-ATS GPS.lnk"); Write-Host "Autostart added." }
if (-not $NoPrompt -and -not $Start) {
    if ((Read-Host "Start it now? (y/n)") -eq "y") { $Start = $true }
}
if ($Start) { Start-Process $target }

# the lane overlay window (started by the app when it is not running): install it too when its build is next to this project
$ov = Join-Path $PSScriptRoot "..\overlay-dotnet\publish\Ets2LaneOverlay.exe"
if (-not (Test-Path $ov)) { $ov = Join-Path $PSScriptRoot "Ets2LaneOverlay.exe" }
if (Test-Path $ov) {
    $odir = Join-Path $env:LOCALAPPDATA "Programs\ETS2 Lane Overlay"
    New-Item -ItemType Directory -Force $odir | Out-Null
    # the overlay is a helper that the app starts again: close the running one so its file can be replaced
    Get-Process Ets2LaneOverlay -ErrorAction SilentlyContinue | ForEach-Object { try { $_.CloseMainWindow() | Out-Null } catch { } }
    Start-Sleep -Milliseconds 800
    Get-Process Ets2LaneOverlay -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
    Install-File $ov $odir
    Write-Host "Lane overlay installed to $odir"
}

# started again only now, so that it starts the newly installed lane overlay as well
if ($wasRunning.Count -gt 0 -and -not $Start) { Start-Process $target; Write-Host "ETS2-ATS GPS was running: closed for the update and started again." }
