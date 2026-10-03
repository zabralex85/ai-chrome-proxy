# Back Channel and Project Settings (3b) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Server-side edits of the mirror reach the picked folder in Chrome within seconds under a hash-guard, edits on both sides become conflicts the user resolves, the periodic full manifest stops overwriting server changes, and each project has settings stored (with the sync state) in SQLite.

**Architecture:** The server keeps a *base* hash per file (SQLite, `IProjectStore`) and decides every path with the pure `SyncDecision.Decide(client, mirror, base, baselined)`. `SyncSession` uses it for manifests, deltas and file-watcher events and pushes `sync.remote` to the client; the client pulls content with `sync.fetch`/`sync.data`, writes under the hash-guard through new `IFolderAccess` methods (`fsaccess.ts`), and confirms with `sync.ack`. `IgnoreRules` moves to Domain so both ends exclude the same files. Project settings ride on `sync.opened` and `project.settings.*`.

**Tech Stack:** .NET 10, ASP.NET Core SignalR, Blazor WebAssembly, `Microsoft.Data.Sqlite` (new, Infrastructure only), `FileSystemWatcher`, File System Access API (`createWritable`, `removeEntry`, `requestPermission({mode:'readwrite'})`) in strict TypeScript, xunit v3 (`FakeTimeProvider`, existing `LoopbackServer`, `FakeFolder`, `FakeTransport`, `ListLogger<T>`, `TempRootCleanup`), Reqnroll + Playwright E2E.

**Spec:** [docs/superpowers/specs/2026-10-03-back-channel-design.md](../specs/2026-10-03-back-channel-design.md). Background: [3a spec](../specs/2026-10-03-sync-and-shell-design.md).

## Global Constraints

- **Safety while implementing:** never run the tray, Setup/Velopack, cloudflared, real services or UAC; never touch the real `%ProgramData%\AiChromeProxy` or `%LocalAppData%\AiChromeProxy` (a live install with a running service exists on this machine; port 5180 is taken — local runs use e.g. `Server__Port=5197` and never set `AICP_DATA_DIR` to the real data directory). Tests use temp folders under `TempRootCleanup.Root`.
- Dependency direction enforced by `tests/AiChromeProxy.Tests/Architecture` (must stay green unchanged): Domain (BCL only) ← Application ← Infrastructure ← Server; Client → Domain only. `Microsoft.Data.Sqlite` only in Infrastructure.
- Style: tabs; CRLF; UTF-8 without BOM; no `this.`; `_camelCase` fields; sorted usings; file-scoped namespaces; one type per file (an enum may share); block-form `using (...) { }` only (`UsingStatementStyleTests`); `Async` suffix; XML docs in the style of the surrounding code; StyleCop errors fail the build — fix code, never the ruleset. English only. No personal paths or domains (example.com).
- TypeScript strict, no `any`, no hand-written `.js` (`BrowserScriptTests`); JS only for the File System Access API; interop wrappers `[ExcludeFromCodeCoverage]` without logic.
- `Envelope` JSON is a public camelCase contract: only new message types and new optional fields.
- Exact values from the spec:
  - Decision: `C == M` → in sync (B := C); `M == B` → upload (C present) / delete mirror file (C absent); `C == B` → push; else → push (conflict candidate). Not baselined → client wins (upload / delete mirror file). Baselined at the end of the first full manifest.
  - SQLite: file `<DataDir>\aicp.db` (dev `data\aicp.db` under the content root), `Projects:Database` overrides; `journal_mode=WAL`, `synchronous=NORMAL`, `PRAGMA user_version`; tables `repo(name PK, baselined)`, `base(repo, path COLLATE NOCASE, sha256, PK(repo, path))`, `setting(repo, key, value, PK(repo, key))`; one transaction per manifest page / delta.
  - Watcher: coalesce 500 ms; drop `.aicp-tmp`, excluded and invalid paths; overflow → check every mirror file.
  - Messages: `sync.opened {repo, settings?}`; push `sync.remote {repo, changes[{path, sha256?, size, base?}]}` (≤ 500 changes and ≤ 24 000 bytes per page); `sync.fetch {repo, path, offset}` → `sync.data {repo, path, offset, data, last, sha256?}` (≤ 16 KB raw, base64url); `sync.ack {repo, path, sha256?}` → echo; `project.settings.get {repo}` / `project.settings.set {repo, settings}` → `project.settings {repo, settings}`; missing file on fetch → `not_found`.
  - Settings: `excludes` (string, `.gitignore` syntax, after built-ins and `.gitignore`, cannot re-include a built-in), `applyServerChanges` (bool, default true); unknown keys kept.
  - Client guard: now-hash == change `sha256` → ack only; == `base` (absent when `base` null) → write/delete, ack; else conflict. Never write a path the client excludes or `SyncPath` rejects. Conflict text preview ≤ 256 KB.
  - Copy: "Connection lost — reconnecting…"; "N server changes — Allow writing" (button **Allow writing**); **Apply** / **Apply all**; **Keep mine** / **Take server's**; tab titles **Conflict**, **Project settings**.
- Commands (repo root):
  - Build: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
  - One class: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<Namespace.ClassName>"`.
  - Gate: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → `failed: 0`, exit 0.
  - E2E (Task 9 only): `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E`.
- Commit only the task's files; no AI attribution in commit messages.

## Decisions taken while planning

1. **Push order.** Pushes are sent from inside the handler through `EnvelopeContext.SendAsync`, so a `sync.remote` may arrive before the reply of the page that caused it. The client queues remote changes independently of the cycle, so order does not matter (the spec's "in reply-order" is dropped).
2. **Session gets the context.** `SyncSession.HandleAsync(Envelope, EnvelopeContext, CancellationToken)`; the session keeps the last context's `SendAsync` as its push channel (one connection per session).
3. **Paging helper.** `ManifestPlanner.Pages<T>` moves to Domain as `SyncPages.Split<T>` (the server pages `sync.remote` the same way); `ManifestPlanner` calls it.
4. **Server ignore rules.** Built per decision batch from the mirror's `.gitignore` (read from disk; absent = none) plus the `excludes` setting. Cheap (one small file).
5. **Watcher ownership.** `SyncSessions` ref-counts one `IMirrorWatcher` watch per repo (first session that opens it starts it, last close/repo change stops it) and fans changes out to the sessions with that repo.
6. **Client structure.** The back-channel half of the engine goes into `SyncEngine.Remote.cs` (`partial class SyncEngine`) so `SyncEngine.cs` does not grow past ~1 000 lines; remote changes are applied at the start of each cycle (inside the `_cycleRunning` guard, so a write never races a scan of the same engine), and a push wakes the loop.
7. **Permission check.** `IFolderAccess.HasWriteAccessAsync()` = `queryPermission({mode:'readwrite'}) === 'granted'`; `RequestWriteAccessAsync()` from the **Allow writing** click.
8. **Fetch hash.** The server sends `sha256` on the last `sync.data` chunk (`GetHashAsync` after reading); the client compares the assembled bytes' SHA-256 with the change's `sha256` and drops a mismatch.
9. **Settings shape.** `ProjectSettings(string? Excludes, bool? ApplyServerChanges, Dictionary<string, JsonElement>? Extra)` with `[JsonExtensionData]` on `Extra` keeps unknown keys; stored one row per key as JSON text.

---

### Task 1: "Connection lost — reconnecting…" instead of the SignalR exception

**Files:**
- Modify: `src/AiChromeProxy.Client/Sync/SyncEngine.cs` (catch block of `SyncOnceAsync`)
- Test: `tests/AiChromeProxy.Tests/Client/SyncEngineTests.cs`

**Interfaces:** Produces `public const string ConnectionLost = "Connection lost — reconnecting…";` on `SyncEngine`.

- [ ] **Step 1: Failing test** — add to `SyncEngineTests`:

```csharp
[Fact]
public async Task SendFailsWhileDisconnecting_ProblemIsConnectionLost()
{
	_folder.Write("a.txt", "a");
	await OpenAsync();
	_server.Transport.Reply = request =>
	{
		// SignalR throws this when the connection drops between the state check and the send.
		_server.Transport.SetState(TransportState.Reconnecting);
		throw new InvalidOperationException("The 'SendCoreAsync' method cannot be called if the connection is not active");
	};

	await _engine.SyncOnceAsync(Ct);

	Assert.Equal(SyncEngine.ConnectionLost, _engine.Problem);
	Assert.Contains(_engine.Activity, a => a.Text == SyncEngine.ConnectionLost);
	Assert.DoesNotContain(_engine.Activity, a => a.Text.Contains("SendCoreAsync", StringComparison.Ordinal));
}
```

(Use the existing `OpenAsync` helper of the class; if `FakeTransport.Reply` is invoked synchronously and the throw escapes `SendAsync`, that is exactly the real behaviour.)

- [ ] **Step 2:** Run `--filter-class "AiChromeProxy.Tests.Client.SyncEngineTests"` → the new test FAILS (Problem is the exception text).
- [ ] **Step 3: Implement** — in the `catch` of `SyncOnceAsync`, replace `Problem = Blocked ? ... : ex.Message;` with:

```csharp
var message = transport.State != TransportState.Connected ? ConnectionLost : ex.Message;
Problem = Blocked ? $"{message} Sync is paused: change the folder or restore access." : message;
```

and add the constant next to `AccessLost` (public, so tests and UI use it).
- [ ] **Step 4:** Build + run the class → all pass.
- [ ] **Step 5: Commit** `Sync: say "Connection lost — reconnecting…" instead of the SignalR error`.

---

### Task 2: Shared Domain pieces — IgnoreRules, paging, new payloads

**Files:**
- Move: `src/AiChromeProxy.Client/Sync/IgnoreRules.cs` → `src/AiChromeProxy.Domain/Sync/IgnoreRules.cs` (namespace `AiChromeProxy.Domain.Sync`)
- Move: `tests/AiChromeProxy.Tests/Client/IgnoreRulesTests.cs` → `tests/AiChromeProxy.Tests/Domain/IgnoreRulesTests.cs`
- Create: `src/AiChromeProxy.Domain/Sync/SyncPages.cs`, `RemoteChange.cs`, `SyncRemotePayload.cs`, `SyncFetchPayload.cs`, `SyncDataPayload.cs`, `SyncAckPayload.cs`, `ProjectSettings.cs`, `ProjectSettingsPayload.cs`
- Modify: `src/AiChromeProxy.Domain/Sync/SyncOpenPayload.cs`, `src/AiChromeProxy.Domain/MessageTypes.cs`, `src/AiChromeProxy.Client/Sync/ManifestPlanner.cs`, every `using`/reference to `AiChromeProxy.Client.Sync.IgnoreRules` (`rg -n IgnoreRules src tests`)
- Test: `tests/AiChromeProxy.Tests/Domain/IgnoreRulesTests.cs` (moved + new cases), `tests/AiChromeProxy.Tests/Domain/BackChannelPayloadTests.cs`

**Interfaces (Produces):**

```csharp
// IgnoreRules: unchanged API plus extra excludes (after .gitignore; a '!' there cannot re-include a built-in, as for .gitignore).
public static IgnoreRules Create(string? gitignore, string? excludes = null);

public static class SyncPages
{
	/// <summary>Splits items into pages of at most SyncLimits.MaxPageEntries items and SyncLimits.MaxPageBytes bytes (by size()).</summary>
	public static IEnumerable<IReadOnlyList<T>> Split<T>(IEnumerable<T> items, Func<T, int> size);
}

public sealed record RemoteChange(string Path, string? Sha256, long Size, string? Base);
public sealed record SyncRemotePayload(string Repo, IReadOnlyList<RemoteChange> Changes);
public sealed record SyncFetchPayload(string Repo, string Path, long Offset);
public sealed record SyncDataPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null);
public sealed record SyncAckPayload(string Repo, string Path, string? Sha256);
public sealed record SyncOpenPayload(string Repo, ProjectSettings? Settings = null);

public sealed record ProjectSettings
{
	public static readonly ProjectSettings Default = new();
	public string? Excludes { get; init; }
	public bool? ApplyServerChanges { get; init; }
	/// <summary>Keys later sub-projects add (agent command, model, permissions): kept as they are.</summary>
	[JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; init; }
	/// <summary>Effective value (default true).</summary>
	[JsonIgnore] public bool ApplyServerChangesOrDefault => ApplyServerChanges ?? true;
}
public sealed record ProjectSettingsPayload(string Repo, ProjectSettings? Settings = null);

// MessageTypes (with XML docs like the existing ones):
public const string SyncRemote = "sync.remote";
public const string SyncFetch = "sync.fetch";
public const string SyncData = "sync.data";
public const string SyncAck = "sync.ack";
public const string ProjectSettingsGet = "project.settings.get";
public const string ProjectSettingsSet = "project.settings.set";
public const string ProjectSettings = "project.settings";
```

`ProjectSettings` uses `System.Text.Json.Serialization` attributes only (BCL). Note `MessageTypes.ProjectSettings` and the record `Domain.Sync.ProjectSettings` live in different namespaces; refer to the constant as `MessageTypes.ProjectSettings`.

- [ ] **Step 1: Move** `IgnoreRules` with `git mv` (file and test), change namespaces, fix references, build → green, run `IgnoreRulesTests` → pass.
- [ ] **Step 2: Failing tests** in `IgnoreRulesTests`:

```csharp
[Theory]
[InlineData("docs/big/", "docs/big/a.bin", true)]
[InlineData("*.tmp", "src/x.tmp", true)]
[InlineData("!.env", ".env", true)]          // cannot re-include a built-in
[InlineData("!keep.log", "keep.log", false)] // re-includes what .gitignore excluded
public void Excludes_AppliedAfterGitignore(string excludes, string path, bool ignored)
{
	var rules = IgnoreRules.Create("*.log\n", excludes);

	Assert.Equal(ignored, rules.IsIgnored(path));
}

[Fact]
public void Excludes_WithoutWildcards_AreSkippedDirectories()
{
	Assert.Contains("/docs/big", IgnoreRules.Create(null, "/docs/big/").SkipDirectories);
}
```

and `BackChannelPayloadTests`:

```csharp
[Fact]
public void RemoteChange_IsCamelCase_NullsKept()
{
	var json = JsonSerializer.Serialize(new SyncRemotePayload("r", [new RemoteChange("a.txt", null, 0, "abc")]), JsonSerializerOptions.Web);

	Assert.Equal("""{"repo":"r","changes":[{"path":"a.txt","sha256":null,"size":0,"base":"abc"}]}""", json);
}

[Fact]
public void ProjectSettings_KeepsUnknownKeys()
{
	var settings = JsonSerializer.Deserialize<ProjectSettings>("""{"excludes":"x/","model":"opus"}""", JsonSerializerOptions.Web)!;

	Assert.Equal("x/", settings.Excludes);
	Assert.True(settings.ApplyServerChangesOrDefault);
	Assert.Contains("\"model\":\"opus\"", JsonSerializer.Serialize(settings, JsonSerializerOptions.Web), StringComparison.Ordinal);
}

[Fact]
public void SyncOpened_WithoutSettings_StaysCompatible()
{
	Assert.Equal("""{"repo":"r","settings":null}""", JsonSerializer.Serialize(new SyncOpenPayload("r"), JsonSerializerOptions.Web));
	Assert.Null(JsonSerializer.Deserialize<SyncOpenPayload>("""{"repo":"r"}""", JsonSerializerOptions.Web)!.Settings);
}

[Fact]
public void Split_RespectsEntryAndByteLimits()
{
	var pages = SyncPages.Split(Enumerable.Range(0, 1_200), _ => 100).ToList();

	Assert.All(pages, p => Assert.True(p.Count <= SyncLimits.MaxPageEntries && p.Count * 100 <= SyncLimits.MaxPageBytes));
	Assert.Equal(1_200, pages.Sum(p => p.Count));
}
```

- [ ] **Step 3:** Run → fail (missing types/overload).
- [ ] **Step 4: Implement.** `IgnoreRules.Create(gitignore, excludes)`: parse `excludes` like `.gitignore` and append its rules to the `.gitignore` rule list (so "last match wins" covers both and built-ins stay un-reincludable exactly as today); `SkipDirectories` is computed over the combined list. `SyncPages.Split` = the body of `ManifestPlanner.Pages` moved (keep its XML doc); `ManifestPlanner.Pages` is deleted and its callers use `SyncPages.Split`. Payload records and constants as in Interfaces.
- [ ] **Step 5:** Build + full test run (`IgnoreRulesTests`, `ManifestPlannerTests`, `BackChannelPayloadTests`, Architecture) → pass.
- [ ] **Step 6: Commit** `Domain: shared ignore rules, paging and back-channel payloads`.

---

### Task 3: The decision table

**Files:**
- Create: `src/AiChromeProxy.Application/Sync/SyncDecision.cs`, `src/AiChromeProxy.Application/Sync/SyncAction.cs`
- Test: `tests/AiChromeProxy.Tests/Application/SyncDecisionTests.cs`

**Interfaces (Produces):**

```csharp
public enum SyncAction
{
	/// <summary>Both sides have the same content (or neither has the file): the base becomes it.</summary>
	InSync,
	/// <summary>Only the client changed: request its upload.</summary>
	Upload,
	/// <summary>The client deleted the file and the mirror still has the agreed version: delete the mirror file.</summary>
	DeleteMirror,
	/// <summary>The mirror changed (alone or together with the client): tell the client (sync.remote).</summary>
	Push,
}

public static class SyncDecision
{
	/// <param name="client">The client's SHA-256, null when the client has no file.</param>
	/// <param name="mirror">The mirror's SHA-256, null when the mirror has no file.</param>
	/// <param name="baseHash">The last agreed SHA-256, null when there is none.</param>
	/// <param name="baselined">Whether the repo has had its first full manifest since bases are kept.</param>
	public static SyncAction Decide(string? client, string? mirror, string? baseHash, bool baselined);
}
```

- [ ] **Step 1: Failing test** (complete table):

```csharp
public sealed class SyncDecisionTests
{
	[Theory]
	// both equal
	[InlineData("a", "a", null, true, SyncAction.InSync)]
	[InlineData("a", "a", "x", true, SyncAction.InSync)]
	[InlineData(null, null, "x", true, SyncAction.InSync)]
	// only the client changed
	[InlineData("b", "a", "a", true, SyncAction.Upload)]
	[InlineData("b", null, null, true, SyncAction.Upload)]   // new on the client (or Keep mine after a server delete)
	[InlineData(null, "a", "a", true, SyncAction.DeleteMirror)]
	// only the mirror changed
	[InlineData("a", "b", "a", true, SyncAction.Push)]
	[InlineData("a", null, "a", true, SyncAction.Push)]      // deleted on the server
	[InlineData(null, "a", null, true, SyncAction.Push)]     // created on the server
	// both changed: conflict candidates, pushed
	[InlineData("b", "c", "a", true, SyncAction.Push)]
	[InlineData("b", "c", null, true, SyncAction.Push)]      // created on both sides
	[InlineData(null, "c", "a", true, SyncAction.Push)]      // deleted here, modified there
	[InlineData("b", null, "a", true, SyncAction.Push)]      // modified here, deleted there
	// before the baseline the client wins (3a)
	[InlineData("b", "c", "a", false, SyncAction.Upload)]
	[InlineData("b", "c", null, false, SyncAction.Upload)]
	[InlineData(null, "c", null, false, SyncAction.DeleteMirror)]
	[InlineData("a", "a", null, false, SyncAction.InSync)]
	public void Decide(string? client, string? mirror, string? baseHash, bool baselined, SyncAction expected)
	{
		Assert.Equal(expected, SyncDecision.Decide(client, mirror, baseHash, baselined));
	}
}
```

- [ ] **Step 2:** Run → fail.
- [ ] **Step 3: Implement**

```csharp
public static SyncAction Decide(string? client, string? mirror, string? baseHash, bool baselined)
{
	if (client == mirror)
	{
		return SyncAction.InSync;
	}

	if (!baselined || mirror == baseHash)
	{
		return client is null ? SyncAction.DeleteMirror : SyncAction.Upload;
	}

	return SyncAction.Push;
}
```

(`C == B` and "both changed" both end in `Push`; the client tells them apart with its guard.)
- [ ] **Step 4:** Run → pass. **Step 5: Commit** `Sync: three-way decision table`.

---

### Task 4: SQLite project store

**Files:**
- Create: `src/AiChromeProxy.Application/Sync/IProjectStore.cs`, `src/AiChromeProxy.Infrastructure/Projects/SqliteProjectStore.cs`, `src/AiChromeProxy.Infrastructure/Projects/ProjectsOptions.cs`
- Modify: `src/AiChromeProxy.Infrastructure/AiChromeProxy.Infrastructure.csproj` (add `Microsoft.Data.Sqlite`, latest stable 10.0.x — check with `dotnet package search Microsoft.Data.Sqlite --exact-match`), `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`, `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs` (`Database => Path.Combine(Root, "aicp.db")`), `src/AiChromeProxy.Server/Program.cs` (PostConfigure like `MirrorOptions`), `THIRD-PARTY-NOTICES.md` (Microsoft.Data.Sqlite MIT, SQLitePCLRaw Apache-2.0, SQLite public domain — follow the file's existing format), `.gitignore` if `src/AiChromeProxy.Server/data/` is not already ignored (it is — check with `git check-ignore -v src/AiChromeProxy.Server/data/aicp.db`).
- Test: `tests/AiChromeProxy.Tests/Infrastructure/SqliteProjectStoreTests.cs`, extend `DependencyInjectionTests` / `DataDirectoryHostingTests` where `Mirror` defaults are tested.

**Interfaces (Produces):**

```csharp
namespace AiChromeProxy.Application.Sync;

/// <summary>Per-repo state that must survive restarts: the agreed hash per file (the base), whether the repo is baselined, and its settings.</summary>
public interface IProjectStore
{
	bool IsBaselined(string repo);

	void SetBaselined(string repo);

	/// <summary>The base of each path (case-insensitive) that has one.</summary>
	IReadOnlyDictionary<string, string> GetBases(string repo);

	/// <summary>Sets (sha256 not null) or removes (null) bases in one transaction.</summary>
	void SetBases(string repo, IReadOnlyCollection<KeyValuePair<string, string?>> changes);

	ProjectSettings GetSettings(string repo);

	/// <summary>Replaces the settings (null properties remove the key; Extra keys are written as they are).</summary>
	ProjectSettings SaveSettings(string repo, ProjectSettings settings);
}

public sealed class ProjectsOptions
{
	public const string Section = "Projects";
	public string Database { get; set; } = string.Empty;
	/// <summary>Configured path made absolute; default &lt;DataDir&gt;\aicp.db (service) or data\aicp.db under the content root (dev).</summary>
	public static string ResolveDatabase(string? configured, DataDirectory? dataDir, string contentRoot);
}
```

`SqliteProjectStore(IOptions<ProjectsOptions>)`: singleton; opens a new `SqliteConnection` per call (`Data Source=<path>;Pooling=True`), on first use creates the folder and the schema (`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;` then, when `PRAGMA user_version` is 0, the three `CREATE TABLE` statements and `PRAGMA user_version=1`), guarded by a `Lock` so two threads do not migrate twice. Settings rows: `key` = property name in camelCase (`excludes`, `applyServerChanges`, plus every `Extra` key), `value` = `JsonSerializer.Serialize(value, JsonSerializerOptions.Web)`; `GetSettings` rebuilds the record by deserializing a JSON object composed from the rows.

- [ ] **Step 1: Failing tests**

```csharp
public sealed class SqliteProjectStoreTests : IDisposable
{
	private readonly string _path = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"), "aicp.db");

	public void Dispose()
	{
		SqliteConnection.ClearAllPools();
		Directory.Delete(Path.GetDirectoryName(_path)!, recursive: true);
	}

	[Fact]
	public void NewDatabase_WalAndSchemaVersion1_NothingStored()
	{
		var store = Create();

		Assert.False(store.IsBaselined("r"));
		Assert.Empty(store.GetBases("r"));
		Assert.Equal(ProjectSettings.Default, store.GetSettings("r"));
		Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
		Assert.Equal(1L, Scalar("PRAGMA user_version"));
	}

	[Fact]
	public void Bases_SetReplaceRemove_PerRepo_PathIgnoresCase()
	{
		var store = Create();
		store.SetBases("r", [new("a.txt", "1"), new("b.txt", "2")]);
		store.SetBases("r", [new("A.TXT", "3"), new("b.txt", null)]);
		store.SetBases("other", [new("a.txt", "9")]);

		var bases = Create().GetBases("r"); // a second instance reads the same file

		Assert.Equal("3", Assert.Single(bases).Value);
		Assert.Equal("3", bases["a.txt"]);
	}

	[Fact]
	public void Baselined_Persists()
	{
		Create().SetBaselined("r");

		Assert.True(Create().IsBaselined("r"));
		Assert.False(Create().IsBaselined("s"));
	}

	[Fact]
	public void Settings_RoundTrip_UnknownKeysKept_NullRemoves()
	{
		var store = Create();
		var extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""{"model":"opus"}""");
		store.SaveSettings("r", new ProjectSettings { Excludes = "docs/", ApplyServerChanges = false, Extra = extra });

		var read = Create().GetSettings("r");
		Assert.Equal("docs/", read.Excludes);
		Assert.False(read.ApplyServerChangesOrDefault);
		Assert.Equal("opus", read.Extra!["model"].GetString());

		store.SaveSettings("r", read with { Excludes = null });
		Assert.Null(Create().GetSettings("r").Excludes);
	}

	[Fact]
	public void Defaults_DataDirOrContentRoot_ConfiguredWins()
	{
		var dataDir = new DataDirectory(@"C:\data");
		Assert.Equal(@"C:\data\aicp.db", ProjectsOptions.ResolveDatabase(null, dataDir, @"C:\app"));
		Assert.Equal(@"C:\app\data\aicp.db", ProjectsOptions.ResolveDatabase("", null, @"C:\app"));
		Assert.Equal(@"C:\app\x.db", ProjectsOptions.ResolveDatabase("x.db", dataDir, @"C:\app"));
	}

	private SqliteProjectStore Create() => new(Options.Create(new ProjectsOptions { Database = _path }));

	private object? Scalar(string sql)
	{
		using (var connection = new SqliteConnection($"Data Source={_path}"))
		{
			connection.Open();
			using (var command = connection.CreateCommand())
			{
				command.CommandText = sql;
				return command.ExecuteScalar();
			}
		}
	}
}
```

- [ ] **Step 2:** Run → fail. **Step 3: Implement** as described (parameterized commands only; `INSERT ... ON CONFLICT(repo, path) DO UPDATE SET sha256 = excluded.sha256`; deletes for null). Register in `AddInfrastructure`: `services.Configure<ProjectsOptions>(configuration.GetSection(ProjectsOptions.Section)); services.AddSingleton<IProjectStore, SqliteProjectStore>();`; in `Program.cs`: `builder.Services.PostConfigure<ProjectsOptions>(o => o.Database = ProjectsOptions.ResolveDatabase(o.Database, dataDir, builder.Environment.ContentRootPath));`. Every test host that syncs (`SyncHubTests`, `ServerHostingTests`, E2E host) must set `Projects:Database` to a temp path next to its `Mirror:Root` — grep for `Mirror:Root` in tests and add it alongside.
- [ ] **Step 4:** Build, run the new class, `DependencyInjectionTests`, `DataDirectoryHostingTests`, Architecture → pass.
- [ ] **Step 5: Commit** `Projects: SQLite store for bases and settings (WAL)`.

---

### Task 5: Server reconciliation — session uses bases, pushes, fetch and ack

**Files:**
- Modify: `src/AiChromeProxy.Application/Sync/SyncSession.cs`, `SyncSessions.cs`, `SyncHandler.cs` (types + context), `IMirrorStore.cs` (+ `ReadAsync`), `src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs`, `src/AiChromeProxy.Application/DependencyInjection.cs`, `tests/AiChromeProxy.Tests/Client/LoopbackServer.cs` (construct with a store)
- Create: `tests/AiChromeProxy.Tests/Application/MemoryProjectStore.cs` (in-memory `IProjectStore` for tests: dictionaries, case-insensitive paths)
- Test: `tests/AiChromeProxy.Tests/Application/SyncSessionTests.cs` (existing tests keep passing — they run before baseline, i.e. client wins), new `tests/AiChromeProxy.Tests/Application/SyncSessionBackChannelTests.cs`, `FileSystemMirrorStoreTests` (+ `ReadAsync`)

**Interfaces:**
- Consumes: `SyncDecision.Decide`, `IProjectStore`, `IgnoreRules.Create(gitignore, excludes)`, `SyncPages.Split`, payloads from Task 2.
- Produces:

```csharp
// IMirrorStore
/// <summary>Up to count bytes at offset of the mirror file; null when there is no regular file. Opened with FileShare.ReadWrite | Delete.</summary>
Task<byte[]?> ReadAsync(string repo, string path, long offset, int count, CancellationToken ct);

// SyncSession
public SyncSession(IMirrorStore store, IProjectStore projects, ILogger logger, TimeProvider time);
public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct);
/// <summary>The repo this session has open (null before sync.open).</summary>
public string? Repo { get; }
/// <summary>Re-decides these mirror paths (null: every mirror file and every base) with the client's last agreed state and pushes what changed.</summary>
public Task MirrorChangedAsync(IReadOnlyCollection<string>? paths, CancellationToken ct);

// SyncHandler.Types adds SyncFetch and SyncAck; SyncHandler passes the context through.
```

**Behaviour to implement (all inside the session gate):**

1. **Rules per batch:** `var rules = IgnoreRules.Create(gitignore: mirror .gitignore text or null, excludes: projects.GetSettings(repo).Excludes)`. Read `.gitignore` via `store.ReadAsync(repo, ".gitignore", 0, (int)SyncLimits.MaxFileSize, ct)` decoded as UTF-8.
2. **NeedAsync (manifest pages and delta upserts):** for each entry: `M = await store.GetHashAsync`, `B = bases.GetValueOrDefault(path)`, `action = Decide(entry.Sha256, M, B, baselined)`. `InSync` → base := C (collected, written with one `SetBases` per page). `Upload` → expected (as today). `Push` → add `RemoteChange(path, M, size of mirror file or 0, B)` to the page's push list. `DeleteMirror` cannot happen for an entry (C not null).
3. **Delta deletes:** `Decide(null, M, B, baselined)`: `InSync` → remove base; `DeleteMirror` → `store.Delete`, remove base; `Push` → push. The existing "would empty the mirror" guard stays first.
4. **Final manifest page:** stale = mirror files not in the manifest, not kept, **not ignored by `rules`**; each → `Decide(null, M, B, baselined)` as for deletes; the empty-folder guard stays (count only what would be deleted). Bases of paths neither in the manifest nor on the mirror are removed. Then `projects.SetBaselined(repo)` (if not yet).
5. **Commit:** after `store.Commit`, `SetBases([path → entry.Sha256])` before returning `sync.stored`.
6. **Pushes:** collected `RemoteChange`s are sent with `context.SendAsync(Envelope.Create(MessageTypes.SyncRemote, new SyncRemotePayload(repo, page)), ct)` per `SyncPages.Split(changes, c => JsonSerializer.SerializeToUtf8Bytes(c, JsonSerializerOptions.Web).Length + 1)`. Never push: ignored paths, `!SyncPath.IsValid(path)`, mirror files larger than `SyncLimits.MaxFileSize` (log once per path+size at Information: "Not sending {Path} ({Size} bytes) to the browser: larger than the sync limit").
7. **`sync.fetch`:** `CheckRepo`; validate path (`SyncPath.GetError` → `bad_request`); refuse ignored paths with `bad_request`; offset < 0 → `bad_request`; `data = store.ReadAsync(repo, path, offset, SyncLimits.ChunkSize)` null → `not_found`; `last = data.Length < ChunkSize` (equal-length final chunk: the next fetch returns an empty last chunk); on `last` include `sha256 = store.GetHashAsync(...)`. Reply `sync.data`.
8. **`sync.ack`:** `CheckRepo`, validate path, `sha256` null or 64 lower-hex (else `bad_request`); `SetBases([path → sha256])`; reply `sync.ack` with the same payload.
9. **`MirrorChangedAsync(paths, ct)`:** no-op before `sync.open` or after dispose. For each path (or, when null, the union of `store.ListFiles(repo)` and the bases' keys): skip ignored/invalid/temp; `M`, `B`; push when `M != B` (i.e. `Decide(B, M, B, true) == Push`) — sizes as in 6. Uses the push channel kept from the last handled message.

- [ ] **Step 1: Failing tests** — `SyncSessionBackChannelTests` with a real `FileSystemMirrorStore` in a temp root (copy the fixture of `SyncSessionTests`), `MemoryProjectStore`, and a context that records pushes:

```csharp
private readonly List<Envelope> _pushed = [];
private EnvelopeContext Context => new("conn-1", null, (e, _) => { _pushed.Add(e); return Task.CompletedTask; });
```

Tests (one `[Fact]` each, names as listed; write each with Arrange/Act/Assert like `SyncSessionTests`):
  - `FirstFullManifest_ClientWins_ThenBaselined_BasesSet` — mirror has `a.txt`="old", `b.txt`; manifest `a.txt`="new" final → need `[a.txt]`, `b.txt` deleted, `projects.IsBaselined` true; after uploading `a.txt`, base of `a.txt` = hash("new").
  - `ServerEdit_PeriodicManifest_PushesInsteadOfUpload` — baselined repo, base(a)=h("v1"), mirror a="v2", manifest a=h("v1") → need empty, one `sync.remote` with `{a.txt, h("v2"), 2, h("v1")}`, mirror still "v2".
  - `ServerCreatedFile_NotInManifest_PushedNotDeleted` — baselined, mirror `new.txt` without base, manifest without it → file still there, pushed with `base` null.
  - `ClientDeleted_MirrorUnchanged_DeletedAndBaseRemoved` (final page and delta variants).
  - `ExcludedMirrorFiles_NeverDeletedNorPushed` — mirror has `bin/app.dll`, `obj/x`, `logs/a.log` with `.gitignore` "logs/" and settings `Excludes="tmp/"` and `tmp/t.txt` → after final manifest (baselined and not) all still exist, nothing pushed.
  - `BothChanged_DeltaUpsert_NoUpload_Pushed`.
  - `Fetch_ReturnsChunks_LastHasHash` — 40 000-byte file → three replies (16 384, 16 384, 7 232 bytes), last `true` with SHA-256; a 16 384-byte file → two replies, the second empty and last.
  - `Fetch_Missing_NotFound`, `Fetch_IgnoredOrInvalidPath_BadRequest`.
  - `Ack_SetsAndRemovesBase_EchoesPayload`, `Ack_BadHash_BadRequest`.
  - `MirrorChanged_PushesOnlyWhenMirrorDiffersFromBase` — commit an upload (base = M) → `MirrorChangedAsync(["a.txt"])` pushes nothing; edit the file on disk → pushes; `MirrorChangedAsync(null)` covers a deleted file with a base (`sha256` null).
  - `LargeMirrorFile_NotPushed_LoggedOnce`.
  - `ExistingSyncSessionTests` stay green (they never baseline before their assertions or assert client-wins behaviour, which holds before and after for client-only changes) — fix only their constructor calls.
- [ ] **Step 2:** Run → fail. **Step 3: Implement** per "Behaviour". Keep `SyncSession.cs` readable: put fetch/ack/mirror-changed into `SyncSession.BackChannel.cs` (`partial class`) if the file passes ~600 lines. `SyncSessions(IMirrorStore, IProjectStore, ILogger<SyncSession>, TimeProvider)`; `LoopbackServer` passes a `MemoryProjectStore` (expose it as a property for Task 7's tests).
- [ ] **Step 4:** Build; run `SyncSessionTests`, `SyncSessionBackChannelTests`, `FileSystemMirrorStoreTests`, `SyncEngineTests`, `SyncHubTests` → pass.
- [ ] **Step 5: Commit** `Sync: reconcile with bases on the server, push mirror changes, fetch and ack`.

---

### Task 6: Mirror watcher

**Files:**
- Create: `src/AiChromeProxy.Application/Sync/IMirrorWatcher.cs`, `src/AiChromeProxy.Infrastructure/Sync/MirrorWatcher.cs`
- Modify: `src/AiChromeProxy.Application/Sync/SyncSessions.cs` (ref-counted watches, fan-out), `SyncSession.cs` (notify `SyncSessions` when its repo changes or it is disposed — via a callback passed in the constructor, e.g. `Action<SyncSession, string?, string?> repoChanged`), `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`, `LoopbackServer.cs` (a fake watcher)
- Create (tests): `tests/AiChromeProxy.Tests/Application/FakeMirrorWatcher.cs`
- Test: `tests/AiChromeProxy.Tests/Infrastructure/MirrorWatcherTests.cs`, `tests/AiChromeProxy.Tests/Application/SyncSessionsWatchTests.cs`

**Interfaces (Produces):**

```csharp
public interface IMirrorWatcher
{
	/// <summary>
	/// Watches &lt;root&gt;\&lt;repo&gt; until disposed; calls changed with the coalesced protocol paths (500 ms after the last event),
	/// or with null when events were lost (buffer overflow) and every file must be checked. Temp files are left out.
	/// </summary>
	IDisposable Watch(string repo, Func<IReadOnlyCollection<string>?, Task> changed);
}
```

`MirrorWatcher(IOptions<MirrorOptions>, TimeProvider, ILogger<MirrorWatcher>)`: `FileSystemWatcher { IncludeSubdirectories = true, NotifyFilter = FileName | DirectoryName | LastWrite | Size, InternalBufferSize = 64 * 1024 }` on the repo folder (created if missing); `Changed/Created/Deleted` add the relative `/` path, `Renamed` adds old and new; a directory event adds the directory path (the session's `MirrorChangedAsync` treats a path that is a folder by also checking the bases under it — simplest: when a pending path has no file and no base, check bases with that prefix); `Error` → overflow (null). Coalescing: a `ITimer` from `TimeProvider.CreateTimer` reset on each event (500 ms); the callback swaps the pending set and awaits `changed` (exceptions logged, never thrown). Paths ending in `.aicp-tmp` are dropped here; ignore rules are the session's job.

`SyncSessions`: `Dictionary<string, (IDisposable Watch, int Count)>` under a lock; on repo change from A to B: release A (dispose at 0), acquire B (watch at 1). The callback runs `MirrorChangedAsync(paths, CancellationToken.None)` for each session whose `Repo` equals the repo (exceptions logged).

- [ ] **Step 1: Failing tests**
  - `SyncSessionsWatchTests` (with `FakeMirrorWatcher` that records `Watch` calls and lets the test raise changes):
    - `OneWatchPerRepo_DisposedWhenLastSessionCloses`
    - `RepoChange_MovesTheWatch`
    - `Change_FansOutToSessionsWithThatRepo` (two sessions on `r`, one on `s`; edit a file under `r` in the temp mirror; raise `["a.txt"]` → two pushes, none for `s`).
  - `MirrorWatcherTests` (real `FileSystemWatcher`, `FakeTimeProvider`):
    - `Events_CoalescedUntilQuiet` — write two files, wait (poll with a 5 s timeout) until the watcher saw both events (expose `internal int PendingCount` via `InternalsVisibleTo` already used by tests, or observe through the callback not being called before `Advance(500 ms)`), `Advance(TimeSpan.FromMilliseconds(500))` → one callback with both paths.
    - `TempFiles_LeftOut`.
    - `Dispose_StopsCallbacks`.
- [ ] **Step 2:** Run → fail. **Step 3: Implement.** Register `services.AddSingleton<IMirrorWatcher, MirrorWatcher>();`.
- [ ] **Step 4:** Run the new classes 3× (no flake) + `SyncSessionTests` + `SyncEngineTests` → pass.
- [ ] **Step 5: Commit** `Sync: watch the mirror and push server-side edits`.

---

### Task 7: Project settings on the server

**Files:**
- Create: `src/AiChromeProxy.Application/Projects/ProjectSettingsHandler.cs`
- Modify: `SyncSession.cs` (`Open` replies `new SyncOpenPayload(repo, projects.GetSettings(repo))`), `src/AiChromeProxy.Application/DependencyInjection.cs`
- Test: `tests/AiChromeProxy.Tests/Application/ProjectSettingsHandlerTests.cs`, one more case in `SyncSessionTests` (`Open_RepliesSettings`)

**Interfaces (Produces):** `ProjectSettingsHandler(string type, IProjectStore projects)` registered for `MessageTypes.ProjectSettingsGet` and `MessageTypes.ProjectSettingsSet`; both reply `MessageTypes.ProjectSettings` with `ProjectSettingsPayload(repo, settings)`. Repo via `RepoName.Sanitize` (null → `bad_request`); `set` without `settings` → `bad_request`; `Excludes` longer than 16 000 characters → `too_large`. After a `set`, every session with that repo re-checks the whole mirror (`MirrorChangedAsync(null)`): call through `SyncSessions` (`Task RecheckAsync(string repo)`), since newly un-excluded mirror files may now need pushing.

- [ ] **Step 1: Failing tests:** `Get_UnknownRepo_Defaults`, `Set_ThenGet_RoundTrip_SanitizedRepo`, `Set_WithoutSettings_BadRequest`, `Set_HugeExcludes_TooLarge`, `Open_RepliesSettings`.
- [ ] **Step 2–4:** Run → fail; implement; run → pass.
- [ ] **Step 5: Commit** `Projects: settings messages and settings on sync.opened`.

---

### Task 8: Browser — write access and guarded writes

**Files:**
- Modify: `src/AiChromeProxy.Client/Scripts/fsaccess.ts`, `src/AiChromeProxy.Client/Sync/IFolderAccess.cs`, `src/AiChromeProxy.Client/Sync/JsFolderAccess.cs`, `tests/AiChromeProxy.Tests/Client/FakeFolder.cs`
- Create: `src/AiChromeProxy.Client/Sync/SyncEngine.Remote.cs` (partial), `src/AiChromeProxy.Client/Sync/RemoteState.cs` (enum + record for the UI)
- Modify: `src/AiChromeProxy.Client/Sync/SyncEngine.cs` (subscribe to pushes, settings from `sync.opened`, `IgnoreRules.Create(gitignore, settings.Excludes)`, apply remote changes at the start of a cycle, `_known` updates)
- Test: `tests/AiChromeProxy.Tests/Client/SyncEngineBackChannelTests.cs`

**Interfaces (Produces):**

```csharp
// IFolderAccess additions
/// <summary>SHA-256 of the file as it is now (not from the last scan); null when there is no such file. Throws when it cannot be read.</summary>
Task<string?> HashNowAsync(string path);
/// <summary>Whether the folder may be written (queryPermission readwrite; asks nothing).</summary>
Task<bool> HasWriteAccessAsync();
/// <summary>Asks for write access (must run from a click).</summary>
Task<bool> RequestWriteAccessAsync();
/// <summary>Replaces (or creates, with its folders) the file atomically (createWritable).</summary>
Task WriteAsync(string path, byte[] content);
/// <summary>Deletes the file; no-op when it is not there.</summary>
Task DeleteAsync(string path);

// RemoteState.cs
public enum RemoteStatus { Waiting, Conflict }
/// <param name="Local">The client's hash when the conflict was found (null: deleted here).</param>
public sealed record RemoteItem(RemoteChange Change, RemoteStatus Status, string? Local);

// SyncEngine (public surface for the UI)
public IReadOnlyList<RemoteItem> Remote { get; }          // sorted by path; a new list when it changes
public int ConflictCount { get; }
public bool CanWrite { get; }                              // last HasWriteAccessAsync answer
public ProjectSettings Settings { get; }                   // from sync.opened / SaveSettingsAsync
public Task AllowWritingAsync();                           // from the click: RequestWriteAccessAsync, then Wake
public Task ApplyAsync(string? path);                      // Apply (path) / Apply all (null) when applyServerChanges is off
public Task KeepMineAsync(string path);
public Task TakeServersAsync(string path);
public Task<string?> ServerTextAsync(string path);         // conflict preview: fetched text when ≤ 256 KB and valid UTF-8, else null
public Task SaveSettingsAsync(ProjectSettings settings);   // project.settings.set, then rescan
```

**Behaviour:**
- `InitializeAsync` subscribes `transport.Received`: an envelope of type `sync.remote` whose `repo` equals `_repo` → merge changes into `_remote` (by path; a newer change replaces an older one, a conflict stays a conflict but takes the new change), `Raise()`, `Wake()`. Unsubscribe pattern like `StateChanged`.
- At the start of `SyncOnceAsync` (after the guard, before the scan, only when connected and a session is open — if `_repo` is null the full manifest re-pushes anything pending, so skip): `await ApplyRemoteAsync(generation, ct)`:
  - `CanWrite = await folder.HasWriteAccessAsync()`; if false or `!Settings.ApplyServerChangesOrDefault` → leave `Waiting` items, return.
  - For each `Waiting` item in path order: refuse (drop with an Actions-history error) when `!SyncPath.IsValid(path)` or the current rules ignore it. `now = await folder.HashNowAsync(path)`:
    - `now == change.Sha256` → `ack(change.Sha256)`; set `_known`.
    - `now == change.Base` → if `change.Sha256 is null`: `DeleteAsync`; else `bytes = FetchAsync(path)`; if `SHA256(bytes) != change.Sha256` → drop (newer push follows); else `WriteAsync`. Then `ack(change.Sha256)`; `_known[path] = new ManifestEntry(path, size, sha)` or remove; log "Received N files from the server: …" grouped per cycle (`Group("Received", …)`), deletes as "Deleted on the server: …".
    - else → `Conflict` with `Local = now`; log "Conflict: path changed here and on the server." once per path.
  - A conflict item is skipped by the upload: in the scan result, entries whose path is in conflict are treated like backed-off (left out of upserts/uploads, kept in `_known` as they were).
- `FetchAsync(path)`: loop `sync.fetch` from offset 0 with `RequestAsync`, append `SyncData.Decode(data)` until `last`; max `SyncLimits.MaxFileSize` (abort above).
- `KeepMineAsync(path)`: `ack(change.Sha256)`; `_known[path] = change.Sha256 is null ? remove : new ManifestEntry(path, change.Size, change.Sha256)` so the next delta upserts (or deletes) the local version; remove the item; Wake.
- `TakeServersAsync(path)`: needs `CanWrite` (else `AllowWritingAsync` first from the same click); write/delete like the guard path but without comparing `now`; ack; remove; Wake.
- `ApplyAsync(path)`: runs the guarded apply for that item (or all) even when `applyServerChanges` is off.
- Settings: `OpenAsync` stores `opened.Settings ?? ProjectSettings.Default`; the scan uses `IgnoreRules.Create(gitignore, Settings.Excludes)`.
- `fsaccess.ts`: `hashNow(path)` (walk handles by segments with `getDirectoryHandle`/`getFileHandle`, `NotFoundError` → null, read `getFile()`, `crypto.subtle.digest`); `hasWriteAccess()` / `requestWriteAccess()` on `root` with `{mode: 'readwrite'}`; `write(path, bytes: Uint8Array)` (`getDirectoryHandle(seg, {create: true})` per folder, `getFileHandle(name, {create: true})`, `createWritable()`, `write`, `close`; a failure calls `abort()`); `remove(path)` (`removeEntry(name)`, `NotFoundError` ignored). Each validates segments (non-empty, not `.`/`..`) before touching handles. `JsFolderAccess` gets one-line wrappers (`byte[]` marshals to `Uint8Array`).
- `FakeFolder`: `WriteAccess` (bool, default false), `RequestWriteAccessAsync` sets it true; `HashNowAsync` from `Files`; `WriteAsync`/`DeleteAsync` mutate `Files` and record `Writes` (list of paths); throw `JSException` when `WriteAccess` is false.

- [ ] **Step 1: Failing tests** — `SyncEngineBackChannelTests` over `LoopbackServer` (real server session + `MemoryProjectStore` + `FakeMirrorWatcher`), with a helper `await SyncedAsync()` that picks the folder, syncs once and baselines; a helper `EditMirror(path, text)` that writes the mirror file and raises the fake watcher for it (pushes reach the client through `FakeTransport`):
  - `ServerEdit_WithWriteAccess_WrittenAndAcked` — folder "v1" synced; `EditMirror("a.txt","v2")`; `SyncOnceAsync` → folder has "v2", `Remote` empty, base = h("v2"), no `sync.chunk` sent for `a.txt`.
  - `ServerEdit_WithoutWriteAccess_Waits_ThenAllowWriting` — `Remote` has `Waiting`, folder unchanged; `AllowWritingAsync` + cycle → written.
  - `ServerDelete_Applied`.
  - `ServerCreate_Applied_FoldersCreated` (`docs/new/x.md`).
  - `BothChanged_Conflict_NotUploadedNotWritten` → `ConflictCount == 1`, mirror still the server's, folder still the client's, one Actions-history entry.
  - `KeepMine_UploadsLocalVersion`; `TakeServers_WritesServerVersion`.
  - `ApplyServerChangesOff_Waits_ApplyAll_Writes`.
  - `IgnoredOrInvalidRemotePath_NeverWritten` (push crafted with `FakeTransport.Push` for `.env` and `a/../b`).
  - `FetchHashMismatch_Dropped` (mirror edited again between push and fetch — edit the mirror file on disk before the cycle without raising the watcher; the cycle fetches the newer content whose hash differs → nothing written; raising the watcher afterwards then writes it).
  - `PeriodicFullManifest_AfterServerEdit_DoesNotOverwrite` (`FakeTimeProvider` past `FullManifestInterval`; mirror keeps "v2", folder gets "v2").
  - `SettingsExcludes_AppliedToScan` (open reply carries `Excludes = "docs/"` set via `MemoryProjectStore` before the first open → `docs/a.md` not uploaded).
  - `ServerText_SmallUtf8_ElseNull`.
- [ ] **Step 2:** Run → fail. **Step 3: Implement** (TS + C#). Build compiles `fsaccess.ts` via `Microsoft.TypeScript.MSBuild`; `BrowserScriptTests` must stay green.
- [ ] **Step 4:** Build; run `SyncEngineBackChannelTests` 3×, `SyncEngineTests`, `ShellTests`, Architecture → pass.
- [ ] **Step 5: Commit** `Client: apply server changes under the hash-guard, conflicts, settings`.

---

### Task 9: UI — server changes, conflicts, project settings

**Files:**
- Modify: `src/AiChromeProxy.Client/Shell/TabSet.cs` (`public const string Settings = ":settings";`, `ConflictTab(path)` = `"conflict:" + path`, `IsConflict`, `ConflictPath`), `MainArea.razor` (Conflict and Project settings panels), `Explorer.razor` (banner "N server changes — Allow writing" / **Apply all**; conflict badge per file via `Engine.Remote`), `FileTreeView.razor`/`TreeRow.cs` (badge `conflict`), `SyncStatus.razor` (conflict count, clickable → first conflict tab), `TopBar.razor` (gear button `aria-label="Project settings"`, enabled while a folder is open), `wwwroot/css/app.css` (badge/banner styles with existing tokens), `Icon.razor` (gear, conflict icons as inline SVG like the others)
- Modify: `tests/AiChromeProxy.Tests/Client/ShellTests.cs` (bUnit-style tests already there — follow them), `tests/AiChromeProxy.E2E` feature + steps (follow the existing shell feature)

**Content:**
- Conflict tab: heading "Conflict" + path; two columns "This folder" / "Server" with size and SHA-256 (or "Deleted"); a `<pre>` with `ServerTextAsync` (loaded when the tab opens; "Preview not available" when null); buttons **Keep mine** (`KeepMineAsync`) and **Take server's** (`TakeServersAsync`; if `!CanWrite` the same click calls `AllowWritingAsync` first). The tab closes itself when the conflict is gone.
- Project settings tab: label "Extra excludes (.gitignore syntax)" with a `<textarea>` (monospace, 8 rows); checkbox "Apply server changes automatically"; **Save** (disabled while unchanged) → `SaveSettingsAsync`; "Saved." status text with `role="status"`.
- Explorer banner (only when `Remote` has `Waiting` items): `!CanWrite` → "{n} server changes — " + button **Allow writing**; `CanWrite && !ApplyServerChanges` → "{n} server changes waiting" + **Apply all**. Use `Format.Count` for numbers; singular "1 server change".
- Accessibility: buttons are real `<button>`s, visible focus, banner `role="status"`.
- [ ] **Step 1: Failing tests** in `ShellTests` (engine state through `LoopbackServer` + `FakeFolder` as the existing shell tests do): banner text and button for no write access; Apply all when automatic apply is off; conflict badge + status count; conflict tab buttons call the engine (folder/mirror result asserted); settings tab saves and re-reads. E2E: one scenario "Project settings tab opens from the gear and shows its fields".
- [ ] **Step 2–4:** Run → fail; implement; run unit tests + E2E → pass; screenshot-check dark and light at 1280×720 with Playwright (`page.ScreenshotAsync`) into the scratchpad, look at them.
- [ ] **Step 5: Commit** `Shell: server changes banner, conflict tab, project settings tab`.

---

### Task 10: End to end over SignalR, docs

**Files:**
- Modify: `tests/AiChromeProxy.Tests/Server/SyncHubTests.cs` (one new test), `docs/sync.md` (back channel section + manual checklist), `docs/superpowers/specs/2026-10-03-sync-and-shell-design.md` ("Known limitations": the periodic full manifest no longer overwrites server edits — link 3b), `README.md` (one line in the feature list: server edits come back to the folder), `CLAUDE.md` (Infrastructure line: SQLite project store and mirror watcher)

- [ ] **Step 1: Failing test** `ServerEdit_PushedFetchedAcked_OverSignalR` in `SyncHubTests` (real host via `WebApplicationFactory`, temp `Mirror:Root` and `Projects:Database`): connect a `HubConnection`, open, send a final manifest of one file, upload it, then write the mirror file on disk; wait (≤ 10 s) for a `sync.remote` envelope; `sync.fetch` → `sync.data` with the new content and hash; `sync.ack` → echo; a second manifest with the new hash → `need` empty and no push.
- [ ] **Step 2–4:** Run → fail (if the earlier tasks are right it may pass at once — then it is a regression test; keep it). Run the full gate (≥ 85%).
- [ ] **Step 5: Docs.** `docs/sync.md` gets "Back channel" (what happens on a server edit, conflicts and both buttons, Allow writing, project settings, excluded files on the mirror stay, the `aicp.db` location) and checklist items: edit on the server → folder within ~5 s; edit both → conflict → each button; delete on the server; `dotnet build` on the mirror → `bin/obj` stay and are not pushed; no write access → banner → Allow writing; extra excludes; restart the service → bases persist (no conflict, no overwrite).
- [ ] **Step 6: Commit** `Back channel: hub end-to-end test and docs`.

---

## Self-review (done while writing)

- Spec coverage: three-way state, decision table (T3), SQLite store (T4), watcher (T6), server excludes (T2, T5), push/fetch/ack (T5), hash-guard + write permission (T8), conflicts UI (T9), settings (T7–T9), connection-lost (T1), testing incl. hub E2E and manual checklist (T10).
- Types used across tasks: `RemoteChange(Path, Sha256, Size, Base)`, `SyncDecision.Decide(client, mirror, baseHash, baselined)`, `IProjectStore.GetBases/SetBases/IsBaselined/SetBaselined/GetSettings/SaveSettings`, `IMirrorWatcher.Watch(repo, changed)`, `SyncSession.MirrorChangedAsync(paths, ct)`, `SyncSessions.RecheckAsync(repo)`, `IFolderAccess.HashNowAsync/HasWriteAccessAsync/RequestWriteAccessAsync/WriteAsync/DeleteAsync`, `SyncEngine.Remote/ConflictCount/CanWrite/Settings/AllowWritingAsync/ApplyAsync/KeepMineAsync/TakeServersAsync/ServerTextAsync/SaveSettingsAsync`.
