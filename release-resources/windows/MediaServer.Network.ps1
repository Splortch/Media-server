# Shared by the tray and console launchers. Never changes adapter or router settings.
function Get-HomeAddresses {
    @([Net.NetworkInformation.NetworkInterface]::GetAllNetworkInterfaces() | Where-Object {
        $_.OperationalStatus -eq 'Up' -and $_.NetworkInterfaceType -ne 'Loopback'
    } | ForEach-Object { $_.GetIPProperties().UnicastAddresses } | ForEach-Object {
        $ip = $_.Address
        if ($ip.AddressFamily -eq 'InterNetwork') {
            $b = $ip.GetAddressBytes()
            if ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168)) { $ip.ToString() }
        }
    } | Sort-Object -Unique)
}
function Resolve-ServerNetwork($Config, [string[]]$AvailableAddresses) {
    $mode = [string]$Config.networkMode
    if (-not $mode) { $mode = 'local' }
    $selected = @()
    switch ($mode) {
        'local' { }
        'home' {
            $selected = @($AvailableAddresses)
            if (-not $selected.Count) { throw 'No private IPv4 address is available. Connect to your home network or select This computer.' }
        }
        'specific' {
            $ip = [string]$Config.networkAddress
            if ($ip -notin $AvailableAddresses) { throw 'The saved home IP is no longer assigned to this PC. Select an available address in Network settings, or reserve the address in your router.' }
            $selected = @($ip)
        }
        default { throw 'Unknown network mode in launcher.json. Use local, home, or specific.' }
    }
    # Validate even caller-supplied addresses: no hostname, wildcard, public IP or URL injection.
    foreach ($address in $selected) {
        $parsed = $null
        if (-not [Net.IPAddress]::TryParse($address, [ref]$parsed) -or $parsed.AddressFamily -ne 'InterNetwork' -or $parsed.ToString() -ne $address) { throw 'Invalid private IPv4 address.' }
        $b = $parsed.GetAddressBytes()
        if (-not ($b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168))) { throw 'Only private home-network addresses are supported.' }
    }
    [pscustomobject]@{
        Mode = $mode
        Urls = (@('http://127.0.0.1:5050') + @($selected | ForEach-Object { 'http://' + $_ + ':5050' })) -join ';'
        AllowedHosts = (@('localhost', '127.0.0.1', '[::1]') + $selected) -join ';'
        HomeUrls = @($selected | ForEach-Object { 'http://' + $_ + ':5050' })
    }
}
function Set-ServerNetworkEnvironment($Config, [string]$DataDirectory) {
    $network = Resolve-ServerNetwork $Config @(Get-HomeAddresses)
    if ($network.Mode -ne 'local' -and ((Test-Path -LiteralPath (Join-Path $DataDirectory 'setup-token')) -or -not (Test-Path -LiteralPath (Join-Path $DataDirectory 'media.db')))) {
        throw 'Complete first-time account setup on localhost before enabling home access. Stop the server and select This computer in Network settings if needed.'
    }
    $env:ASPNETCORE_URLS = $network.Urls
    $env:AllowedHosts = $network.AllowedHosts
    return $network
}
