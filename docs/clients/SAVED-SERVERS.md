# Saved servers and switching

The dedicated desktop client (Windows, Linux and macOS) and Android phone client have a **Saved servers** list. The server's web interface stays attached to the server that serves it; use browser bookmarks for multiple web servers. Android TV and iOS do not yet have shipping clients.

1. On the connection screen, enter a server's HTTPS address and an optional name, such as **Home** or **Family**.
2. Select **Connect**. A successful API compatibility check adds the address to this device's saved list. A failed connection is not saved. Complete first-time account setup in the server web interface first.
3. Sign in with that server's account. Saving a name/address and remembering an authenticated session are separate: the existing remember-session option controls secure session storage.
4. To switch, use **Change server** in the desktop sidebar or **Change server / sign out** on Android. This stops playback, attempts to save its current position to the original server, signs out and returns to the picker. A network failure can prevent the last save or remote revocation; earlier server progress remains and the client reports revocation problems. No media/progress state is reused on another server.
5. Select **Use server** (desktop) or **Use NAME** (Android), then **Connect**. You choose explicitly when to connect; saved entries never initiate background connections.
6. To rename a saved entry, select it, change the name and connect successfully again. The normalized server address identifies the entry, preventing duplicates. Use **Forget** to remove its saved address. This removes device-local metadata and clears that server's remembered local session; it does not delete server accounts, libraries or other devices' sessions. Revoke unwanted sessions in server settings.

Up to 20 connections are kept, most recently used first. Names are limited to 80 characters. Corrupt, excessive or invalid local metadata is ignored; you can still type an address manually. Desktop stores only the name and address in its WebView's local storage. Android stores only those fields in the app's private preferences. Passwords, cookies and API keys are never added to the address book. Existing secure session mechanisms remain separate and bound to the original server address.

The previous single-address preference remains usable; Android migrates it into the list when no catalog exists. Desktop still pre-fills its previous address, which joins the list after a successful connection. Forgetting an address clears its local secure-store entry without contacting the server. It cannot revoke a remote session while offline; revoke unwanted sessions through the server's session-management settings.

Remote HTTP, credentials embedded in URLs, URL paths, query strings and fragments remain rejected by native clients. Use a trusted HTTPS origin. The Android debug emulator and local-loopback development exceptions are unchanged. A changed IP/hostname is a different origin and requires connecting/signing in again. A router DHCP reservation can keep a home server's address stable; see [home-network setup](../deployment/HOME-NETWORK.md).
