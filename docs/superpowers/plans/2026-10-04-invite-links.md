# Invite Links Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The tray's remote access wizard can set up remote access from an invite link issued by a hosted provisioning service.

**Architecture:** `Infrastructure/Hosted/` parses the link and talks to the service (protocol v1 in the spec), returning the existing `RemoteAccessResult`; the tray wizard gains an invite mode that applies the result through the existing settings path.

**Spec:** [docs/superpowers/specs/2026-10-04-invite-links-design.md](../specs/2026-10-04-invite-links-design.md).

## Global Constraints

- **Safety:** never launch the tray exe, Setup/Velopack, cloudflared, services or the real `claude`; never touch `%ProgramData%\AiChromeProxy`; no real network in tests (fake `HttpMessageHandler`); never call a real provisioning service.
- No real host names: `example.com` / `example.org` only.
- Architecture tests unchanged (Tray → Domain, Infrastructure only).
- Style: tabs; CRLF; UTF-8 without BOM; no `this.`; `_camelCase`; sorted usings; file-scoped namespaces; one type per file; block-form `using (...) { }`; `Async` suffix; XML docs like the surrounding code; StyleCop errors fail the build. English only.
- Exact values and copy: code `[A-Za-z0-9_-]{16,64}`; `https` only (plus `http://localhost` / `http://127.0.0.1` for development); path exactly `/invite/<code>` (optional trailing slash), query/fragment → invalid; timeouts 15 s (GET) and 90 s (redeem); subdomain `^[a-z0-9]([a-z0-9-]{0,30}[a-z0-9])?$`; mode labels **Use my Cloudflare account** / **I have an invite link**; fields **Invite link**, **Subdomain**, **Email**; button **Check**, **Set up**; texts "Your address: {sub}.{zone}", "Setting up remote access…", "The service answered {status}.", "Could not reach {host}: {reason}", "Enter the invite link you received.", "Use lowercase letters, digits and dashes (up to 32).", "Enter one email address.".
- Commands: build `dotnet build -c Release` → 0/0; class `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<NS.Class>"`; gate `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`.
- Commit only the task's files; no AI attribution.

---

### Task 1: Link and client

**Files:** `src/AiChromeProxy.Infrastructure/Hosted/InviteLink.cs` (`sealed record InviteLink(Uri Origin, string Code)`, `static bool TryParse(string? text, out InviteLink? link)`), `HostedProvisioningClient.cs` (ctor `HttpClient`; `Task<string> GetZoneAsync(InviteLink, CancellationToken)`, `Task<RemoteAccessResult> RedeemAsync(InviteLink, string subdomain, string email, string machineName, int port, CancellationToken)`), `HostedProvisioningException.cs`; registration only if the tray resolves Infrastructure services through DI (follow how `CloudflareApi` is created for the wizard); tests `InviteLinkTests`, `HostedProvisioningClientTests` (spec Testing list).
- [ ] Commit `Hosted: invite link parsing and provisioning client`.

### Task 2: Wizard invite mode and docs

**Files:** the remote access wizard view model and view in `src/AiChromeProxy.Tray` (mode choice, invite fields, Check, address preview, Set up → redeem → existing apply path; errors in the existing error area; busy state), tests for the view model (fake client seam — introduce the smallest interface or delegate needed; no `HttpClient` in VM tests), `docs/windows-host.md` (both ways; what an invite link is), README line.
- [ ] Commit `Tray: set up remote access from an invite link`.
