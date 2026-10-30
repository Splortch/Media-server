param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$install = Join-Path $env:LOCALAPPDATA 'Programs/Splortch/MediaServer'
$data = Join-Path $env:LOCALAPPDATA 'Splortch/MediaServer'
if ((Test-Path $install) -or (Test-Path $data)) { throw 'CI fixture requires a clean disposable Windows runner; refusing to touch existing data.' }
$temporary = Join-Path $env:RUNNER_TEMP ('server-installer-' + [Guid]::NewGuid().ToString('N'))
$media = Join-Path $temporary 'media'; New-Item -ItemType Directory $media -Force | Out-Null
& ffmpeg -v error -f lavfi -i testsrc2=size=320x180:rate=24 -f lavfi -i sine=frequency=440 -t 8 -c:v libvpx-vp9 -c:a libopus (Join-Path $media 'sample.webm')
if ($LASTEXITCODE -ne 0) { throw 'Synthetic video generation failed.' }
$mediaHash = (Get-FileHash (Join-Path $media 'sample.webm')).Hash
$ps = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$trayScript = Join-Path $install 'app/MediaServer.Tray.ps1'
function Run-Installer([string]$File, [string]$Arguments, [int]$Expected = 0) {
    $process = Start-Process -FilePath $File -ArgumentList $Arguments -PassThru
    if (-not $process.WaitForExit(120000)) { throw 'Installer fixture timed out.' }
    if ($process.ExitCode -ne $Expected) { throw "Unexpected installer exit code: $($process.ExitCode), expected $Expected" }
}
function Start-Tray {
    $process = Start-Process -FilePath $ps -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "' + $trayScript + '" -NoBrowser -NonInteractive') -PassThru
    for ($i = 0; $i -lt 90; $i++) {
        if ($process.HasExited) { throw "Tray exited during startup: $($process.ExitCode)" }
        try { if ((Invoke-RestMethod 'http://127.0.0.1:5050/health' -TimeoutSec 1).status -eq 'ok') { return $process } } catch {}
        Start-Sleep -Seconds 1
    }
    throw 'Installed tray startup timed out.'
}
function Stop-Tray($Process) {
    & $ps -NoProfile -ExecutionPolicy Bypass -File $trayScript -Stop
    if (-not $Process.WaitForExit(15000)) { throw 'Tray did not stop its owned server.' }
    if ($Process.ExitCode -ne 0) { throw 'Tray failed while stopping.' }
}
$hostProcess = $null
try {
    Run-Installer $Installer '/S'
    foreach ($name in @('Api.exe', 'coreclr.dll', 'wwwroot/index.html', 'MediaServer.Tray.ps1')) {
        if (-not (Test-Path (Join-Path $install "app/$name"))) { throw "Installed payload missing: $name" }
    }
    $startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Media Server/Media Server.lnk'
    if (-not (Test-Path $startMenu)) { throw 'Start menu shortcut missing.' }
    if (-not (Test-Path (Join-Path ([Environment]::GetFolderPath('Programs')) 'Media Server/Media Server Control.lnk'))) { throw 'Server Control shortcut missing.' }
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
    if ($shortcut.Arguments -notlike '*MediaServer.Tray.ps1*') { throw 'Start menu does not launch the real tray host.' }
    if (Test-Path (Join-Path ([Environment]::GetFolderPath('Startup')) 'Media Server.lnk')) { throw 'Sign-in startup must be opt-in.' }
    $desktop = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'Media Server Control.lnk'
    if (Test-Path $desktop) { throw 'Desktop shortcut must be opt-in.' }
    Run-Installer $Installer '/S /DESKTOP'
    if (-not (Test-Path $desktop)) { throw 'Opt-in desktop shortcut missing.' }
    $desktopLink = (New-Object -ComObject WScript.Shell).CreateShortcut($desktop)
    if ($desktopLink.Arguments -notlike '*MediaServer.Tray.ps1* -Command Show') { throw 'Desktop shortcut does not reopen Server Control.' }
    New-Item -ItemType Directory $data -Force | Out-Null
    @{ media = $media } | ConvertTo-Json | Set-Content (Join-Path $data 'launcher.json') -Encoding UTF8
    $hostProcess = Start-Tray
    & python scripts/test-installed-server.py $data $media initialize
    if ($LASTEXITCODE -ne 0) { throw 'Installed real-backend integration failed.' }
    # Reopening must dispatch to the original controller, not take .server.lock again.
    $originalBackend = (Get-Process Api | Select-Object -First 1).Id
    foreach ($suffix in @('', ' -Command Show')) {
        $second = Start-Process -FilePath $ps -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -STA -File "' + $trayScript + '" -NonInteractive' + $suffix) -PassThru
        if (-not $second.WaitForExit(15000) -or $second.ExitCode -ne 0) { throw 'Second control launch did not reopen the existing controller.' }
        if ($hostProcess.HasExited -or -not (Get-Process -Id $originalBackend -ErrorAction SilentlyContinue)) { throw 'Reopening replaced or stopped the original server.' }
    }
    # Do not overwrite files or remove an application while either lease is held.
    Run-Installer $Installer '/S' 2
    Run-Installer (Join-Path $install 'Uninstall.exe') ('/S _?=' + $install) 2
    # Exercise the same local commands used by the control window/tray. Stop
    # leaves the controller alive so it can start the server again without login.
    foreach ($action in @('Stop', 'Start', 'Restart')) {
        $previousServer = Get-Process Api -ErrorAction SilentlyContinue | Select-Object -First 1
        $previousId = if ($previousServer) { $previousServer.Id } else { 0 }
        & $ps -NoProfile -ExecutionPolicy Bypass -File $trayScript -Command $action
        if ($LASTEXITCODE -ne 0) { throw "Control command failed: $action" }
        $expectedRunning = $action -ne 'Stop'
        $matched = $false
        for ($attempt = 0; $attempt -lt 90; $attempt++) {
            if ($hostProcess.HasExited) { throw 'Control window exited during a start/stop command.' }
            $healthy = $false
            try { $healthy = (Invoke-RestMethod 'http://127.0.0.1:5050/health' -TimeoutSec 1).status -eq 'ok' } catch {}
            $oldExited = $action -ne 'Restart' -or -not (Get-Process -Id $previousId -ErrorAction SilentlyContinue)
            if (($healthy -eq $expectedRunning) -and $oldExited) { $matched = $true; break }
            Start-Sleep -Seconds 1
        }
        if (-not $matched) { throw "Server did not reach expected state after $action" }
        if ($expectedRunning) {
            & python scripts/test-installed-server.py $data $media verify
            if ($LASTEXITCODE -ne 0) { throw "Persisted account/library/progress failed after $action" }
        }
    }
    Stop-Tray $hostProcess; $hostProcess = $null
    # A separately launched backend must remain untouched by an observing control.
    $env:MediaServer__DataDirectory = $data
    $env:MediaServer__AllowedRoots__0 = $media
    $env:MediaServer__FfprobePath = (Get-Command ffprobe).Source
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5050'
    $external = Start-Process -FilePath (Join-Path $install 'app/Api.exe') -WorkingDirectory (Join-Path $install 'app') -PassThru -RedirectStandardOutput (Join-Path $temporary 'external.log') -RedirectStandardError (Join-Path $temporary 'external-error.log')
    try {
        $ready = $false
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            if ($external.HasExited) { throw 'External fixture server exited.' }
            try { $ready = (Invoke-RestMethod 'http://127.0.0.1:5050/health' -TimeoutSec 1).status -eq 'ok' } catch {}
            if ($ready) { break }; Start-Sleep -Seconds 1
        }
        if (-not $ready) { throw 'External fixture did not start.' }
        $hostProcess = Start-Tray
        Start-Sleep -Seconds 3
        if ($hostProcess.HasExited) { throw 'Control failed to open alongside an external server.' }
        Stop-Tray $hostProcess; $hostProcess = $null
        if ($external.HasExited) { throw 'Control terminated a server it did not own.' }
        & python scripts/test-installed-server.py $data $media verify
        if ($LASTEXITCODE -ne 0) { throw 'External backend was not usable after exiting control.' }
    } finally {
        if ($hostProcess) { Stop-Tray $hostProcess; $hostProcess = $null }
        if (-not $external.HasExited) { $external.Kill(); $external.WaitForExit() }
    }
    $database = Get-ChildItem $data -Filter '*.db' | Select-Object -First 1
    if (-not $database) { throw 'Installed database missing.' }
    $databaseHash = (Get-FileHash $database.FullName).Hash
    $sentinel = Join-Path $data 'keep-private-data.txt'; Set-Content $sentinel 'explicit fixture'
    $registration = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\Splortch.MediaServer'
    $number = (Get-ItemProperty $registration).ReleaseNumber
    if ($number -lt 65535) {
        try {
            Set-ItemProperty $registration -Name ReleaseNumber -Value ($number + 1)
            Run-Installer $Installer '/S' 4
        } finally { Set-ItemProperty $registration -Name ReleaseNumber -Value $number }
    }
    $trayLease = [IO.File]::Open((Join-Path $data 'tray-host.lock'), 'Open', 'ReadWrite', 'None')
    try { Run-Installer $Installer '/S' 2 } finally { $trayLease.Dispose() }
    # Same-channel upgrade/reinstall must preserve existing data without starting it silently.
    Run-Installer $Installer '/S'
    if ((Get-FileHash $database.FullName).Hash -ne $databaseHash) { throw 'Installer modified the database.' }
    $hostProcess = Start-Tray
    & python scripts/test-installed-server.py $data $media verify
    if ($LASTEXITCODE -ne 0) { throw 'Resume/account/library did not survive reinstall.' }
    Stop-Tray $hostProcess; $hostProcess = $null
    # Unknown user files in the install directory are also preserved.
    $unknown = Join-Path $install 'app/operator-note.txt'; Set-Content $unknown 'explicit fixture'
    $databaseHash = (Get-FileHash $database.FullName).Hash
    Run-Installer (Join-Path $install 'Uninstall.exe') ('/S _?=' + $install)
    if (Test-Path $desktop) { throw 'Uninstall left the desktop shortcut.' }
    if ((Test-Path (Join-Path $install 'app/Api.exe')) -or (Test-Path $startMenu)) { throw 'Uninstall left registered application files.' }
    if (-not (Test-Path $sentinel) -or -not (Test-Path $unknown)) { throw 'Uninstall removed an unowned file.' }
    if ((Get-FileHash $database.FullName).Hash -ne $databaseHash) { throw 'Uninstall modified the database.' }
    if ((Get-FileHash (Join-Path $media 'sample.webm')).Hash -ne $mediaHash) { throw 'Original media changed.' }
    Write-Host 'Actual setup.exe install, tray startup/stop, locked-update refusal, reinstall/resume, and non-destructive uninstall passed.'
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Tray $hostProcess }
}
