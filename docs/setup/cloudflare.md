# Cloudflare Tunnel + Access setup

The Server listens on `127.0.0.1` only. The only way in from outside is a Cloudflare Tunnel, and every request must carry a valid Cloudflare Access token — the Server checks it itself and answers `401` otherwise.

## 1. Install cloudflared (home server, Windows)

```powershell
winget install --id Cloudflare.cloudflared
```

## 2. Create the tunnel

In the Cloudflare dashboard: **Zero Trust → Networks → Tunnels → Create a tunnel** (type *Cloudflared*). Copy the install command it shows and run it in an elevated PowerShell — it installs `cloudflared` as a Windows service, so the tunnel is up after a reboot without anyone logging in.

Add a **public hostname**, e.g. `code.example.com` → service `http://127.0.0.1:5180`.

## 3. Protect it with Access

**Zero Trust → Access → Applications → Add an application → Self-hosted**:

- Application domain: the hostname from step 2.
- Policy: *Allow*, include your email address(es).

Open the application and copy its **Application Audience (AUD) tag**. Your team domain is shown under **Settings → Custom pages** (`<team>.cloudflareaccess.com`).

## 4. Configure the Server

Three values:

| Setting | Example | What it is |
|---|---|---|
| `CloudflareAccess:TeamDomain` | `<team>.cloudflareaccess.com` | Team domain from step 3 — bare host name, no `https://`, path, port or trailing slash. |
| `CloudflareAccess:Audience` | `<AUD tag>` | Application Audience (AUD) tag from step 3. |
| `Server:PublicHost` | `code.example.com` | Public hostname from step 2. The Server answers only to this `Host` header plus `127.0.0.1` / `localhost` (DNS-rebinding hardening); any other host gets `400`. |

**Installed as a Windows service** (see [windows-host.md](../windows-host.md)): enter them in the tray's **Settings…** window; it writes them to `%ProgramData%\AiChromeProxy\appsettings.json`.

**Running from source** (`dotnet run`): set persistent user environment variables (PowerShell), then open a new terminal so they are visible:

```powershell
[Environment]::SetEnvironmentVariable("CloudflareAccess__TeamDomain", "<team>.cloudflareaccess.com", "User")
[Environment]::SetEnvironmentVariable("CloudflareAccess__Audience", "<AUD tag>", "User")
[Environment]::SetEnvironmentVariable("Server__PublicHost", "code.example.com", "User")
```

Environment variables override the settings file. Outside `Development` the Server refuses to start if any of the three values is missing or a host name is not bare.

> **Moving from source to the Windows service:** remove these User-scope variables (and `ASPNETCORE_ENVIRONMENT` / `DOTNET_ENVIRONMENT` if you set them), for example `[Environment]::SetEnvironmentVariable("Server__PublicHost", $null, "User")`. They may reach the service too and would silently win over the values saved in **Settings…**; that window shows a warning while it sees any of them.

## 5. Run and verify

```powershell
dotnet run --project src/AiChromeProxy.Server -c Release --no-launch-profile
```

1. Open `https://code.example.com` in Chrome on another machine → Access login → page shows **Connected**; **Ping** shows the round-trip time.
2. On the home server, a request without a token is rejected:

   ```powershell
   curl.exe -i http://127.0.0.1:5180/   # HTTP/1.1 401 Unauthorized
   ```
