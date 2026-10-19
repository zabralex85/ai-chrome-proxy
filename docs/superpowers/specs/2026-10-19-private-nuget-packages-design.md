# Private NuGet packages — a Packages tab that brings them to the home server

Date: 2026-10-19. Builds on [3 sync](2026-10-03-sync-and-shell-design.md) (picked folder handles, chunked upload, path rules) and [4 Claude chat](2026-10-04-claude-chat-design.md) (`ClaudeRunner`, `ClaudeArguments`). Owner's request: client projects often depend on packages from a private NuGet feed (Artifactory/JFrog, Azure Artifacts, ...) that the home server cannot reach; Claude cannot restore, build or test there. The packages must be usable **without changing anything in the project's own configuration** (`nuget.config`, `*.csproj`, `Directory.*.props`), so the client's repository is never spoiled. npm is a later sub-project with its own spec.

## What NuGet offers

- The global packages folder (`%USERPROFILE%\.nuget\packages`) holds every restored package as `<id>/<version>/` (lower case) with the `.nupkg`, its `.nupkg.sha512`, the extracted files and `.nupkg.metadata` (`{"version":2,"contentHash":"<base64 sha512 of the .nupkg>","source":"<feed it came from>"}`). Chrome lets the user pick this folder (it is not under AppData).
- `obj/project.assets.json` (written by restore next to each project) lists the exact packages and versions a project uses (`libraries`, `type: "package"`, key `Id/Version`, `path` `id/version`).
- `NUGET_FALLBACK_PACKAGES` (environment) adds read-only fallback package folders: restore uses a package found there without downloading it.
- MSBuild reads environment variables as properties: `RestoreIgnoreFailedSources=true` turns an unreachable feed into a warning (NU1801) instead of an error.

**Verified on .NET SDK 10 (2026-10-19)** with a `nuget.config` listing an unreachable HTTPS feed (`artifactory.corp.invalid`) and nuget.org, a private package only in a fallback folder and a public one: `NUGET_FALLBACK_PACKAGES` alone fails (NU1301); adding `RestoreIgnoreFailedSources=true` restores and builds (the private package comes from the fallback folder, the public one is downloaded; NU1801 and NU1900 remain warnings); without the fallback folder it fails. With `TreatWarningsAsErrors` those warnings become errors; `NuGetAudit=false` and `WarningsNotAsErrors=NU1801` (environment) make it build again, NU1801 staying visible as a warning. `nuget.config` and the project file are unchanged in every case. The package folder layout is lower case: `<id>/<version>/<id>.<version>.nupkg`, `.nupkg.sha512`, `<id>.nuspec`, `.nupkg.metadata`, extracted files.

## Goal

A **Packages** tab next to **Explorer** in the left panel. The user adds a source of type **NuGet cache folder** (their `.nuget\packages` on the corporate machine). The browser finds the packages the open repo uses, and the private ones (not from nuget.org) are uploaded once to the home server. Every Claude run restores them from there through environment variables only; nothing in the repo, the user's folder or Claude Code's own files changes.

## Decisions

| Topic | Decision | Why |
|---|---|---|
| Tab | The left panel gets two tabs, **Explorer** (today's tree) and **Packages**. Packages shows **Sources** and **Packages this folder needs**. | Owner's request; one place to see what the server has. |
| Sources | **Add source** → type (only **NuGet cache folder** now; the list is typed so npm can join later) → folder picker. A row: type, folder name, state (**Ready** / **Needs permission** with **Allow** / **Not found**), **Remove**. Sources belong to this browser (handles in IndexedDB, like the repo folder), not to a repo: one cache serves every project. One source of each type (adding another replaces it). | A cache is per machine. |
| Needed packages | On folder open and on every sync pass the browser reads `obj/project.assets.json` next to every `*.csproj` the walk found (`obj` is excluded from sync but readable), re-reading a file only when its modification time changed. Parsing `libraries` (packages only; project references ignored) is pure C#. Per package it reads `<source>/<id>/<version>/.nupkg.metadata`: `source` on `api.nuget.org` → **Public**; folder missing in every source → **Not on this computer**; else a private candidate with its `contentHash`. A project without `project.assets.json` → **No restore yet** (build it on this computer first). `BaseIntermediateOutputPath` redirects are not followed (limitation). | Exact versions; only what this repo uses; public packages the server downloads itself. |
| Rows | Id, version, state: **On the server**, **Uploading 40%**, **Public**, **Not on this computer**, **Too large to upload (300.1 MB > 250 MB)** (size rounded up) or **Too large to upload (2 400 files > 2 000)**, **Failed: <reason>**. Summary line: "12 private packages ready on the server · 1 missing". | What Claude can and cannot restore, at a glance. |
| Protocol | Additive hub messages: `packages.nuget.have` `{packages:[{id, version, contentHash}]}` → `packages.nuget.missing` `{missing:[{id, version}]}`; `packages.nuget.begin` `{id, version, contentHash, files:[{path, size}]}` → `packages.nuget.begun` `{uploadId}`; `packages.nuget.chunk` `{uploadId, path, offset, data, last}` (base64url without padding, ≤ 16 KB raw, pipelined like `sync.chunk`; files in the order of `begin`; only the package's last chunk is answered: `packages.nuget.stored` `{id, version}` or the upload's first error). No removal: a package still in the cache would be uploaded again on the next pass. | Same proven chunk scheme; one hub. |
| Server store | `IPackageStore` (Application port) + file system (Infrastructure) under `Packages:Root` (default `<DataDir>\packages`, dev `data\packages`), NuGet in `nuget\<id>\<version>\` (lower case). An upload writes into `nuget\<id>\<version>.<tag>.tmp\`; when every file of `begin` arrived with its size, the sha512 of `<id>.<version>.nupkg` must equal `contentHash`, then the folder is renamed into place (a package restore can see is always complete). An existing package is never overwritten (`have` does not list it as missing). One upload per connection at a time; an unfinished temp folder is deleted when its connection closes and at start. | Immutable packages; integrity; no half packages. |
| Validation | Id: NuGet id characters (`[A-Za-z0-9._-]`, ≤ 100); version: `[A-Za-z0-9.+-]`, ≤ 64; both lower-cased. File paths: the sync path rules (normalized, no escape, segment limits). Limits: 250 MB and 2 000 files per package; `have` at most 2 000 packages. | Untrusted input from the protocol. |
| Agent | `ClaudeRunner` adds to every run's environment, when `Packages:Root\nuget` exists: `NUGET_FALLBACK_PACKAGES` = the value from `Agent:Env` (else the service's environment) + `;` + that folder; `RestoreIgnoreFailedSources=true`, `NuGetAudit=false` and `WarningsNotAsErrors=NU1801`, each unless `Agent:Env` sets it (a project that sets `WarningsNotAsErrors` itself without `$(WarningsNotAsErrors)` still fails on NU1801: documented limitation). Vulnerability audit stays on the user's computer. `--append-system-prompt` gains: "Private NuGet packages are provided through a fallback packages folder. Do not edit nuget.config, project files or Directory.*.props to fix a restore; if a private package is missing, tell the user to restore the project on their computer (the Packages tab lists it)." | Nothing in the project changes; Claude does not "fix" restore by editing the client's config. |
| Not in scope | npm (next sub-project); removing packages from the server (by hand in `Packages:Root` for now); several sources of one type; following `BaseIntermediateOutputPath`; `packages.config` projects. | YAGNI. |

## Errors

| Case | Result |
|---|---|
| Package over 250 MB or 2 000 files | Row "Too large to upload (… MB > 250 MB)" or "Too large to upload (… files > 2 000)"; not sent. |
| `contentHash` mismatch on the server | Upload discarded; row "Changed while uploading; retried on the next pass". |
| Source folder permission lost | Source row "Needs permission" with **Allow**; packages from it show "Not on this computer" until allowed. |
| Connection drops mid-upload | The server deletes the temp folder; the next pass starts the package again. |
| Unreadable `project.assets.json` | Project shown as "Could not read obj/project.assets.json"; others still work. |

## Security

- Private packages of the client end up on the home server: the docs say so and ask the owner to check their company's policy.
- Uploaded files are checked like synced ones (paths, sizes) and verified against the package's own hash before restore can see them.
- The store is outside the mirror and never synced back.

## Testing

xunit: `project.assets.json` parser (packages, project references ignored, invalid JSON); `.nupkg.metadata` classification; id/version/path validation; store (temp → hash → rename, size and file limits, existing package never overwritten, temp cleanup); handlers (order, last-chunk reply, first error); client packages engine against `LoopbackServer` with a fake source (needed set, Public/missing/private, upload, retry after a failure); `ClaudeRunner` environment (appends to an existing `NUGET_FALLBACK_PACKAGES`, keeps a user's `RestoreIgnoreFailedSources`, adds nothing without a store) and the system prompt line. E2E: an OPFS folder stands for the NuGet cache (a private package with `.nupkg.metadata`, a public one), the repo has `obj/project.assets.json`; **Add source** → the private row becomes "On the server" and its files exist in `Packages:Root`, the public row says "Public"; screenshots dark/light, looked at.

## Done when

- On the real install: a repo using a private package restores and builds in a Claude run with nothing changed in the repo.
- CI green; gate ≥ 85 %; E2E green; `docs/packages.md` (tab, what is uploaded, nothing in the project changes, environment variables, limits, policy note, manual checklist) linked from `CLAUDE.md`.
