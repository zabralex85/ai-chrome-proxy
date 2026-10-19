# Private NuGet packages

Your project may use packages from a private feed that only your computer can reach. The server's Claude cannot restore them, so the Packages tab copies them from your NuGet cache to the home server. Nothing in your project changes.

## Using it

1. Open the folder as usual, and restore the project on your computer (`dotnet restore` or a build).
2. Open the **Packages** tab in the left panel and choose **Add NuGet cache folder**. Pick the NuGet cache, usually `%USERPROFILE%\.nuget\packages` (Chrome or Edge ask once; the browser remembers the folder, and **Allow** asks again after a restart).
3. After each sync pass the tab reads `obj/project.assets.json` next to every `*.csproj` of the repo and lists each package with its state: `On the server`, `Uploading n%`, `Public`, `Not on this computer`, `Too large to upload (...)` or `Failed: ...`. The summary reads `n private packages ready on the server · m missing`.

## What is uploaded

- Only **private** packages that the repo restored, at the exact versions. A package whose `.nupkg.metadata` names `https://api.nuget.org/v3/index.json` is public: the server downloads it from nuget.org like any restore, nothing is uploaded.
- The `.nupkg` goes first, then the other files of the package folder. The server checks the SHA-512 of the `.nupkg` against `contentHash` of the metadata; a mismatch is reported as `Changed while uploading; retried on the next pass.`
- Packages the server already holds are not sent again.

## Nothing in the project changes

The feature never writes to your folder, the mirror, `nuget.config`, `*.csproj`, `Directory.*.props` or Claude Code's own files. When `<Packages:Root>\nuget` exists, every run gets these environment variables (each unless `Agent:Env` already sets it):

| Variable | Value |
|---|---|
| `NUGET_FALLBACK_PACKAGES` | the configured or inherited value, then `;` and `<Packages:Root>\nuget` |
| `RestoreIgnoreFailedSources` | `true` |
| `NuGetAudit` | `false` |
| `WarningsNotAsErrors` | `NU1801` |

The fallback folder is consulted before any feed, so a private package restores without its feed. The two switches stop an unreachable private feed from failing the restore (`NU1801` is only a warning), and the audit would need that feed too.

The run's system prompt gets this line: "Private NuGet packages are provided through a fallback packages folder. Do not edit nuget.config, project files or Directory.*.props to fix a restore; if a private package is missing, tell the user to restore the project on their computer (the Packages tab lists it)."

## Limits

250 MB and 2 000 files per package; at most 2 000 packages per check; chunks of 16 KB; id up to 100 characters (`A-Z a-z 0-9 . _ -`), version up to 64 (`A-Z a-z 0-9 . + -`); both stored in lower case.

## Where packages live

`Packages:Root` (environment variable `Packages__Root`): by default `<DataDir>\packages` for the service, `data\packages` under the content root in development. Packages sit in `nuget\<id>\<version>\` (the layout of a NuGet cache). They are never synced back to your computer, and nothing removes them: delete a folder by hand to remove a package.

## Limitations

- `packages.config` projects have no `project.assets.json`; they are not covered.
- A project that moves `BaseIntermediateOutputPath` has its assets file elsewhere than `obj/`; it is not found.
- A project's own `WarningsNotAsErrors` in its project file replaces the `NU1801` value from the environment.
- npm and other ecosystems: later.

## Policy note

Private packages of your client are copied to your home server. Check that your company allows it before you add the cache folder.

## Manual checklist

- [ ] Pick the real `.nuget\packages`: the source row shows `Ready` and the rows match your restore.
- [ ] A repo with a private package restores and builds in a Claude run.
- [ ] `git status` on the mirror and in your folder shows no changed config.
