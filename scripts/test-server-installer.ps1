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
    $shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startMenu)
    if ($shortcut.Arguments -notlike '*MediaServer.Tray.ps1*') { throw 'Start menu does not launch the real tray host.' }
    if (Test-Path (Join-Path ([Environment]::GetFolderPath('Startup')) 'Media Server.lnk')) { throw 'Sign-in startup must be opt-in.' }
    New-Item -ItemType Directory $data -Force | Out-Null
    @{ media = $media } | ConvertTo-Json | Set-Content (Join-Path $data 'launcher.json') -Encoding UTF8
    $hostProcess = Start-Tray
    & python scripts/test-installed-server.py $data $media initialize
    if ($LASTEXITCODE -ne 0) { throw 'Installed real-backend integration failed.' }
    # Do not overwrite files or remove an application while either lease is held.
    Run-Installer $Installer '/S' 2
    Run-Installer (Join-Path $install 'Uninstall.exe') ('/S _?=' + $install) 2
    Stop-Tray $hostProcess; $hostProcess = $null
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
    if ((Test-Path (Join-Path $install 'app/Api.exe')) -or (Test-Path $startMenu)) { throw 'Uninstall left registered application files.' }
    if (-not (Test-Path $sentinel) -or -not (Test-Path $unknown)) { throw 'Uninstall removed an unowned file.' }
    if ((Get-FileHash $database.FullName).Hash -ne $databaseHash) { throw 'Uninstall modified the database.' }
    if ((Get-FileHash (Join-Path $media 'sample.webm')).Hash -ne $mediaHash) { throw 'Original media changed.' }
    Write-Host 'Actual setup.exe install, tray startup/stop, locked-update refusal, reinstall/resume, and non-destructive uninstall passed.'
} finally {
    if ($hostProcess -and -not $hostProcess.HasExited) { Stop-Tray $hostProcess }
}
