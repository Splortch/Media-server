$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../release-resources/windows/MediaServer.Network.ps1')
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Must-Fail([scriptblock]$Action) {
    $failed = $false
    try { & $Action | Out-Null } catch { $failed = $true }
    Assert $failed 'Expected invalid network configuration to fail.'
}
$local = Resolve-ServerNetwork @{ media = 'legacy' } @('192.168.1.20')
Assert ($local.Urls -eq 'http://127.0.0.1:5050') 'Old launcher config must remain local.'
Assert ($local.AllowedHosts -notmatch '\*') 'Host allowlist must never contain wildcards.'
$homeNetwork = Resolve-ServerNetwork @{ networkMode = 'home' } @('192.168.1.20', '10.0.0.5')
Assert ($homeNetwork.HomeUrls.Count -eq 2) 'Home mode must include assigned private interfaces.'
Assert ($homeNetwork.Urls.StartsWith('http://127.0.0.1:5050;')) 'Local management must remain reachable.'
$specific = Resolve-ServerNetwork @{ networkMode = 'specific'; networkAddress = '10.0.0.5' } @('192.168.1.20', '10.0.0.5')
Assert ($specific.Urls -eq 'http://127.0.0.1:5050;http://10.0.0.5:5050') 'Specific mode must not bind every adapter.'
foreach ($ip in @('0.0.0.0', '8.8.8.8', '127.0.0.2', '169.254.1.1', '::1', '192.168.1.1;http://*:5050', '172.32.0.1')) {
    Must-Fail { Resolve-ServerNetwork @{ networkMode = 'home' } @($ip) }
}
Must-Fail { Resolve-ServerNetwork @{ networkMode = 'specific'; networkAddress = '192.168.1.21' } @('192.168.1.20') }
Must-Fail { Resolve-ServerNetwork @{ networkMode = 'home' } @() }
Must-Fail { Resolve-ServerNetwork @{ networkMode = 'unknown' } @() }
# Incomplete setup must never expose an initial administrator setup endpoint on LAN.
$temp = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $temp | Out-Null
$oldUrls = $env:ASPNETCORE_URLS; $oldHosts = $env:AllowedHosts
try {
    function Get-HomeAddresses { @('192.168.1.20') }
    Must-Fail { Set-ServerNetworkEnvironment @{ networkMode = 'home' } $temp }
    Set-Content (Join-Path $temp 'media.db') 'fixture'
    Set-Content (Join-Path $temp 'setup-token') 'fixture'
    Must-Fail { Set-ServerNetworkEnvironment @{ networkMode = 'home' } $temp }
    Remove-Item (Join-Path $temp 'setup-token')
    [void](Set-ServerNetworkEnvironment @{ networkMode = 'home' } $temp)
    Assert ($env:ASPNETCORE_URLS -match '192.168.1.20') 'Launch environment must use saved mode.'
    Assert ($env:AllowedHosts -notmatch '\*') 'Home hosts must be explicitly allowlisted.'
    [void](Set-ServerNetworkEnvironment @{ networkMode = 'local' } $temp)
    Assert ($env:ASPNETCORE_URLS -eq 'http://127.0.0.1:5050') 'Local mode must remove old LAN listeners.'
    Assert ($env:AllowedHosts -notmatch '192.168') 'Local mode must remove old LAN hosts.'
} finally {
    $env:ASPNETCORE_URLS = $oldUrls; $env:AllowedHosts = $oldHosts
    Remove-Item $temp -Recurse -Force
}
# Parse every changed launcher without running its Windows-only UI.
foreach ($path in Get-ChildItem (Join-Path $PSScriptRoot '../release-resources/windows/*.ps1')) {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path.FullName, [ref]$tokens, [ref]$errors)
    Assert (-not $errors.Count) ($errors | Out-String)
}
Write-Host 'Server network validation, setup guard, environment transitions and launcher parsing passed.'
