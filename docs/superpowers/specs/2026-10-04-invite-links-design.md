# Invite links — remote access from a hosted provisioning service

Date: 2026-10-04. Builds on [2b remote access wizard](2026-10-03-remote-access-wizard-design.md) (`RemoteAccessProvisioner`, the tray wizard, the settings it writes). A hosted service can set up remote access for a user who has no Cloudflare account or domain of their own: the user pastes an invite link into the tray instead of an API token.

This repository holds only the client side and the protocol. The service is separate (any implementation of the protocol below); nothing here names a real host.

## Goal

The remote access wizard offers two ways: **Use my Cloudflare account** (today's API token flow, unchanged) and **I have an invite link**. With an invite link the user enters the link, picks a subdomain and the email they will sign in with; the service creates the tunnel, DNS record and Access application in its own account and returns the same four settings the token flow produces. The tray writes them the same way.

## Protocol (version 1)

The invite link is `https://<service host>/invite/<code>`; `<code>` is 16–64 characters `[A-Za-z0-9_-]`. The service's base address is the link's origin (`https://<service host>`). Only `https` links are accepted (`http://localhost` too, for development). All bodies are JSON (camelCase, UTF-8); a request body is sent with `Content-Length` (at most 4 KB, no chunked encoding).

| Call | Request | Success | Errors |
|---|---|---|---|
| `GET /v1/invites/<code>` | — | `200 { "zone": "example.com" }` — the domain subdomains are created under | `404 { "error": "…" }` unknown, used or expired code; `429` |
| `POST /v1/invites/<code>/redeem` | `{ "subdomain": "alice", "email": "alice@example.org", "machineName": "HOMEPC", "port": 5180 }` | `200 { "teamDomain": "…", "audience": "…", "publicHost": "alice.example.com", "tunnelToken": "…" }` | `400`/`404`/`409`/`429`/`5xx` with `{ "error": "<message for the user>" }` |

`error` texts are shown to the user as they are. A redeem may take up to 120 s; clients wait at least 130 s. Retrying a failed redeem with the same code is allowed (the service makes it converge).

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Where | `Infrastructure/Hosted/`: `InviteLink.TryParse(string, out InviteLink?)` (origin + code, rules above; trims whitespace), `HostedProvisioningClient` (`HttpClient`; `GetZoneAsync(link)`, `RedeemAsync(link, subdomain, email, machineName, port)` → `RemoteAccessResult`; non-2xx → `HostedProvisioningException` with the `error` text, or "The service answered {status}." when there is none; timeouts 15 s for GET, 130 s for redeem (longer than the service may hold the invite); network failure → "Could not reach {host}: {reason}"). | Same layer as the Cloudflare client; testable with a fake handler. |
| Wizard | A choice at the top: **Use my Cloudflare account** / **I have an invite link**. Invite mode: **Invite link** field → on leaving it (or **Check**) the tray calls `GetZoneAsync` and shows "Your address: `<subdomain>.<zone>`" next to the **Subdomain** field; **Email** field (one address); **Set up** runs the redeem with progress "Setting up remote access…" and then applies the result exactly like the token flow (same settings writer, same restart of the service). Subdomain rule: `^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$` checked before calling (the service checks again). Errors show in the wizard's existing error area. | One wizard, one apply path. |
| Secrets | The tunnel token is written like today (settings file, never logged; `RemoteAccessResult.ToString` already hides it). The invite code is not stored. | Same handling as the token flow. |
| Machine name / port | `Environment.MachineName` and the configured Server port, as the token flow uses. | Tunnel per machine. |

## Testing

xunit: `InviteLink` table (valid, http rejected, localhost http allowed, bad code chars, short/long code, extra path, query ignored or rejected — rejected); client over a fake `HttpMessageHandler`: zone ok, 404 with error text, 500 without body, redeem ok → result, redeem error text, timeout message, request JSON shape (camelCase, fields); wizard view model: mode switch, subdomain validation, address preview after the zone lookup, success applies the four settings through the existing writer (fake), error shown, busy state. No real network.

## Done when

- The wizard sets up remote access from an invite link against a service that follows the protocol; the token flow is unchanged.
- CI green; gate ≥ 85 %; `docs/windows-host.md` explains both ways.
