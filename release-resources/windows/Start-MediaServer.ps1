$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$data = Join-Path $env:LOCALAPPDATA 'Splortch/MediaServer'
New-Item -ItemType Directory -Path $data -Force | Out-Null
$configFile = Join-Path $data 'launcher.json'
if (Test-Path $configFile) { $config = Get-Content $configFile -Raw | ConvertFrom-Json }
else {
    Add-Type -AssemblyName System.Windows.Forms
    $picker = New-Object System.Windows.Forms.FolderBrowserDialog
    $picker.Description = 'Choose the media folder the server is allowed to read'
    if ($picker.ShowDialog() -ne 'OK') { throw 'Choose a media folder to complete first-time setup.' }
    $config = @{ media = $picker.SelectedPath }
    $config | ConvertTo-Json | Set-Content -Path $configFile -Encoding UTF8
}
if (-not (Test-Path -LiteralPath $config.media -PathType Container)) { throw "Saved media folder is unavailable. Edit $configFile or reconnect the drive." }
$probe = Get-Command ffprobe -ErrorAction SilentlyContinue
$downloadedProbe = Join-Path $data 'tools/ffmpeg-8.1.2-essentials_build/bin/ffprobe.exe'
if ($probe) { $probePath = $probe.Source }
elseif (Test-Path $downloadedProbe) { $probePath = $downloadedProbe }
else {
    Write-Host 'FFprobe is needed to scan media. First-time setup can download the pinned FFmpeg tools from GyanD on GitHub.'
    Write-Host 'The tools have their own GPL/LGPL licensing; details: https://www.gyan.dev/ffmpeg/builds/'
    if ((Read-Host 'Download and verify the tools now? [Y/n]') -match '^[nN]') { throw 'Install FFprobe on PATH, then run this launcher again.' }
    $archive = Join-Path $data 'media-tools.zip'
    Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip' -OutFile $archive
    if ((Get-FileHash $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec') {
        Remove-Item $archive -Force
        throw 'Media tools checksum mismatch; refusing to execute the download.'
    }
    Expand-Archive $archive -DestinationPath (Join-Path $data 'tools') -Force
    Remove-Item $archive -Force
    $probePath = $downloadedProbe
}
$env:MediaServer__DataDirectory = $data
$env:MediaServer__AllowedRoots__0 = $config.media
$env:MediaServer__FfprobePath = $probePath
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5050'
$server = Start-Process -FilePath (Join-Path $root 'Api.exe') -WorkingDirectory $root -NoNewWindow -PassThru
try {
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($server.HasExited) { throw 'The server exited during startup. Read the console output above.' }
        try { $status = Invoke-RestMethod 'http://127.0.0.1:5050/health'; if ($status.status -eq 'ok') { $ready = $true; break } } catch {}
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'The server did not become ready within 60 seconds.' }
    $token = Join-Path $data 'setup-token'
    if (Test-Path $token) { Write-Host ('First-time setup token: ' + (Get-Content $token -Raw)) }
    Write-Host "Server data: $data"
    Write-Host 'Leave this window open while the server is running. Press Ctrl+C to stop.'
    Start-Process 'http://127.0.0.1:5050'
    Wait-Process -Id $server.Id
} finally {
    if (-not $server.HasExited) { Stop-Process -Id $server.Id }
}
