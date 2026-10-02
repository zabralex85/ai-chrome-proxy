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

Set two persistent user environment variables (PowerShell):

```powershell
[Environment]::SetEnvironmentVariable("CloudflareAccess__TeamDomain", "<team>.cloudflareaccess.com", "User")
[Environment]::SetEnvironmentVariable("CloudflareAccess__Audience", "<AUD tag>", "User")
```

Open a new terminal afterwards so the variables are visible. (The Windows host installer will manage this configuration later.)

Outside `Development` the Server refuses to start if either value is missing.

## 5. Run and verify

```powershell
dotnet run --project src/AiChromeProxy.Server -c Release --no-launch-profile
```

1. Open `https://code.example.com` in Chrome on another machine → Access login → page shows **Connected**; **Ping** shows the round-trip time.
2. On the home server, a request without a token is rejected:

   ```powershell
   curl.exe -i http://127.0.0.1:5180/   # HTTP/1.1 401 Unauthorized
   ```
