# Connect from another device at home

`localhost` and `127.0.0.1` mean the device you are currently using. Other devices need your server PC's home-network address, for example `http://192.168.1.50:5050`.

## Windows server: switch home access on

1. Install and start Media Server. Complete account setup on the server PC at `http://127.0.0.1:5050` first.
2. Open **Media Server Control** from Windows search or its tray icon.
3. Select **Stop server**. This interrupts active playback; your accounts, libraries and progress stay saved.
4. Select **Network settings**. Choose **Home network** to listen on currently assigned private IPv4 adapters, or **Specific home IP** and select one assigned address. Localhost remains available in both modes.
5. Select **Save settings**, close the dialog, then **Start server**. The control window lists the addresses you can copy to other devices. Saved settings are reused after restarting Windows.
6. On your trusted home network, allow the server through Windows Firewall using the limited rule below. Do not disable the firewall or allow Public networks. Your normal Windows user may need an administrator to add this rule.
7. Connect a phone or another computer to the same home network. Open one of the listed addresses in its browser. Sign in using a server account. A phone's mobile data or guest Wi-Fi may not be able to reach your home devices.
8. Check browsing, playback and resume. If unavailable, verify the server is running, the IP is current, Wi-Fi isolation is off for these devices, and the PC network profile is Private. Check `http://YOUR-IP:5050/health` first.

To turn home access off: **Stop server → Network settings → This computer → Save settings → Start server**. This removes the home listeners and their allowed hosts. The app does not change your firewall or router automatically.

Only RFC1918 private IPv4 addresses already assigned to the computer are supported by this control. Public IPs, wildcard listeners, hostnames, link-local addresses and IPv6 are not offered. Home mode can include VPN/virtual private adapters: choose **Specific home IP** if you want only one interface. If a saved address disappears, the server stays stopped and the interactive controller remains available to choose another address. Console/unattended launches fail with a useful error instead of silently exposing a different interface.

## Windows Firewall: trusted home subnet only

Set your home connection to **Private** in Windows network settings, only if you trust that network. In an **administrator PowerShell** window, replace `YOUR_WINDOWS_USER` with the account that installed the server and run:

```powershell
New-NetFirewallRule -Name 'Splortch.MediaServer.Home' -DisplayName 'Media Server - trusted home network' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5050 -Program 'C:\Users\YOUR_WINDOWS_USER\AppData\Local\Programs\Splortch\MediaServer\app\Api.exe' -Profile Private -RemoteAddress LocalSubnet -EdgeTraversalPolicy Block
```

If you installed elsewhere, use the actual `Api.exe` path. If the rule already exists, inspect it in Windows Defender Firewall with Advanced Security rather than adding duplicates. The program path, TCP port, Private profile and local-subnet scope all matter. To remove this rule later:

```powershell
Remove-NetFirewallRule -Name 'Splortch.MediaServer.Home'
```

Do not forward port 5050 on your router. Binding a private address is not a firewall: a VPN, route or router forwarding rule may still make that address reachable. This feature does not configure UPnP, internet access, DNS, certificates, or router settings.

## Keep a dedicated address

Selecting an address in the app controls where the server listens; it does **not** assign that address to Windows.

For a stable home address, use your router's **DHCP reservation**, **Address reservation**, or **Static lease** page:

1. Find the server PC in the router's connected devices. Match its Ethernet or Wi-Fi adapter; each adapter has a different MAC address.
2. Reserve its current private address for that adapter, following the router's instructions. Avoid assigning an address already used by another device.
3. Reconnect the PC if the router asks, then confirm the address in `ipconfig`.
4. Select that assigned address under **Specific home IP** in Server Control. Keep using the same Ethernet/Wi-Fi adapter.

This avoids changing DNS, gateway and subnet settings manually in Windows. A dedicated **public** static IP is a different service supplied by an ISP; it is not needed for use around the house. For secure access away from home, use [the VPN/HTTPS guide](REMOTE-ACCESS.md).

## Security and native clients

Home HTTP is unencrypted, including login credentials and media. Use it only on a network you trust. Accounts, authorization, host validation and media path restrictions remain enabled; guest access is still a separate opt-in setting. Use HTTPS for sensitive or shared networks.

The native Windows and Android clients retain their existing HTTPS requirements for non-loopback connections. Do not disable certificate validation to make them connect. Configure a trusted HTTPS origin using the [remote-access guide](REMOTE-ACCESS.md), even when that origin is only reachable within your home. Browsers can use the explicit trusted-home HTTP mode above.

## Console launcher

The Windows console launcher reads the same `%LOCALAPPDATA%\Splortch\MediaServer\launcher.json` configuration. Prefer the control window; advanced users can preserve the existing `media` field and add:

```json
{
  "media": "D:\\Media",
  "networkMode": "specific",
  "networkAddress": "192.168.1.50"
}
```

`networkMode` accepts `local` (default for old configurations), `home`, or `specific`. `networkAddress` is used only for `specific`. Finish first-time local setup before selecting a home mode. Restart the server after changing this file.

## Linux, macOS and Docker

The new graphical control is for Windows server installations. Other deployments retain their existing configuration: explicitly set `ASPNETCORE_URLS` and `AllowedHosts`, retain loopback if needed, and restrict the host firewall. For example, with an already assigned private address:

```sh
ASPNETCORE_URLS='http://127.0.0.1:5050;http://192.168.1.50:5050' \
AllowedHosts='localhost;127.0.0.1;192.168.1.50' ./Api
```

Use the normal data directory and media-root configuration for your installation. Do not run a second server against the same database. Compose publishes only loopback by default: deliberately change the published host address to the server's assigned private IP and set the matching exact `AllowedHosts`; retain the container's existing listener configuration. Finish setup locally first and apply firewall rules appropriate to your host. These manual deployment paths are not automated by the Windows control.
