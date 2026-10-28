param([switch]$NoBrowser, [switch]$Stop, [switch]$NonInteractive)
$ErrorActionPreference = 'Stop'
$data = Join-Path $env:LOCALAPPDATA 'Splortch/MediaServer'
$app = $PSScriptRoot
$eventName = 'Local\Splortch.MediaServer.Stop.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
if ($Stop) {
    try { $signal = [Threading.EventWaitHandle]::OpenExisting($eventName); $signal.Set() | Out-Null; $signal.Dispose(); exit 0 }
    catch [Threading.WaitHandleCannotBeOpenedException] { exit 0 }
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$exitCode = 0
$lease = $null; $signal = $null; $server = $null; $tray = $null; $timer = $null
try {
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    try { $lease = [IO.File]::Open((Join-Path $data 'tray-host.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] {
        if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:5050' }
        exit 0
    }
    $signal = New-Object Threading.EventWaitHandle($false, [Threading.EventResetMode]::ManualReset, $eventName)
    $signal.Reset() | Out-Null
    # Do not launch a second backend, including alongside a portable server.
    $lockPath = Join-Path $data '.server.lock'
    if (Test-Path -LiteralPath $lockPath) {
        $check = [IO.File]::Open($lockPath, 'Open', 'ReadWrite', 'None')
        $check.Dispose()
    }
    $configPath = Join-Path $data 'launcher.json'
    if (Test-Path -LiteralPath $configPath) { $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
    else {
        if ($NonInteractive) { throw 'Media folder has not been configured.' }
        $picker = New-Object Windows.Forms.FolderBrowserDialog
        $picker.Description = 'Choose the media folder the server is allowed to read. Add and scan it in the browser after setup.'
        try {
            if ($picker.ShowDialog() -ne 'OK') { throw 'Choose a media folder to complete first-time setup.' }
            $config = @{ media = $picker.SelectedPath }
            $media = [IO.Path]::GetFullPath($config.media).TrimEnd('\')
            foreach ($private in @($app, $data)) {
                $prefix = [IO.Path]::GetFullPath($private).TrimEnd('\')
                if ($media.Equals($prefix, [StringComparison]::OrdinalIgnoreCase) -or $media.StartsWith($prefix + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Keep media outside application and private server-data folders.'
                }
            }
            if ($media.Length -le 3) { throw 'Choose a media subfolder, not an entire drive.' }
            $config | ConvertTo-Json | Set-Content -LiteralPath $configPath -Encoding UTF8
        } finally { $picker.Dispose() }
    }
    if (-not (Test-Path -LiteralPath $config.media -PathType Container)) { throw 'The saved media folder is unavailable. Reconnect the drive or edit launcher.json in the server data folder.' }
    $probe = Get-Command ffprobe -ErrorAction SilentlyContinue
    $downloaded = Join-Path $data 'tools/ffmpeg-8.1.2-essentials_build/bin/ffprobe.exe'
    if ($probe) { $probePath = $probe.Source }
    elseif (Test-Path -LiteralPath $downloaded) { $probePath = $downloaded }
    else {
        if ($NonInteractive) { throw 'FFprobe is unavailable.' }
        $answer = [Windows.Forms.MessageBox]::Show('Scanning needs FFprobe. Download the pinned FFmpeg tools from GyanD on GitHub and verify their checksum? The tools have their own GPL/LGPL licenses. No media is uploaded. This download may take several minutes.', 'Media Server tools', 'YesNo', 'Question', 'Button2')
        if ($answer -ne 'Yes') { throw 'Install FFprobe on PATH, then start Media Server again. See the setup guide for offline installation.' }
        $archive = Join-Path $data ('media-tools-' + [Guid]::NewGuid().ToString('N') + '.zip')
        $progress = New-Object Windows.Forms.Form
        $progress.Text = 'Media Server - downloading tools'
        $progress.Width = 450; $progress.Height = 120; $progress.ControlBox = $false
        $label = New-Object Windows.Forms.Label
        $label.Text = 'Downloading and verifying FFprobe. Please wait...'; $label.AutoSize = $true; $label.Left = 20; $label.Top = 20
        $progress.Controls.Add($label); $progress.Show(); $progress.Refresh()
        try {
            Invoke-WebRequest -UseBasicParsing -TimeoutSec 240 -Uri 'https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip' -OutFile $archive
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec') { throw 'Media tools checksum mismatch; refusing to execute the download.' }
            Expand-Archive -LiteralPath $archive -DestinationPath (Join-Path $data 'tools') -Force
            if (-not (Test-Path -LiteralPath $downloaded)) { throw 'Verified archive did not contain FFprobe.' }
        } finally {
            Remove-Item -LiteralPath $archive -Force -ErrorAction SilentlyContinue
            $progress.Close(); $progress.Dispose()
        }
        $probePath = $downloaded
    }
    $env:MediaServer__DataDirectory = $data
    $env:MediaServer__AllowedRoots__0 = $config.media
    $env:MediaServer__FfprobePath = $probePath
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5050'
    foreach ($name in @('server.log', 'server-error.log')) {
        $path = Join-Path $data $name
        if (Test-Path -LiteralPath $path) { Move-Item -LiteralPath $path -Destination ($path + '.previous') -Force }
    }
    $server = Start-Process -FilePath (Join-Path $app 'Api.exe') -WorkingDirectory $app -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $data 'server.log') -RedirectStandardError (Join-Path $data 'server-error.log')
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        if ($server.HasExited) { throw 'Server startup failed. Check server.log and server-error.log in the data folder.' }
        try {
            $health = Invoke-RestMethod 'http://127.0.0.1:5050/health' -TimeoutSec 1
            if ($health.status -eq 'ok') { $ready = $true; break }
        } catch {}
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'The server was not ready after 60 seconds. Check logs and whether port 5050 is occupied.' }
    # A port occupied by another app must never be treated as our healthy backend.
    Start-Sleep -Milliseconds 300
    if ($server.HasExited) { throw 'The server exited. Check for another application using port 5050.' }
    $context = New-Object Windows.Forms.ApplicationContext
    $tray = New-Object Windows.Forms.NotifyIcon
    $tray.Icon = [Drawing.SystemIcons]::Application
    $tray.Text = 'Media Server - running locally'
    $menu = New-Object Windows.Forms.ContextMenuStrip
    $status = $menu.Items.Add('Running at 127.0.0.1:5050'); $status.Enabled = $false
    $open = $menu.Items.Add('Open Media Server'); $open.add_Click({ Start-Process 'http://127.0.0.1:5050' })
    $tokenItem = $menu.Items.Add('Show first-time setup token')
    $showToken = {
        $tokenPath = Join-Path $data 'setup-token'
        if (Test-Path -LiteralPath $tokenPath) {
            $form = New-Object Windows.Forms.Form
            $form.Text = 'Media Server - first-time setup'; $form.Width = 620; $form.Height = 180
            $help = New-Object Windows.Forms.Label
            $help.Text = 'Copy this local token into the browser setup form. Keep it private.'; $help.AutoSize = $true; $help.Left = 12; $help.Top = 12
            $box = New-Object Windows.Forms.TextBox
            $box.Text = (Get-Content -LiteralPath $tokenPath -Raw).Trim(); $box.ReadOnly = $true; $box.Left = 12; $box.Top = 45; $box.Width = 570
            $button = New-Object Windows.Forms.Button
            $button.Text = 'Open setup'; $button.Left = 12; $button.Top = 80; $button.Width = 120
            $button.add_Click({ Start-Process 'http://127.0.0.1:5050' })
            $form.Controls.AddRange(@($help, $box, $button))
            try { $form.ShowDialog() | Out-Null } finally { $form.Dispose() }
        } else { [Windows.Forms.MessageBox]::Show('Setup is already complete. Sign in with your local account.', 'Media Server') | Out-Null }
    }
    $tokenItem.add_Click($showToken)
    $folder = $menu.Items.Add('Open server data and logs'); $folder.add_Click({ Start-Process explorer.exe -ArgumentList ('"' + $data + '"') })
    $startupPath = Join-Path ([Environment]::GetFolderPath('Startup')) 'Media Server.lnk'
    $startup = $menu.Items.Add('Start when I sign in'); $startup.CheckOnClick = $true; $startup.Checked = Test-Path -LiteralPath $startupPath
    $startup.add_Click({
        try {
            if ($startup.Checked) {
                $shell = New-Object -ComObject WScript.Shell
                $link = $shell.CreateShortcut($startupPath)
                $link.TargetPath = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
                $link.Arguments = '-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "' + (Join-Path $app 'MediaServer.Tray.ps1') + '" -NoBrowser'
                $link.WorkingDirectory = $app; $link.Save()
            } else { Remove-Item -LiteralPath $startupPath -ErrorAction SilentlyContinue }
        } catch { $startup.Checked = Test-Path -LiteralPath $startupPath; [Windows.Forms.MessageBox]::Show('Could not change the sign-in shortcut.', 'Media Server') | Out-Null }
    })
    $quit = $menu.Items.Add('Stop server and exit'); $quit.add_Click({ $context.ExitThread() })
    $tray.ContextMenuStrip = $menu; $tray.Visible = $true
    $tray.add_DoubleClick({ Start-Process 'http://127.0.0.1:5050' })
    $timer = New-Object Windows.Forms.Timer
    $timer.Interval = 500
    $timer.add_Tick({
        if ($signal.WaitOne(0)) { $context.ExitThread() }
        elseif ($server.HasExited) {
            $tray.Text = 'Media Server - stopped'; $status.Text = 'Server stopped; see logs'; $timer.Stop()
            if ($NonInteractive) { $script:exitCode = 1; $context.ExitThread() }
            else { [Windows.Forms.MessageBox]::Show('The server stopped unexpectedly. Open server data and logs from the tray menu, then restart Media Server.', 'Media Server') | Out-Null }
        }
    })
    $timer.Start()
    if (-not $NoBrowser) { Start-Process 'http://127.0.0.1:5050'; & $showToken }
    [Windows.Forms.Application]::Run($context)
    if ($exitCode -ne 0) { throw 'The owned server stopped unexpectedly.' }
} catch {
    if ($NonInteractive) { [Console]::Error.WriteLine($_.Exception.Message) }
    else { [Windows.Forms.MessageBox]::Show(($_.Exception.Message + "`n`nData: " + $data), 'Media Server could not start', 'OK', 'Error') | Out-Null }
    exit 1
} finally {
    if ($timer) { $timer.Stop(); $timer.Dispose() }
    if ($tray) { $tray.Visible = $false; $tray.Dispose() }
    if ($server -and -not $server.HasExited) { $server.Kill(); $server.WaitForExit() }
    if ($signal) { $signal.Dispose() }
    if ($lease) { $lease.Dispose() }
}
