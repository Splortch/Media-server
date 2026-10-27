# CI-only test tooling; FFmpeg is not bundled into the Windows client installer.
$ErrorActionPreference = 'Stop'
$version = '8.1.2'
$asset = "ffmpeg-$version-essentials_build.zip"
$expectedHash = 'db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec'
$archive = Join-Path $env:RUNNER_TEMP $asset
$destination = Join-Path $env:RUNNER_TEMP 'media-server-ffmpeg'

if (-not (Test-Path $archive)) {
    & curl.exe --fail --location --retry 3 --connect-timeout 20 --max-time 240 `
        --output $archive "https://github.com/GyanD/codexffmpeg/releases/download/$version/$asset"
    if ($LASTEXITCODE -ne 0) { throw 'FFmpeg archive download failed' }
}
$actualHash = (Get-FileHash -Path $archive -Algorithm SHA256).Hash.ToLowerInvariant()
if ($actualHash -ne $expectedHash) {
    Remove-Item $archive -Force
    throw 'FFmpeg archive checksum mismatch; refusing to execute it'
}
Expand-Archive -Path $archive -DestinationPath $destination -Force
$bin = Join-Path $destination "ffmpeg-$version-essentials_build/bin"
foreach ($tool in @('ffmpeg', 'ffprobe')) {
    $executable = Join-Path $bin "$tool.exe"
    if (-not (Test-Path $executable)) { throw "Missing required tool: $tool" }
    & $executable -version
    if ($LASTEXITCODE -ne 0) { throw "$tool did not start successfully" }
}
# Export to future Actions steps as well as the current PowerShell process.
$bin | Out-File -FilePath $env:GITHUB_PATH -Append -Encoding utf8
$env:PATH = "$bin;$env:PATH"
