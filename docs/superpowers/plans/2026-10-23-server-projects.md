# Server projects (read-only view) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** From any computer, list every project on the server and look at another computer's project read-only — its chat, file tree and files — while the local folder keeps syncing.

**Architecture:** Three new hub request/reply pairs (`repos.list`, `mirror.list`, `mirror.read`) in a new `MirrorBrowseHandler` (Application) that only reads the mirror, `aicp.db` and live run/sync state. The client gets a `MirrorBrowser` engine (lists projects, loads and reads the viewed repo's files), `ChatEngine.ViewAsync` (moves the `chat.open` subscription to the viewed repo, refuses send/stop/approve) and a view mode in `Home` (projects menu in the top bar, mirror tree in the Explorer, `FileView` reading from the mirror, a separate tab set).

**Tech Stack:** .NET 10, C#, ASP.NET Core SignalR hub (existing), Blazor WebAssembly, xunit v3 on Microsoft.Testing.Platform, Reqnroll + Playwright (E2E).

Spec: [2026-10-23-server-projects-design.md](../specs/2026-10-23-server-projects-design.md).

## Global Constraints

- Everything in the repo is in English; no personal paths, domains or secrets.
- Files are UTF-8 without BOM, CRLF; C#: tabs, no `this.`, private fields `_camelCase`, file-scoped namespaces; Razor markup: 4 spaces (as the existing `.razor` files).
- Disposables use block `using (...) { }` — never `using var` (enforced by `Architecture/UsingStatementStyleTests`).
- Client depends on Domain only; Application does not depend on Infrastructure (enforced by `Architecture/LayerDependencyTests`).
- The `Envelope` JSON shape (camelCase, `JsonSerializerOptions.Web`) is a public contract: only additions.
- Every hub message stays under SignalR's 32 KB: pages bounded by `SyncLimits.MaxPageEntries` (500) and `SyncLimits.MaxPageBytes` (24 000); file data in `SyncLimits.ChunkSize` (16 KB) chunks, base64url.
- Nothing in the new messages writes the mirror, `aicp.db` or the attachments folder.
- Gate: `dotnet build -c Release` with no warnings-as-errors, then `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` green. A failing test is fixed, never disabled; the threshold is never lowered.
- Search with `rg`; on PowerShell 5.1 pass `-Encoding utf8` when writing files.

Run one test class: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class <Namespace.ClassName>` (after `dotnet build -c Release`).

## File Structure

| File | Responsibility |
|---|---|
| `src/AiChromeProxy.Domain/Projects/RepoInfo.cs`, `ReposPayload.cs`, `MirrorFile.cs`, `MirrorListPayload.cs`, `MirrorFilesPayload.cs`, `MirrorReadPayload.cs` (create) | Wire records of the three messages |
| `src/AiChromeProxy.Domain/MessageTypes.cs` (modify) | `repos.list`, `repos`, `mirror.list`, `mirror.files`, `mirror.read`, `mirror.data` |
| `src/AiChromeProxy.Application/Sync/IMirrorStore.cs`, `src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs` (modify) | `ListRepos()`, `ListFileInfos(repo)` |
| `src/AiChromeProxy.Application/Chat/IChatStore.cs`, `src/AiChromeProxy.Infrastructure/Chat/SqliteChatStore.cs`, `tests/.../Application/MemoryChatStore.cs` (modify) | `LastActivity()` |
| `src/AiChromeProxy.Application/Chat/ChatService.cs`, `src/AiChromeProxy.Application/Sync/SyncSessions.cs` (modify) | `IsRunning(repo)`, `IsOpen(repo)` |
| `src/AiChromeProxy.Application/Sync/MirrorRules.cs` (create), `SyncSession.BackChannel.cs` (modify) | One definition of the ignore rules the server applies to the mirror |
| `src/AiChromeProxy.Application/Projects/MirrorBrowseHandler.cs` (create), `DependencyInjection.cs` (modify) | The three read-only messages |
| `src/AiChromeProxy.Client/Projects/MirrorBrowser.cs` (create), `Program.cs`, `Sync/SyncEngine.cs` (modify) | Client engine of the view; `SyncEngine.Visible` |
| `src/AiChromeProxy.Client/Chat/ChatEngine.cs` (modify) | `ViewAsync`, `Viewing`, read-only guards |
| `src/AiChromeProxy.Client/Projects/ProjectLine.cs`, `Shell/ProjectsMenu.razor`, `Shell/MirrorTreeView.razor` (create) | Menu line text and keyboard moves; the menu; the read-only tree |
| `src/AiChromeProxy.Client/Shell/TopBar.razor`, `Pages/Home.razor`, `Shell/Explorer.razor`, `Shell/MainArea.razor`, `Shell/FileView.razor`, `Shell/ChatView.razor`, `Shell/Format.cs`, `Navigator/FileContent.cs`, `wwwroot/css/app.css` (modify) | View mode in the shell |
| `tests/AiChromeProxy.E2E/Features/Projects.feature`, `StepDefinitions/ProjectsSteps.cs` (create) | E2E |
| `docs/projects.md` (create), `CLAUDE.md`, spec (modify) | Docs |

---

### Task 1: Wire contract

**Files:**
- Create: `src/AiChromeProxy.Domain/Projects/RepoInfo.cs`, `ReposPayload.cs`, `MirrorFile.cs`, `MirrorListPayload.cs`, `MirrorFilesPayload.cs`, `MirrorReadPayload.cs`
- Modify: `src/AiChromeProxy.Domain/MessageTypes.cs` (append after `PackagesNuGetStored`)
- Test: `tests/AiChromeProxy.Tests/Domain/ProjectsContractTests.cs`

**Interfaces:**
- Produces: `RepoInfo(string Name, DateTimeOffset? LastActivity, bool Running, bool Open)`, `ReposPayload(IReadOnlyList<RepoInfo> Repos, bool Truncated)`, `MirrorFile(string Path, long Size, DateTimeOffset Modified)`, `MirrorListPayload(string Repo, string? After = null)`, `MirrorFilesPayload(string Repo, IReadOnlyList<MirrorFile> Files, string? Next)`, `MirrorReadPayload(string Repo, string Path, long Offset)` — namespace `AiChromeProxy.Domain.Projects`; `MessageTypes.ReposList/Repos/MirrorList/MirrorFiles/MirrorRead/MirrorData`. `mirror.data` carries the existing `AiChromeProxy.Domain.Sync.SyncDataPayload`.

- [ ] **Step 1: Write the failing test**

`tests/AiChromeProxy.Tests/Domain/ProjectsContractTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Tests.Domain;

public sealed class ProjectsContractTests
{
	private static readonly DateTimeOffset At = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Payloads_AreCamelCase()
	{
		Assert.Equal("""{"repos":[{"name":"r","lastActivity":"2026-10-23T12:00:00+00:00","running":true,"open":false}],"truncated":false}""", Json(new ReposPayload([new RepoInfo("r", At, true, false)], false)));
		Assert.Equal("""{"name":"r","lastActivity":null,"running":false,"open":true}""", Json(new RepoInfo("r", null, false, true)));
		Assert.Equal("""{"repo":"r","after":null}""", Json(new MirrorListPayload("r")));
		Assert.Equal("""{"repo":"r","files":[{"path":"a/b.cs","size":3,"modified":"2026-10-23T12:00:00+00:00"}],"next":"a/b.cs"}""", Json(new MirrorFilesPayload("r", [new MirrorFile("a/b.cs", 3, At)], "a/b.cs")));
		Assert.Equal("""{"repo":"r","path":"a.txt","offset":16384}""", Json(new MirrorReadPayload("r", "a.txt", 16384)));
	}

	[Fact]
	public void Types()
	{
		Assert.Equal("repos.list", MessageTypes.ReposList);
		Assert.Equal("repos", MessageTypes.Repos);
		Assert.Equal("mirror.list", MessageTypes.MirrorList);
		Assert.Equal("mirror.files", MessageTypes.MirrorFiles);
		Assert.Equal("mirror.read", MessageTypes.MirrorRead);
		Assert.Equal("mirror.data", MessageTypes.MirrorData);
	}

	private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
```

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet build -c Release`
Expected: build error CS0234/CS0246 (`AiChromeProxy.Domain.Projects` does not exist).

- [ ] **Step 3: Add the records and the types**

`src/AiChromeProxy.Domain/Projects/RepoInfo.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary>
/// A project on the server (a folder of the mirror): <paramref name="LastActivity"/> is the newest update of its chat sessions (null: no chat yet),
/// <paramref name="Running"/> whether Claude is working there now, <paramref name="Open"/> whether a browser has the folder open now.
/// </summary>
public sealed record RepoInfo(string Name, DateTimeOffset? LastActivity, bool Running, bool Open);
```

`src/AiChromeProxy.Domain/Projects/ReposPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary><c>repos {repos, truncated}</c>: server → client, the reply to <c>repos.list</c>; most recent activity first, cut off (<paramref name="Truncated"/>) to stay under the hub's limit.</summary>
public sealed record ReposPayload(IReadOnlyList<RepoInfo> Repos, bool Truncated);
```

`src/AiChromeProxy.Domain/Projects/MirrorFile.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary>A file of the mirror: protocol path, size in bytes and last write time (UTC).</summary>
public sealed record MirrorFile(string Path, long Size, DateTimeOffset Modified);
```

`src/AiChromeProxy.Domain/Projects/MirrorListPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.list {repo, after}</c>: client → server, one page of the repo's mirror files after the path <paramref name="After"/> (null: from the start); reply <c>mirror.files</c>.</summary>
public sealed record MirrorListPayload(string Repo, string? After = null);
```

`src/AiChromeProxy.Domain/Projects/MirrorFilesPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.files {repo, files, next}</c>: server → client, files sorted by path (ordinal); <paramref name="Next"/> is the <c>after</c> of the next page, null on the last.</summary>
public sealed record MirrorFilesPayload(string Repo, IReadOnlyList<MirrorFile> Files, string? Next);
```

`src/AiChromeProxy.Domain/Projects/MirrorReadPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Projects;

/// <summary><c>mirror.read {repo, path, offset}</c>: client → server, one chunk of a mirror file from <paramref name="Offset"/>; reply <c>mirror.data</c> (a <c>Sync.SyncDataPayload</c>, or <c>not_found</c>).</summary>
public sealed record MirrorReadPayload(string Repo, string Path, long Offset);
```

Append to `src/AiChromeProxy.Domain/MessageTypes.cs`, before the closing brace:

```csharp

	/// <summary>Client → server: lists the projects on the server (empty payload); reply <see cref="Repos"/>.</summary>
	public const string ReposList = "repos.list";

	/// <summary>Server → client: the projects (<c>Projects.ReposPayload</c>).</summary>
	public const string Repos = "repos";

	/// <summary>Client → server: one page of a project's mirror files (<c>Projects.MirrorListPayload</c>); reply <see cref="MirrorFiles"/>.</summary>
	public const string MirrorList = "mirror.list";

	/// <summary>Server → client: the page (<c>Projects.MirrorFilesPayload</c>).</summary>
	public const string MirrorFiles = "mirror.files";

	/// <summary>Client → server: one chunk of a mirror file (<c>Projects.MirrorReadPayload</c>); reply <see cref="MirrorData"/>.</summary>
	public const string MirrorRead = "mirror.read";

	/// <summary>Server → client: the chunk (<c>Sync.SyncDataPayload</c>, as <see cref="SyncData"/>).</summary>
	public const string MirrorData = "mirror.data";
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Domain.ProjectsContractTests`
Expected: 2 tests passed. (If the `lastActivity` text differs only in the offset format, keep the assertion on what `JsonSerializerOptions.Web` writes — it is the wire format.)

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Domain/Projects src/AiChromeProxy.Domain/MessageTypes.cs tests/AiChromeProxy.Tests/Domain/ProjectsContractTests.cs
git commit -m "Domain: repos.list, mirror.list and mirror.read contract"
```

---

### Task 2: Mirror store lists repos and file details

**Files:**
- Modify: `src/AiChromeProxy.Application/Sync/IMirrorStore.cs`, `src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs`, `tests/AiChromeProxy.Tests/Application/SyncSessionTests.cs` (its `FakeStore`)
- Test: `tests/AiChromeProxy.Tests/Infrastructure/FileSystemMirrorStoreTests.cs`

**Interfaces:**
- Consumes: `MirrorFile` (Task 1).
- Produces: `IMirrorStore.ListRepos()` → `IReadOnlyList<string>`; `IMirrorStore.ListFileInfos(string repo)` → `IReadOnlyList<MirrorFile>` (unsorted).

- [ ] **Step 1: Write the failing tests**

Add to `FileSystemMirrorStoreTests` (it already has `_temp`, `_root`, `_repoRoot`, `Repo = "repo"`, `_store`, `Junction(link, target)`):

```csharp
	[Fact]
	public void ListRepos_ValidFolders_SkipsFilesJunctionsAndInvalidNames()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		Directory.CreateDirectory(Path.Combine(_root, "Other_PC"));
		Directory.CreateDirectory(Path.Combine(_root, "has space"));
		File.WriteAllText(Path.Combine(_root, "file.txt"), "f");
		Junction(Path.Combine(_root, "linked"), outside);

		Assert.Equal(["Other_PC", Repo], _store.ListRepos().Order(StringComparer.Ordinal));
	}

	[Fact]
	public void ListRepos_NoMirrorRoot_Empty()
	{
		var store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = Path.Combine(_temp, "missing") }));

		Assert.Empty(store.ListRepos());
	}

	[Fact]
	public void ListFileInfos_SizesAndTimes_SkipsTempFilesAndJunctions()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Directory.CreateDirectory(Path.Combine(_repoRoot, "src"));
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "abc");
		File.WriteAllText(Path.Combine(_repoRoot, "src", "b.cs"), "b");
		File.WriteAllText(Path.Combine(_repoRoot, "c.txt.aicp-tmp"), "t");
		var written = new DateTime(2026, 10, 23, 10, 0, 0, DateTimeKind.Utc);
		File.SetLastWriteTimeUtc(Path.Combine(_repoRoot, "a.txt"), written);
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var files = _store.ListFileInfos(Repo).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();

		Assert.Equal(["a.txt", "src/b.cs"], files.Select(f => f.Path));
		Assert.Equal((3L, new DateTimeOffset(written)), (files[0].Size, files[0].Modified));
		Assert.Empty(_store.ListFileInfos("not-synced-yet"));
	}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS1061 — `FileSystemMirrorStore` has no `ListRepos` / `ListFileInfos`.

- [ ] **Step 3: Implement**

In `IMirrorStore.cs` add `using AiChromeProxy.Domain.Projects;` at the top and, after `ListFiles`:

```csharp
	/// <summary>The repos of the mirror: folders directly under the root with a valid repo name; files, links and junctions are skipped.</summary>
	IReadOnlyList<string> ListRepos();

	/// <summary>Like <see cref="ListFiles"/>, with each file's size and last write time; unsorted, empty when the repo folder is missing.</summary>
	IReadOnlyList<MirrorFile> ListFileInfos(string repo);
```

In `FileSystemMirrorStore.cs` add `using AiChromeProxy.Domain.Projects;` and, after `ListFiles`:

```csharp
	public IReadOnlyList<string> ListRepos()
	{
		if (!Directory.Exists(_root))
		{
			return [];
		}

		return [.. new DirectoryInfo(_root).EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = FileAttributes.ReparsePoint })
			.Select(d => d.Name)
			.Where(RepoName.IsValid)];
	}

	public IReadOnlyList<MirrorFile> ListFileInfos(string repo)
	{
		var repoRoot = RepoRoot(repo);
		if (!Directory.Exists(repoRoot) || IsLink(repoRoot))
		{
			return [];
		}

		// The enumeration's own data: a file deleted meanwhile does not throw.
		return [.. new DirectoryInfo(repoRoot).EnumerateFiles("*", Recursive)
			.Select(f => (RelativePath: Path.GetRelativePath(repoRoot, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), File: f))
			.Where(e => SyncPath.IsValid(e.RelativePath))
			.Select(e => new MirrorFile(e.RelativePath, e.File.Length, new DateTimeOffset(e.File.LastWriteTimeUtc, TimeSpan.Zero)))];
	}
```

In `SyncSessionTests.cs`, `FakeStore` (around line 678), add `using AiChromeProxy.Domain.Projects;` at the top of the file and, after `ListFiles`:

```csharp
		public IReadOnlyList<string> ListRepos() => [];

		public IReadOnlyList<MirrorFile> ListFileInfos(string repo) => [];
```

Check for other implementations: `rg -n ": IMirrorStore" src tests` must list only `FileSystemMirrorStore` and `FakeStore`.

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Infrastructure.FileSystemMirrorStoreTests`
Expected: all pass (the junction tests need no special rights; symbolic-link tests may skip as today).

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Application/Sync/IMirrorStore.cs src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs tests/AiChromeProxy.Tests/Application/SyncSessionTests.cs tests/AiChromeProxy.Tests/Infrastructure/FileSystemMirrorStoreTests.cs
git commit -m "Mirror store: list repos and file sizes and times"
```

---

### Task 3: Chat activity, running runs and open folders

**Files:**
- Modify: `src/AiChromeProxy.Application/Chat/IChatStore.cs`, `src/AiChromeProxy.Infrastructure/Chat/SqliteChatStore.cs`, `tests/AiChromeProxy.Tests/Application/MemoryChatStore.cs`, `src/AiChromeProxy.Application/Chat/ChatService.cs`, `src/AiChromeProxy.Application/Sync/SyncSessions.cs`
- Test: `tests/AiChromeProxy.Tests/Infrastructure/SqliteChatStoreTests.cs`, `tests/AiChromeProxy.Tests/Application/SyncSessionsWatchTests.cs`, `tests/AiChromeProxy.Tests/Application/ChatServiceTests.cs`

**Interfaces:**
- Produces: `IChatStore.LastActivity()` → `IReadOnlyDictionary<string, DateTimeOffset>` (repo → newest `Updated`, keys compared ignoring case); `ChatService.IsRunning(string repo)` → `bool`; `SyncSessions.IsOpen(string repo)` → `bool`.

- [ ] **Step 1: Write the failing tests**

Add to `SqliteChatStoreTests` (it has `Stores`, `_clock`, `Chat()`):

```csharp
	[Theory]
	[MemberData(nameof(Stores))]
	public void LastActivity_NewestUpdatePerRepo_SameInBothStores(bool memory)
	{
		IChatStore chat = memory ? new MemoryChatStore(_clock) : Chat();
		var first = chat.CreateSession("a", "one");
		var b = chat.CreateSession("b", "two");
		chat.CreateSession("a", "three");
		chat.Touch(first.Id);
		var newest = chat.ListSessions("a")[0].Updated;

		var last = chat.LastActivity();

		Assert.Equal(2, last.Count);
		Assert.Equal(newest, last["a"]);
		Assert.Equal(b.Updated, last["B"]);
		Assert.False(last.ContainsKey("c"));
	}
```

Add to `SyncSessionsWatchTests` (repos `r` and `s` exist under its root; see its other tests for opening a session — use the same helper they use, or this inline form):

```csharp
	[Fact]
	public async Task IsOpen_WhileASessionHasTheRepo()
	{
		var context = new EnvelopeContext("c1", null, (_, _) => Task.CompletedTask);
		_pushed["c1"] = [];
		Assert.False(_sessions.IsOpen("r"));

		await _sessions.Get("c1").HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("r"), "o1"), context, Ct);

		Assert.True(_sessions.IsOpen("r"));
		Assert.True(_sessions.IsOpen("R"));
		Assert.False(_sessions.IsOpen("s"));
		_sessions.Close("c1");
		Assert.False(_sessions.IsOpen("r"));
	}
```

Add to `ChatServiceTests` (it has `Client`, `OpenAsync`, `SendAsync`, `_runner`):

```csharp
	[Fact]
	public async Task IsRunning_FromStartToExit()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);
		Assert.False(_service.IsRunning(Repo));

		await SendAsync(client, Repo, null, "hi");
		var process = await _runner.NextAsync();
		Assert.True(_service.IsRunning(Repo));
		Assert.True(_service.IsRunning("REPO"));
		Assert.False(_service.IsRunning("other"));

		process.Write(Result);
		process.Exit();
		await client.WaitAsync(e => e.Kind == ChatEventKinds.Result);
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (_service.IsRunning(Repo))
		{
			Assert.True(DateTime.UtcNow < deadline, "The run did not end.");
			await Task.Delay(10, Ct);
		}
	}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS1061 for `LastActivity`, `IsOpen`, `IsRunning`.

- [ ] **Step 3: Implement**

`IChatStore.cs`, after `ListSessions`:

```csharp
	/// <summary>Each repo with sessions and the newest <c>Updated</c> among them; keys compare ignoring case.</summary>
	IReadOnlyDictionary<string, DateTimeOffset> LastActivity();
```

`SqliteChatStore.cs`, after `ListSessions`:

```csharp
	public IReadOnlyDictionary<string, DateTimeOffset> LastActivity()
	{
		var last = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
		using (var connection = AicpDatabase.Open(_path))
		{
			using (var command = Command(connection, "SELECT repo, MAX(updated) FROM chat_session GROUP BY repo"))
			{
				using (var reader = command.ExecuteReader())
				{
					while (reader.Read())
					{
						var repo = reader.GetString(0);
						var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1));

						// Two spellings of one repo (SQLite groups by exact text) keep the newer.
						last[repo] = last.TryGetValue(repo, out var seen) && seen > at ? seen : at;
					}
				}
			}
		}

		return last;
	}
```

`MemoryChatStore.cs`, after `ListSessions`:

```csharp
	public IReadOnlyDictionary<string, DateTimeOffset> LastActivity()
	{
		lock (_lock)
		{
			return _sessions.GroupBy(s => s.Repo, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(s => s.Updated), StringComparer.OrdinalIgnoreCase);
		}
	}
```

`ChatService.cs`, after `Unsubscribe`:

```csharp
	/// <summary>Whether Claude is working in the repo now (one run per repo).</summary>
	public bool IsRunning(string repo) => _runs.ContainsKey(repo);
```

`SyncSessions.cs`, after `RecheckAsync`:

```csharp
	/// <summary>Whether a sync session (a browser with the folder open) has the repo now.</summary>
	public bool IsOpen(string repo)
	{
		lock (_watches)
		{
			return _watches.ContainsKey(repo);
		}
	}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Infrastructure.SqliteChatStoreTests --filter-class AiChromeProxy.Tests.Application.SyncSessionsWatchTests --filter-class AiChromeProxy.Tests.Application.ChatServiceTests`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Application/Chat src/AiChromeProxy.Application/Sync/SyncSessions.cs src/AiChromeProxy.Infrastructure/Chat/SqliteChatStore.cs tests/AiChromeProxy.Tests/Application tests/AiChromeProxy.Tests/Infrastructure/SqliteChatStoreTests.cs
git commit -m "Chat activity per repo, running runs and open folders"
```

---

### Task 4: One definition of the mirror's ignore rules

**Files:**
- Create: `src/AiChromeProxy.Application/Sync/MirrorRules.cs`
- Modify: `src/AiChromeProxy.Application/Sync/SyncSession.BackChannel.cs:136-141`

**Interfaces:**
- Produces: `MirrorRules.LoadAsync(IMirrorStore store, IProjectStore projects, string repo, CancellationToken ct)` → `Task<IgnoreRules>`.

This is a move without behaviour change; the existing back-channel tests are its tests.

- [ ] **Step 1: Create the shared rules**

`src/AiChromeProxy.Application/Sync/MirrorRules.cs`:

```csharp
using System.Text;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Sync;

/// <summary>What the server never deletes, pushes or serves of a repo: the mirror's <c>.gitignore</c> and the project's extra excludes.</summary>
public static class MirrorRules
{
	public static async Task<IgnoreRules> LoadAsync(IMirrorStore store, IProjectStore projects, string repo, CancellationToken ct)
	{
		var gitignore = await store.ReadAsync(repo, ".gitignore", 0, (int)SyncLimits.MaxFileSize, ct);
		return IgnoreRules.Create(gitignore is null ? null : Encoding.UTF8.GetString(gitignore), projects.GetSettings(repo).Excludes);
	}
}
```

- [ ] **Step 2: Use it in the back channel**

Replace `RulesAsync` in `SyncSession.BackChannel.cs` (lines 136–141) with:

```csharp
	/// <summary>The repo's <see cref="MirrorRules"/>: excluded files are never deleted, pushed or served.</summary>
	private Task<IgnoreRules> RulesAsync(string repo, CancellationToken ct) => MirrorRules.LoadAsync(store, projects, repo, ct);
```

Remove `using System.Text;` from that file if nothing else in it uses `Encoding` (`rg -n "Encoding\." src/AiChromeProxy.Application/Sync/SyncSession.BackChannel.cs`).

- [ ] **Step 3: Run the back-channel and session tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Application.SyncSessionBackChannelTests --filter-class AiChromeProxy.Tests.Application.SyncSessionTests`
Expected: all pass, same count as before the change.

- [ ] **Step 4: Commit**

```bash
git add src/AiChromeProxy.Application/Sync/MirrorRules.cs src/AiChromeProxy.Application/Sync/SyncSession.BackChannel.cs
git commit -m "Sync: share the mirror ignore rules"
```

---

### Task 5: MirrorBrowseHandler

**Files:**
- Create: `src/AiChromeProxy.Application/Projects/MirrorBrowseHandler.cs`
- Modify: `src/AiChromeProxy.Application/DependencyInjection.cs`, `tests/AiChromeProxy.Tests/Client/LoopbackServer.cs`
- Test: `tests/AiChromeProxy.Tests/Application/MirrorBrowseHandlerTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4 (`ListRepos`, `ListFileInfos`, `LastActivity`, `IsRunning`, `IsOpen`, `MirrorRules.LoadAsync`).
- Produces: `MirrorBrowseHandler(string type, IMirrorStore store, IProjectStore projects, IChatStore chats, ChatService chat, SyncSessions sessions) : IEnvelopeHandler`; `MirrorBrowseHandler.Types`; `public static ReposPayload ReposPage(IEnumerable<RepoInfo> repos)`; `public static MirrorFilesPayload FilesPage(string repo, IReadOnlyList<MirrorFile> sorted, string? after)`. Errors: `bad_request` for a bad repo name, an invalid or excluded path, a negative offset; `not_found` "This project is no longer on the server." for `mirror.list` of a missing repo folder and "'<path>' is not on the server." for a missing file.

- [ ] **Step 1: Write the failing tests**

`tests/AiChromeProxy.Tests/Application/MirrorBrowseHandlerTests.cs`:

```csharp
using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Projects;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Projects;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Client;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

/// <summary><c>repos.list</c>, <c>mirror.list</c> and <c>mirror.read</c> over a real mirror in a temp folder.</summary>
public sealed class MirrorBrowseHandlerTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero));
	private readonly MemoryProjectStore _projects = new();
	private readonly MemoryChatStore _chats;
	private readonly FakeAgentRunner _runner = new();
	private readonly ChatService _chat;
	private readonly SyncSessions _sessions;
	private readonly EnvelopeRouter _router;

	public MirrorBrowseHandlerTests()
	{
		Directory.CreateDirectory(_root);
		var store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
		_chats = new MemoryChatStore(_time);
		_chat = new ChatService(_chats, store, _projects, _runner, _time, new ListLogger<ChatService>());
		_sessions = new SyncSessions(store, _projects, new ListLogger<SyncSession>(), TimeProvider.System, new FakeMirrorWatcher());
		_router = new EnvelopeRouter([.. MirrorBrowseHandler.Types.Select(t => new MirrorBrowseHandler(t, store, _projects, _chats, _chat, _sessions))]);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private static EnvelopeContext Context => new("c1", null, (_, _) => Task.CompletedTask);

	public void Dispose()
	{
		_sessions.Close("c1");
		_chat.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public void Types_AreTheThreeRequests() =>
		Assert.Equal([MessageTypes.ReposList, MessageTypes.MirrorList, MessageTypes.MirrorRead], MirrorBrowseHandler.Types);

	[Fact]
	public async Task ReposList_ActivityRunningAndOpen_NewestFirst()
	{
		Write("quiet", "a.txt", "a");
		Write("busy", "a.txt", "a");
		Write("open", "a.txt", "a");
		_chats.CreateSession("open", "older");
		_time.Advance(TimeSpan.FromMinutes(1));
		_chats.CreateSession("busy", "newer");
		await _sessions.Get("c1").HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("open"), "o1"), Context, Ct);
		_chat.Send(new ChatSendPayload("busy", null, "work"), Context);
		var process = await _runner.NextAsync();

		var repos = Read<ReposPayload>(await RequestAsync(MessageTypes.ReposList, new { }));

		Assert.False(repos.Truncated);
		Assert.Equal(
			[
				new RepoInfo("busy", _time.GetUtcNow(), true, false),
				new RepoInfo("open", _time.GetUtcNow() - TimeSpan.FromMinutes(1), false, true),
				new RepoInfo("quiet", null, false, false),
			],
			repos.Repos);
		process.Exit();
	}

	[Fact]
	public void ReposPage_CutOffUnderTheByteLimit_FlagsTruncated()
	{
		var repos = Enumerable.Range(0, 400).Select(i => new RepoInfo($"{i:D3}" + new string('r', RepoName.MaxLength - 3), DateTimeOffset.MaxValue, true, true)).ToList();

		var page = MirrorBrowseHandler.ReposPage(repos);

		Assert.True(page.Truncated);
		Assert.InRange(page.Repos.Count, 100, 399);
		Assert.True(ManifestPlannerTests.WireSize(Envelope.Create(MessageTypes.Repos, page, Guid.NewGuid().ToString("N"))) < ManifestPlannerTests.SignalRLimit - 4096);
	}

	[Fact]
	public async Task MirrorList_PagesInPathOrder_LeavesOutExcluded()
	{
		Write("r", ".gitignore", "*.log\n");
		Write("r", "b.txt", "bb");
		Write("r", "a/z.cs", "z");
		Write("r", "debug.log", "x");
		Write("r", "secret.txt", "s");
		_projects.SaveSettings("r", new ProjectSettings { Excludes = "secret.txt" });

		var page = Read<MirrorFilesPayload>(await RequestAsync(MessageTypes.MirrorList, new MirrorListPayload("r")));

		Assert.Equal([".gitignore", "a/z.cs", "b.txt"], page.Files.Select(f => f.Path));
		Assert.Equal(2, page.Files[2].Size);
		Assert.Null(page.Next);
	}

	[Fact]
	public void FilesPage_EntryAndByteBounds_NextIsTheLastPath()
	{
		var files = Enumerable.Range(0, 1_200).Select(i => new MirrorFile($"{i:D4}/" + new string('ж', 250), long.MaxValue, DateTimeOffset.MaxValue)).ToList();

		var first = MirrorBrowseHandler.FilesPage("r", files, null);
		var second = MirrorBrowseHandler.FilesPage("r", files, first.Next);

		Assert.True(first.Files.Count <= SyncLimits.MaxPageEntries);
		Assert.Equal(first.Files[^1].Path, first.Next);
		Assert.Equal(files[first.Files.Count].Path, second.Files[0].Path);
		Assert.True(ManifestPlannerTests.WireSize(Envelope.Create(MessageTypes.MirrorFiles, first, Guid.NewGuid().ToString("N"))) < ManifestPlannerTests.SignalRLimit - 4096);

		var small = files.Take(3).ToList();
		Assert.Null(MirrorBrowseHandler.FilesPage("r", small, null).Next);
		Assert.Empty(MirrorBrowseHandler.FilesPage("r", small, small[^1].Path).Files);
	}

	[Fact]
	public async Task MirrorList_MissingRepo_NotFound()
	{
		var error = await ErrorAsync(MessageTypes.MirrorList, new MirrorListPayload("gone"));

		Assert.Equal((ErrorCodes.NotFound, "This project is no longer on the server."), (error.Code, error.Message));
	}

	[Fact]
	public async Task MirrorRead_ChunksLastAndHash_ExactBoundaryEndsWithAnEmptyChunk()
	{
		var content = Enumerable.Range(0, SyncLimits.ChunkSize).Select(i => (byte)i).ToArray();
		Directory.CreateDirectory(Path.Combine(_root, "r"));
		File.WriteAllBytes(Path.Combine(_root, "r", "f.bin"), content);

		var first = Read<SyncDataPayload>(await RequestAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", "f.bin", 0)));
		var last = Read<SyncDataPayload>(await RequestAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", "f.bin", SyncLimits.ChunkSize)));

		Assert.Equal((content, false, (string?)null), (SyncData.Decode(first.Data), first.Last, first.Sha256));
		Assert.Equal((0, true), (SyncData.Decode(last.Data).Length, last.Last));
		Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content)), last.Sha256);
	}

	[Theory]
	[InlineData("debug.log", 0, ErrorCodes.BadRequest)]
	[InlineData("../x.txt", 0, ErrorCodes.BadRequest)]
	[InlineData("a.txt", -1, ErrorCodes.BadRequest)]
	[InlineData("missing.txt", 0, ErrorCodes.NotFound)]
	public async Task MirrorRead_Refusals(string path, long offset, string code)
	{
		Write("r", ".gitignore", "*.log\n");
		Write("r", "a.txt", "a");
		Write("r", "debug.log", "x");

		var error = await ErrorAsync(MessageTypes.MirrorRead, new MirrorReadPayload("r", path, offset));

		Assert.Equal(code, error.Code);
	}

	[Fact]
	public async Task BadRepoName_BadRequest()
	{
		var error = await ErrorAsync(MessageTypes.MirrorList, new MirrorListPayload(" "));

		Assert.Equal(ErrorCodes.BadRequest, error.Code);
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private void Write(string repo, string path, string text)
	{
		var full = Path.Combine(_root, repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text, Encoding.UTF8);
	}

	private async Task<Envelope> RequestAsync<T>(string type, T payload) =>
		(await _router.RouteAsync(Envelope.Create(type, payload, "q1"), Context, Ct))!;

	private async Task<ErrorPayload> ErrorAsync<T>(string type, T payload)
	{
		var reply = await RequestAsync(type, payload);
		Assert.Equal(MessageTypes.Error, reply.Type);
		return Read<ErrorPayload>(reply);
	}
}
```

`EnvelopeRouter.RouteAsync(request, context, ct)` turns an `EnvelopeException` into an `error` reply (`ErrorPayload(Code, Message, Type)`), which is what `ErrorAsync` reads.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS0246 — `MirrorBrowseHandler` not found.

- [ ] **Step 3: Implement the handler**

`src/AiChromeProxy.Application/Projects/MirrorBrowseHandler.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Projects;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Projects;

/// <summary>
/// The read-only view of the server's projects (registered once per type, see <see cref="Types"/>): <c>repos.list</c>, <c>mirror.list</c> and
/// <c>mirror.read</c>. Outside any sync session: nothing here writes the mirror or the database. Excluded paths (<see cref="MirrorRules"/>)
/// are neither listed nor served.
/// </summary>
public sealed class MirrorBrowseHandler(string type, IMirrorStore store, IProjectStore projects, IChatStore chats, ChatService chat, SyncSessions sessions) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.ReposList, MessageTypes.MirrorList, MessageTypes.MirrorRead];

	public string Type => type;

	/// <summary>Most recent activity first (none last), then by name; as many as fit <see cref="SyncLimits.MaxPageBytes"/>.</summary>
	public static ReposPayload ReposPage(IEnumerable<RepoInfo> repos)
	{
		var page = new List<RepoInfo>();
		var bytes = 0;
		foreach (var repo in repos.OrderByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
		{
			var size = JsonSerializer.SerializeToUtf8Bytes(repo, JsonSerializerOptions.Web).Length + 1;
			if (bytes + size > SyncLimits.MaxPageBytes)
			{
				return new ReposPayload(page, true);
			}

			page.Add(repo);
			bytes += size;
		}

		return new ReposPayload(page, false);
	}

	/// <summary>The files after <paramref name="after"/> in <paramref name="sorted"/> (ordinal by path), bounded by entries and serialized bytes.</summary>
	public static MirrorFilesPayload FilesPage(string repo, IReadOnlyList<MirrorFile> sorted, string? after)
	{
		var page = new List<MirrorFile>();
		var bytes = 0;
		foreach (var file in sorted.Where(f => after is null || string.CompareOrdinal(f.Path, after) > 0))
		{
			var size = JsonSerializer.SerializeToUtf8Bytes(file, JsonSerializerOptions.Web).Length + 1;
			if (page.Count == SyncLimits.MaxPageEntries || bytes + size > SyncLimits.MaxPageBytes)
			{
				return new MirrorFilesPayload(repo, page, page[^1].Path);
			}

			page.Add(file);
			bytes += size;
		}

		return new MirrorFilesPayload(repo, page, null);
	}

	public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var reply = type switch
		{
			MessageTypes.ReposList => Envelope.Create(MessageTypes.Repos, Repos()),
			MessageTypes.MirrorList => Envelope.Create(MessageTypes.MirrorFiles, await ListAsync(Read<MirrorListPayload>(request), ct)),
			_ => Envelope.Create(MessageTypes.MirrorData, await ReadAsync(Read<MirrorReadPayload>(request), ct)),
		};

		return reply with { CorrelationId = request.CorrelationId };
	}

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private static string RepoOf(string? repo, string type) =>
		RepoName.Sanitize(repo) ?? throw BadRequest($"{type} needs the folder name in 'repo'.");

	private static T Read<T>(Envelope request)
		where T : class
	{
		try
		{
			return request.Payload.Deserialize<T>(JsonSerializerOptions.Web) ?? throw BadRequest($"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw BadRequest($"Invalid {request.Type} payload.");
		}
	}

	private ReposPayload Repos()
	{
		var activity = chats.LastActivity();
		return ReposPage(store.ListRepos().Select(name => new RepoInfo(
			name,
			activity.TryGetValue(name, out var at) ? at : null,
			chat.IsRunning(name),
			sessions.IsOpen(name))));
	}

	private async Task<MirrorFilesPayload> ListAsync(MirrorListPayload payload, CancellationToken ct)
	{
		var repo = RepoOf(payload.Repo, MessageTypes.MirrorList);
		if (store.RepoFolder(repo) is null)
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "This project is no longer on the server.");
		}

		var rules = await MirrorRules.LoadAsync(store, projects, repo, ct);
		var files = store.ListFileInfos(repo).Where(f => !rules.IsIgnored(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
		return FilesPage(repo, files, payload.After);
	}

	/// <summary>Like <c>sync.fetch</c>, without a session.</summary>
	private async Task<SyncDataPayload> ReadAsync(MirrorReadPayload payload, CancellationToken ct)
	{
		var repo = RepoOf(payload.Repo, MessageTypes.MirrorRead);
		if (SyncPath.GetError(payload.Path) is { } error)
		{
			throw BadRequest(error);
		}

		if (payload.Offset < 0)
		{
			throw BadRequest("mirror.read needs an offset of 0 or more.");
		}

		var path = payload.Path;
		if ((await MirrorRules.LoadAsync(store, projects, repo, ct)).IsIgnored(path))
		{
			throw BadRequest($"'{path}' is excluded from sync.");
		}

		var data = await store.ReadAsync(repo, path, payload.Offset, SyncLimits.ChunkSize, ct)
			?? throw new EnvelopeException(ErrorCodes.NotFound, $"'{path}' is not on the server.");

		// A file ending exactly at a chunk boundary gets one more, empty, last chunk.
		var last = data.Length < SyncLimits.ChunkSize;
		var sha256 = last ? await store.GetHashAsync(repo, path, ct) : null;
		return new SyncDataPayload(repo, path, payload.Offset, SyncData.Encode(data), last, sha256);
	}
}
```

- [ ] **Step 4: Register it**

`DependencyInjection.cs`, after the `PackagesHandler` loop:

```csharp
		foreach (var type in MirrorBrowseHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => ActivatorUtilities.CreateInstance<MirrorBrowseHandler>(sp, type));
		}
```

`LoopbackServer.cs`, in the router list after the `ClaudeToolsHandler` line:

```csharp
			.. MirrorBrowseHandler.Types.Select(t => new MirrorBrowseHandler(t, store, Projects, Chats, Chat, _sessions)),
```

(`using AiChromeProxy.Application.Projects;` is already there.)

- [ ] **Step 5: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Application.MirrorBrowseHandlerTests --filter-class AiChromeProxy.Tests.Infrastructure.DependencyInjectionTests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/AiChromeProxy.Application/Projects/MirrorBrowseHandler.cs src/AiChromeProxy.Application/DependencyInjection.cs tests/AiChromeProxy.Tests/Application/MirrorBrowseHandlerTests.cs tests/AiChromeProxy.Tests/Client/LoopbackServer.cs
git commit -m "Server: repos.list, mirror.list and mirror.read"
```

---

### Task 6: Client MirrorBrowser

**Files:**
- Create: `src/AiChromeProxy.Client/Projects/MirrorBrowser.cs`
- Modify: `src/AiChromeProxy.Client/Sync/SyncEngine.cs` (add `Visible`), `src/AiChromeProxy.Client/Program.cs`
- Test: `tests/AiChromeProxy.Tests/Client/MirrorBrowserTests.cs`

**Interfaces:**
- Consumes: Task 1 records; the server of Task 5 through `LoopbackServer`.
- Produces (namespace `AiChromeProxy.Client.Projects`): `MirrorBrowser(ITransport transport, SyncEngine sync, TimeProvider time)` with `event Action? Changed`, `string? Repo`, `IReadOnlyList<MirrorFile> Files`, `string? Problem`, `bool Loading`, `int Generation`, `const int ReadsInFlight = 8`, `static TimeSpan RefreshInterval` (30 s), `const string Gone`, `Task<ReposPayload> ListReposAsync(CancellationToken ct)`, `Task ViewAsync(string repo, CancellationToken ct)`, `void Leave()`, `Task RefreshAsync(CancellationToken ct)`, `MirrorFile? FileAt(string path)`, `Task<FileBytes?> ReadFileAsync(string path, CancellationToken ct)` (null: not on the server; throws `InvalidOperationException` with the reason otherwise), `Task RunAsync(CancellationToken ct)`. `SyncEngine.Visible` → `bool`.

- [ ] **Step 1: Write the failing tests**

`tests/AiChromeProxy.Tests/Client/MirrorBrowserTests.cs`:

```csharp
using AiChromeProxy.Client.Navigator;
using AiChromeProxy.Client.Projects;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Client;

/// <summary>The view of another computer's project against <see cref="LoopbackServer"/> (the real <c>MirrorBrowseHandler</c>).</summary>
public sealed class MirrorBrowserTests : IDisposable
{
	private const string Other = "Other_PC";

	private readonly LoopbackServer _server = new();
	private readonly FakeTimeProvider _time = new();
	private readonly SyncEngine _sync;
	private readonly MirrorBrowser _mirror;

	public MirrorBrowserTests()
	{
		_sync = new SyncEngine(_server.Transport, new FakeFolder(), TimeProvider.System);
		_mirror = new MirrorBrowser(_server.Transport, _sync, _time);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task ListRepos_TheServersFolders()
	{
		Write(Other, "a.txt", "a");

		var repos = await _mirror.ListReposAsync(Ct);

		Assert.Equal([Other], repos.Repos.Select(r => r.Name));
	}

	[Fact]
	public async Task View_LoadsEveryPage_Leave_Forgets()
	{
		for (var i = 0; i < SyncLimits.MaxPageEntries + 20; i++)
		{
			Write(Other, $"f{i:D4}.txt", "x");
		}

		var changes = 0;
		_mirror.Changed += () => changes++;
		await _mirror.ViewAsync(Other, Ct);

		Assert.Equal(Other, _mirror.Repo);
		Assert.Equal(SyncLimits.MaxPageEntries + 20, _mirror.Files.Count);
		Assert.Equal(1, _mirror.FileAt("f0000.txt")?.Size);
		Assert.True(changes > 0);
		Assert.Equal(2, _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorList));
		var generation = _mirror.Generation;

		_mirror.Leave();

		Assert.Null(_mirror.Repo);
		Assert.Empty(_mirror.Files);
		Assert.NotEqual(generation, _mirror.Generation);
	}

	[Fact]
	public async Task View_MissingRepo_Problem()
	{
		await _mirror.ViewAsync("gone", Ct);

		Assert.Equal(MirrorBrowser.Gone, _mirror.Problem);
		Assert.Empty(_mirror.Files);
	}

	[Fact]
	public async Task ReadFile_AssemblesChunksInOrder_WithReadsInFlight()
	{
		var content = string.Concat(Enumerable.Range(0, 5 * SyncLimits.ChunkSize / 10).Select(i => $"{i % 10_000:D9}\n"));
		Write(Other, "big.txt", content);
		await _mirror.ViewAsync(Other, Ct);

		// Delayed replies, so that requests in flight are counted (FakeTransport.MaxPendingReplies).
		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(5);
		var bytes = await _mirror.ReadFileAsync("big.txt", Ct);

		Assert.Equal(content, System.Text.Encoding.UTF8.GetString(bytes!.Bytes));
		Assert.Equal(content.Length, bytes.Size);
		Assert.InRange(_server.Transport.MaxPendingReplies, 2, MirrorBrowser.ReadsInFlight);
	}

	[Fact]
	public async Task ReadFile_Missing_Null_TooLarge_NotRead_Excluded_Throws()
	{
		Write(Other, ".gitignore", "*.log\n");
		Write(Other, "debug.log", "x");
		File.WriteAllBytes(_server.PathOf(Other, "huge.bin"), new byte[FileText.MaxBytes + 1]);
		await _mirror.ViewAsync(Other, Ct);

		Assert.Null(await _mirror.ReadFileAsync("missing.txt", Ct));
		var reads = _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorRead);
		var huge = await _mirror.ReadFileAsync("huge.bin", Ct);
		Assert.Equal((0, (long)FileText.MaxBytes + 1), (huge!.Bytes.Length, huge.Size));
		Assert.Equal(reads, _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorRead));
		var excluded = await Assert.ThrowsAsync<InvalidOperationException>(() => _mirror.ReadFileAsync("debug.log", Ct));
		Assert.Contains("excluded", excluded.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunAsync_RefreshesEveryInterval_WhileViewedAndVisible()
	{
		Write(Other, "a.txt", "a");
		await _mirror.ViewAsync(Other, Ct);
		using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var loop = _mirror.RunAsync(stop.Token);
			Write(Other, "b.txt", "b");

			_sync.SetVisible(false);
			_time.Advance(MirrorBrowser.RefreshInterval);
			await Task.Delay(100, Ct);
			Assert.Null(_mirror.FileAt("b.txt"));

			_sync.SetVisible(true);
			_time.Advance(MirrorBrowser.RefreshInterval);
			await UntilAsync(() => _mirror.FileAt("b.txt") is not null);

			stop.Cancel();
			await loop;
		}
	}

	private static async Task UntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the mirror browser.");
			await Task.Delay(10, Ct);
		}
	}

	private void Write(string repo, string path, string text)
	{
		var full = _server.PathOf(repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS0234 — `AiChromeProxy.Client.Projects` not found.

- [ ] **Step 3: Expose the tab's visibility**

In `SyncEngine.cs`, next to `public string? Repo => _repo;` (line 137):

```csharp
	/// <summary>Gets a value indicating whether the tab is visible (scans and other polling pause while it is hidden).</summary>
	public bool Visible => _visible;
```

- [ ] **Step 4: Implement MirrorBrowser**

`src/AiChromeProxy.Client/Projects/MirrorBrowser.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Client.Navigator;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Projects;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Projects;

/// <summary>
/// The read-only view of a project on the server (one synced from another computer): lists the server's projects (<c>repos.list</c>),
/// loads the viewed repo's files (<c>mirror.list</c>, every page) and reads them (<c>mirror.read</c>, up to <see cref="ReadsInFlight"/> chunks at once).
/// While a repo is viewed, <see cref="RunAsync"/> lists its files again every <see cref="RefreshInterval"/> when the tab is visible
/// (ponytail: polling; push mirror changes through IMirrorWatcher if 30 s proves too slow).
/// </summary>
public sealed class MirrorBrowser(ITransport transport, SyncEngine sync, TimeProvider time)
{
	/// <summary>Chunks of one file requested before the first reply is in.</summary>
	public const int ReadsInFlight = 8;

	/// <summary><see cref="Problem"/> when the viewed repo's folder is gone from the server.</summary>
	public const string Gone = "This project is no longer on the server";

	public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	private readonly Lock _gate = new();
	private Dictionary<string, MirrorFile> _byPath = new(StringComparer.Ordinal);

	/// <summary>Raised after every visible change; the UI re-renders.</summary>
	public event Action? Changed;

	/// <summary>Gets the viewed repo, or null when none is.</summary>
	public string? Repo { get; private set; }

	/// <summary>Gets the viewed repo's files as last listed, sorted by path.</summary>
	public IReadOnlyList<MirrorFile> Files { get; private set; } = [];

	/// <summary>Gets why the files could not be listed, or null.</summary>
	public string? Problem { get; private set; }

	/// <summary>Gets a value indicating whether a listing is under way.</summary>
	public bool Loading { get; private set; }

	/// <summary>Gets a number that changes whenever another repo is viewed or the view ends (file views are keyed on it).</summary>
	public int Generation { get; private set; }

	public async Task<ReposPayload> ListReposAsync(CancellationToken ct) =>
		Read<ReposPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.ReposList, new { }), RequestTimeout, ct));

	/// <summary>Views <paramref name="repo"/>: forgets the files of the one before and lists the new one's.</summary>
	public Task ViewAsync(string repo, CancellationToken ct)
	{
		lock (_gate)
		{
			Reset(repo);
		}

		Raise();
		return RefreshAsync(ct);
	}

	/// <summary>Ends the view.</summary>
	public void Leave()
	{
		lock (_gate)
		{
			Reset(null);
		}

		Raise();
	}

	/// <summary>Lists the viewed repo's files again (every page); a failure becomes <see cref="Problem"/>.</summary>
	public async Task RefreshAsync(CancellationToken ct)
	{
		string repo;
		int generation;
		lock (_gate)
		{
			if (Repo is null)
			{
				return;
			}

			(repo, generation) = (Repo, Generation);
			Loading = true;
		}

		Raise();
		List<MirrorFile>? files = null;
		string? problem = null;
		try
		{
			files = [];
			string? after = null;
			do
			{
				var page = Read<MirrorFilesPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.MirrorList, new MirrorListPayload(repo, after)), RequestTimeout, ct));
				files.AddRange(page.Files);
				after = page.Next;
			}
			while (after is not null);
		}
		catch (RequestFailedException ex) when (ex.Code == ErrorCodes.NotFound)
		{
			files = [];
			problem = Gone;
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			files = null;
			problem = "Could not load the files: " + ex.Message;
		}
		finally
		{
			lock (_gate)
			{
				if (generation == Generation)
				{
					Loading = false;
					Problem = problem;
					if (files is not null)
					{
						Files = files;
						_byPath = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
					}
				}
			}

			Raise();
		}
	}

	/// <summary>The listed file at the path, or null.</summary>
	public MirrorFile? FileAt(string path)
	{
		lock (_gate)
		{
			return _byPath.GetValueOrDefault(path);
		}
	}

	/// <summary>
	/// The file's bytes from the server: null when it is not there; a file listed over <see cref="FileText.MaxBytes"/> comes back empty with its size
	/// (the viewer shows "Too large") without being read.
	/// </summary>
	/// <exception cref="InvalidOperationException">The server refused (an excluded path) or did not answer.</exception>
	public async Task<FileBytes?> ReadFileAsync(string path, CancellationToken ct)
	{
		var repo = Repo ?? throw new InvalidOperationException("No project is viewed.");
		var listed = FileAt(path);
		if (listed is { Size: > FileText.MaxBytes })
		{
			return new FileBytes([], listed.Size);
		}

		// Without a listed size (a link to a file not listed yet) one chunk at a time, so a small file costs one request.
		var window = listed is { } known ? (int)Math.Clamp((known.Size / SyncLimits.ChunkSize) + 1, 1, ReadsInFlight) : 1;
		var pending = new Queue<Task<Envelope>>();
		var parts = new List<byte[]>();
		var total = 0L;
		var next = 0L;
		try
		{
			while (true)
			{
				while (pending.Count < window)
				{
					pending.Enqueue(transport.RequestAsync(Envelope.Create(MessageTypes.MirrorRead, new MirrorReadPayload(repo, path, next)), RequestTimeout, ct));
					next += SyncLimits.ChunkSize;
				}

				var part = Read<SyncDataPayload>(await pending.Dequeue());
				var data = SyncData.Decode(part.Data);
				parts.Add(data);
				total += data.Length;
				if (part.Last || total > FileText.MaxBytes)
				{
					return new FileBytes([.. parts.SelectMany(p => p)], total);
				}
			}
		}
		catch (RequestFailedException ex) when (ex.Code == ErrorCodes.NotFound)
		{
			return null;
		}
		catch (Exception ex) when (ex is RequestFailedException or TimeoutException)
		{
			throw new InvalidOperationException(ex.Message, ex);
		}
		finally
		{
			// Chunks past the end (or after a failure) are not needed; their failures must not go unobserved.
			foreach (var late in pending)
			{
				_ = late.ContinueWith(static t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
			}
		}
	}

	/// <summary>Lists the viewed repo's files again every <see cref="RefreshInterval"/> while the tab is visible and connected; ends on cancellation.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		try
		{
			while (true)
			{
				await Task.Delay(RefreshInterval, time, ct);
				if (Repo is not null && sync.Visible && transport.State == TransportState.Connected)
				{
					await RefreshAsync(ct);
				}
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Disposed.
		}
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	/// <summary>Call under the lock.</summary>
	private void Reset(string? repo)
	{
		Repo = repo;
		Files = [];
		_byPath = new Dictionary<string, MirrorFile>(StringComparer.Ordinal);
		Problem = null;
		Loading = false;
		Generation++;
	}

	private void Raise() => Changed?.Invoke();
}
```

Note: the `catch (RequestFailedException) when NotFound` above must come before the general one — keep that order. A read that is cancelled throws `OperationCanceledException` out of `ReadFileAsync`; `FileView` disposes on that path anyway.

`Program.cs`, after `builder.Services.AddSingleton<ChatEngine>();`:

```csharp
builder.Services.AddSingleton<MirrorBrowser>();
```

and `using AiChromeProxy.Client.Projects;` at the top.

- [ ] **Step 5: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Client.MirrorBrowserTests`
Expected: all pass.

- [ ] **Step 6: Commit**

```bash
git add src/AiChromeProxy.Client/Projects/MirrorBrowser.cs src/AiChromeProxy.Client/Sync/SyncEngine.cs src/AiChromeProxy.Client/Program.cs tests/AiChromeProxy.Tests/Client/MirrorBrowserTests.cs
git commit -m "Client: MirrorBrowser lists and reads another computer's project"
```

---

### Task 7: ChatEngine view mode

**Files:**
- Modify: `src/AiChromeProxy.Client/Chat/ChatEngine.cs`
- Test: `tests/AiChromeProxy.Tests/Client/ChatEngineTests.cs`

**Interfaces:**
- Produces: `ChatEngine.ViewAsync(string? repo)` → `Task`; `ChatEngine.Viewing` → `string?`; `ChatEngine.ReadOnlyNote` (const string "Read-only: open this folder on its computer to chat."); while viewing `SendAsync` returns false with that error line, `StopAsync` and `ApproveAsync` do nothing.

- [ ] **Step 1: Write the failing tests**

Add to `ChatEngineTests` (local repo is `My_Repo`; `ReadyAsync` syncs the fake folder and initializes the chat):

```csharp
	[Fact]
	public async Task View_OtherReposChatReadOnly_BackReopensTheFolders()
	{
		var other = _server.Chats.CreateSession("Other_PC", "their question");
		_server.Chats.Append(other.Id, [new ChatEvent(other.Id, "r1", 0, ChatEventKinds.Prompt, Text: "their question")]);
		await ReadyAsync();
		await UntilAsync(() => _chat.Repo == "My_Repo");

		await _chat.ViewAsync("Other_PC");
		await UntilAsync(() => _chat.Repo == "Other_PC" && _chat.Sessions.Count == 1);

		Assert.Equal("Other_PC", _chat.Viewing);
		Assert.Equal("their question", _chat.Sessions[0].Title);
		Assert.False(await _chat.SendAsync("hi"));
		Assert.Contains(_chat.Current.Items, i => i.Kind == ChatItemKind.Error && i.Text == ChatEngine.ReadOnlyNote);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatSend);

		await _chat.ViewAsync(null);
		await UntilAsync(() => _chat.Repo == "My_Repo");
		Assert.Null(_chat.Viewing);
		Assert.Empty(_chat.Sessions);
	}

	[Fact]
	public async Task View_RunningElsewhere_StopAndApproveSendNothing()
	{
		await ReadyAsync();
		_server.Chat.Send(new ChatSendPayload("My_Repo", null, "work"), new EnvelopeContext("other-tab", null, (_, _) => Task.CompletedTask));
		var process = await _server.Agent.NextAsync();

		// The same repo viewed from "another computer": the run is listed as running, but this page may not stop it.
		await _chat.ViewAsync("My_Repo");
		await UntilAsync(() => _chat.Running);
		await _chat.StopAsync();
		Assert.False(await _chat.ApproveAsync("nope", ChatDecisions.Allow));

		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type is MessageTypes.ChatCancel or MessageTypes.ChatApprove);
		process.Exit();
	}
```

Add `using AiChromeProxy.Application.Transport;` to the test file for `EnvelopeContext`. `ViewAsync("My_Repo")` is allowed by the engine (the shell never does it — picking "this folder" goes back instead), which makes the read-only guards testable with a run the loopback server already has.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS1061 — no `ViewAsync`, `Viewing`, `ReadOnlyNote`.

- [ ] **Step 3: Implement**

In `ChatEngine.cs`:

1. After `private const string TooLong = ...;` add:

```csharp
	/// <summary>The error line of a message typed while another project is viewed.</summary>
	public const string ReadOnlyNote = "Read-only: open this folder on its computer to chat.";
```

2. After `private bool _stopping;` add:

```csharp
	/// <summary>The repo shown read-only instead of the open folder's (another computer's project), or null.</summary>
	private string? _viewing;
```

3. After the `Repo` property add:

```csharp
	/// <summary>Gets the repo shown read-only (another computer's project on the server), or null for the open folder's chat.</summary>
	public string? Viewing
	{
		get
		{
			lock (_gate)
			{
				return _viewing;
			}
		}
	}
```

4. After `NewChat()` add:

```csharp
	/// <summary>
	/// Shows another project's chat read-only (<paramref name="repo"/>), or the open folder's again (null). <c>chat.open</c> moves this
	/// connection's subscription there, so the folder's own chat gets no pushes meanwhile; its history catches up on return.
	/// </summary>
	public async Task ViewAsync(string? repo)
	{
		lock (_gate)
		{
			_viewing = repo;
			if (repo is null && sync.Repo is null)
			{
				// Back without a folder of its own: nothing to show.
				Reset(null);
			}
		}

		Raise();
		await EnsureOpenAsync();
	}
```

5. At the start of `SendAsync`, before `ChatSession session;`:

```csharp
		if (Viewing is not null)
		{
			Current.AddError(ReadOnlyNote);
			Raise();
			return false;
		}
```

6. In `StopAsync`, change the condition `if (_stopping || !IsRunning() || (runId is null && repo is null))` to:

```csharp
			if (_viewing is not null || _stopping || !IsRunning() || (runId is null && repo is null))
```

7. In `ApproveAsync`, before `ChatApproval? card;`:

```csharp
		if (Viewing is not null)
		{
			return false;
		}
```

8. In `WantsOpen`, change `repo = sync.Repo ?? _repo ?? string.Empty;` to:

```csharp
		repo = _viewing ?? sync.Repo ?? _repo ?? string.Empty;
```

9. In `EnsureOpenAsync`, replace the body of `if (repo != _repo) { ... }` (the nine assignments) with `Reset(repo);`, and add the helper after `GetOrCreate`:

```csharp
	/// <summary>Forgets the shown repo's sessions and run state; call under the lock.</summary>
	private void Reset(string? repo)
	{
		_sessions.Clear();
		_listed = [];
		_current = new ChatSession(null);
		_awaitingText = null;
		_since = null;
		_liveCost = null;
		_stopping = false;
		_repo = repo;
	}
```

10. Update the class summary's first sentence to: "The browser side of the chat: opens the open folder's chat (or, with <see cref="ViewAsync"/>, another project's, read-only) on the server ...".

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Client.ChatEngineTests --filter-class AiChromeProxy.Tests.Client.ChatUiTests`
Expected: all pass (the existing tests unchanged).

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Client/Chat/ChatEngine.cs tests/AiChromeProxy.Tests/Client/ChatEngineTests.cs
git commit -m "Chat: view another project's chat read-only"
```

---

### Task 8: Projects menu and view mode in the shell

**Files:**
- Create: `src/AiChromeProxy.Client/Projects/ProjectLine.cs`, `src/AiChromeProxy.Client/Shell/ProjectsMenu.razor`
- Modify: `src/AiChromeProxy.Client/Shell/TopBar.razor`, `src/AiChromeProxy.Client/Pages/Home.razor`, `src/AiChromeProxy.Client/Shell/Format.cs`, `src/AiChromeProxy.Client/wwwroot/css/app.css`
- Test: `tests/AiChromeProxy.Tests/Client/ProjectsUiTests.cs`

**Interfaces:**
- Consumes: `MirrorBrowser` (Task 6), `ChatEngine.ViewAsync` (Task 7), `RepoInfo`.
- Produces: `ProjectLine.Of(RepoInfo repo, string? localRepo, DateTimeOffset now)` → `string`; `ProjectLine.Move(string key, int index, int count)` → `int`; `Format.ChatPlaceholder(bool connected, bool folderOpen, bool readOnly = false)`; `Home` fields `_viewing` (repo or null) and methods `ViewProjectAsync(string repo)`, `BackAsync()`; TopBar parameters `Mirror`, `LocalRepo`, `Viewing`, `BackAttention`, `OnPickProject` (`EventCallback<string>`), `OnBack`. Test ids: `projects-button`, `projects-menu`, `project-item` (with `data-repo`), `projects-retry`, `read-only`, `back-to-folder`; `folder-name` stays on the label span.

`*.razor` is excluded from coverage; the logic sits in `ProjectLine` and `Format`, which are tested.

- [ ] **Step 1: Write the failing tests**

`tests/AiChromeProxy.Tests/Client/ProjectsUiTests.cs`:

```csharp
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Projects;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Tests.Client;

public sealed class ProjectsUiTests
{
	private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Line_ThisFolderWorkingOnlineAndTime()
	{
		Assert.Equal("this folder · 5 min ago", ProjectLine.Of(new RepoInfo("My_Repo", Now.AddMinutes(-5), false, true), "my_repo", Now));
		Assert.Equal("Claude is working · online · 42 s ago", ProjectLine.Of(new RepoInfo("Other", Now.AddSeconds(-42), true, true), "My_Repo", Now));
		Assert.Equal("no chat yet", ProjectLine.Of(new RepoInfo("Other", null, false, false), null, Now));
	}

	[Theory]
	[InlineData("ArrowDown", -1, 3, 0)]
	[InlineData("ArrowDown", 2, 3, 0)]
	[InlineData("ArrowUp", 0, 3, 2)]
	[InlineData("ArrowUp", -1, 3, 2)]
	[InlineData("Home", 2, 3, 0)]
	[InlineData("End", 0, 3, 2)]
	[InlineData("x", 1, 3, 1)]
	[InlineData("ArrowDown", -1, 0, -1)]
	public void Move_WrapsAround(string key, int index, int count, int expected) =>
		Assert.Equal(expected, ProjectLine.Move(key, index, count));

	[Fact]
	public void Placeholder_ReadOnlyWins()
	{
		Assert.Equal(ChatEngine.ReadOnlyNote, Format.ChatPlaceholder(true, true, readOnly: true));
		Assert.Equal("Message Claude. Enter sends, Shift+Enter adds a line.", Format.ChatPlaceholder(true, true));
	}
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet build -c Release`
Expected: CS0103/CS0117 — no `ProjectLine`, no 3-argument `ChatPlaceholder`.

- [ ] **Step 3: Implement ProjectLine and the placeholder**

`src/AiChromeProxy.Client/Projects/ProjectLine.cs`:

```csharp
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Client.Projects;

/// <summary>What the projects menu shows and how its keyboard moves.</summary>
public static class ProjectLine
{
	/// <summary>"this folder · Claude is working · online · 5 min ago": the parts that apply, the chat's last activity last.</summary>
	public static string Of(RepoInfo repo, string? localRepo, DateTimeOffset now)
	{
		var local = string.Equals(repo.Name, localRepo, StringComparison.OrdinalIgnoreCase);
		var parts = new List<string>();
		if (local)
		{
			parts.Add("this folder");
		}

		if (repo.Running)
		{
			parts.Add("Claude is working");
		}

		if (repo.Open && !local)
		{
			parts.Add("online");
		}

		parts.Add(repo.LastActivity is { } at ? Format.Ago(at, now) : "no chat yet");
		return string.Join(" · ", parts);
	}

	/// <summary>The item focused after <paramref name="key"/> (arrows wrap around; -1: none focused yet).</summary>
	public static int Move(string key, int index, int count)
	{
		if (count == 0)
		{
			return -1;
		}

		return key switch
		{
			"ArrowDown" => (index + 1) % count,
			"ArrowUp" => index <= 0 ? count - 1 : index - 1,
			"Home" => 0,
			"End" => count - 1,
			_ => index,
		};
	}
}
```

In `Format.cs`, replace `ChatPlaceholder` with:

```csharp
	public static string ChatPlaceholder(bool connected, bool folderOpen, bool readOnly = false) => (connected, folderOpen) switch
	{
		_ when readOnly => ChatEngine.ReadOnlyNote,
		(true, true) => "Message Claude. Enter sends, Shift+Enter adds a line.",
		(_, false) => "Open a folder to chat with Claude",
		_ => "Waiting for the connection… you can write when it is back",
	};
```

(add `using AiChromeProxy.Client.Chat;` to `Format.cs` if it is not there.)

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Client.ProjectsUiTests`
Expected: all pass.

- [ ] **Step 5: The projects menu**

`src/AiChromeProxy.Client/Shell/ProjectsMenu.razor`:

```razor
@using AiChromeProxy.Client.Projects
@using AiChromeProxy.Domain.Projects

<span class="projects-host">
    <button type="button" class="crumb-folder crumb-button" data-testid="projects-button" aria-haspopup="menu" aria-expanded="@(_open ? "true" : "false")"
            disabled="@(!Enabled)" title="@(Enabled ? "Projects on the server" : "Not connected")" @onclick="ToggleAsync">
        <Icon Name="folder" />
        @if (Label is null)
        {
            <span>Projects on the server</span>
        }
        else
        {
            <span data-testid="folder-name">@Label</span>
        }
        <Icon Name="chevron-down" Size="12" />
    </button>
    @if (_open)
    {
        <button type="button" class="menu-backdrop" tabindex="-1" aria-label="Close the projects menu" @onclick="Close"></button>
        <div class="chat-menu projects-menu" role="menu" aria-label="Projects on the server" data-testid="projects-menu" @onkeydown="OnKeyAsync">
            @if (_error is not null)
            {
                <div class="chat-menu-empty" role="alert">
                    <span class="state-error">@_error</span>
                    <button type="button" class="button link" data-testid="projects-retry" @onclick="LoadAsync">Retry</button>
                </div>
            }
            else if (_repos is null)
            {
                <div class="chat-menu-empty muted">Loading…</div>
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                @foreach (var repo in _repos.Repos)
                {
                    var current = string.Equals(repo.Name, Viewing ?? LocalRepo, StringComparison.OrdinalIgnoreCase);
                    <button type="button" role="menuitem" class="chat-menu-item @(current ? "current" : null)" data-testid="project-item" data-repo="@repo.Name" @key="repo.Name"
                            @ref="_items[repo.Name]" @onclick="() => PickAsync(repo.Name)">
                        <Icon Name="@(repo.Running ? "sync" : "folder")" Size="14" Class="@(repo.Running ? "spin" : "kind-folder")" />
                        <span class="chat-menu-title">@repo.Name</span>
                        <span class="muted chat-menu-time">@ProjectLine.Of(repo, LocalRepo, now)</span>
                    </button>
                }
                @if (_repos.Repos.Count == 0)
                {
                    <div class="chat-menu-empty muted">No projects on the server yet.</div>
                }
                @if (_repos.Truncated)
                {
                    <div class="chat-menu-empty muted">More projects are not shown.</div>
                }
            }
        </div>
    }
</span>

@code {
    private readonly Dictionary<string, ElementReference> _items = new(StringComparer.Ordinal);
    private bool _open;
    private bool _focusFirst;
    private int _focused = -1;
    private ReposPayload? _repos;
    private string? _error;

    [Parameter, EditorRequired]
    public MirrorBrowser Mirror { get; set; } = null!;

    /// <summary>The name on the button: the viewed project's, else the open folder's; null shows "Projects on the server".</summary>
    [Parameter]
    public string? Label { get; set; }

    /// <summary>The open folder's repo name (marked "this folder").</summary>
    [Parameter]
    public string? LocalRepo { get; set; }

    /// <summary>The project viewed read-only, or null.</summary>
    [Parameter]
    public string? Viewing { get; set; }

    /// <summary>The connection is up.</summary>
    [Parameter]
    public bool Enabled { get; set; }

    [Parameter]
    public EventCallback<string> OnPick { get; set; }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusFirst && _repos is { Repos.Count: > 0 } repos && _items.TryGetValue(repos.Repos[0].Name, out var first))
        {
            _focusFirst = false;
            _focused = 0;
            await first.FocusAsync();
        }
    }

    private async Task ToggleAsync()
    {
        if (_open)
        {
            Close();
            return;
        }

        _open = true;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _repos = null;
        _error = null;
        _items.Clear();
        StateHasChanged();
        try
        {
            _repos = await Mirror.ListReposAsync(CancellationToken.None);
            _focusFirst = true;
        }
        catch (Exception ex)
        {
            _error = "Could not load the projects: " + ex.Message;
        }
    }

    private void Close()
    {
        _open = false;
        _focused = -1;
    }

    private async Task PickAsync(string repo)
    {
        Close();
        await OnPick.InvokeAsync(repo);
    }

    private async Task OnKeyAsync(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
        {
            Close();
            return;
        }

        var names = _repos?.Repos.Select(r => r.Name).ToList() ?? [];
        _focused = ProjectLine.Move(e.Key, _focused, names.Count);
        if (_focused >= 0 && _items.TryGetValue(names[_focused], out var item))
        {
            await item.FocusAsync();
        }
    }
}
```

- [ ] **Step 6: Top bar**

In `TopBar.razor` add `@using AiChromeProxy.Client.Projects` at the top and replace the `@if (FolderName is not null) { ... crumb-folder ... }` block with:

```razor
    <span class="crumb-sep" aria-hidden="true">/</span>
    <ProjectsMenu Mirror="Mirror" Label="@(Viewing ?? FolderName)" LocalRepo="@LocalRepo" Viewing="@Viewing" Enabled="@(State == TransportState.Connected)" OnPick="OnPickProject" />
    @if (Viewing is not null)
    {
        <span class="pill pill-readonly" data-testid="read-only" title="Files and chat of this project are read from the server">Read-only</span>
        <button type="button" class="button secondary topbar-back @(BackAttention is null ? null : "has-attention")" data-testid="back-to-folder" @onclick="OnBack">
            Back to my folder
            @if (BackAttention is not null)
            {
                <span class="attention"><Icon Name="warning" Size="14" />@BackAttention</span>
            }
        </button>
    }
```

and add these parameters in `@code`:

```csharp
    [Parameter, EditorRequired]
    public MirrorBrowser Mirror { get; set; } = null!;

    /// <summary>The open folder's repo name, or null.</summary>
    [Parameter]
    public string? LocalRepo { get; set; }

    /// <summary>The project viewed read-only, or null.</summary>
    [Parameter]
    public string? Viewing { get; set; }

    /// <summary>What the local folder needs from the user while another project is viewed (<see cref="Format.Attention"/>), or null.</summary>
    [Parameter]
    public string? BackAttention { get; set; }

    [Parameter]
    public EventCallback<string> OnPickProject { get; set; }

    [Parameter]
    public EventCallback OnBack { get; set; }
```

- [ ] **Step 7: Home**

In `Pages/Home.razor`:

1. Add `@using AiChromeProxy.Client.Projects` and `@inject MirrorBrowser Mirror`.
2. `PageTitle` becomes `<PageTitle>@((_viewing ?? Engine.FolderName) is { } title ? $"{title} — ai-chrome-proxy" : "ai-chrome-proxy")</PageTitle>`.
3. `TopBar`: add `Mirror="Mirror" LocalRepo="@Engine.Repo" Viewing="@_viewing" BackAttention="@(_viewing is null ? null : Format.Attention(Engine.Folder, Engine.ErrorCount))" OnPickProject="ViewProjectAsync" OnBack="BackAsync"` and change `SettingsEnabled` to `@(_viewing is null && Engine.Folder == FolderStatus.Ready && Engine.FolderName is not null)`.
4. `Explorer` and `MainArea`: add `Mirror="@(_viewing is null ? null : Mirror)"` (their parameters come in Task 9; until then this step leaves them out — add them in Task 9, Step 5).
5. Fields: change `private readonly TabSet _tabs = new();` to `private TabSet _tabs = new();` and add:

```csharp
    /// <summary>The open folder's tabs while another project is viewed (the view has its own).</summary>
    private TabSet? _savedTabs;

    /// <summary>The project viewed read-only, or null.</summary>
    private string? _viewing;

    private bool _wasRunning;
```

6. `Dispose`: add `Mirror.Changed -= OnEngineChanged;`. `OnInitializedAsync`: add `Mirror.Changed += OnEngineChanged;` next to `Chat.Changed += ...`, and `_ = Mirror.RunAsync(_stop.Token);` after `_ = Engine.RunAsync(_stop.Token);`.
7. `OnEngineChanged` becomes:

```csharp
    private void OnEngineChanged()
    {
        _tabs.CloseResolved(ConflictedPaths());

        // A run of the viewed project ended: its edits are on the server now.
        var running = Chat.Running;
        if (_viewing is not null && _wasRunning && !running)
        {
            _ = Mirror.RefreshAsync(_stop.Token);
        }

        _wasRunning = running;
        _render?.Request();
    }
```

8. `OnStateChanged`: inside `if (state == TransportState.Connected)` add `if (_viewing is not null) { _ = Mirror.RefreshAsync(_stop.Token); }`.
9. Add the two handlers:

```csharp
    /// <summary>Another project opens read-only with tabs of its own; picking the open folder goes back.</summary>
    private async Task ViewProjectAsync(string repo)
    {
        if (string.Equals(repo, Engine.Repo, StringComparison.OrdinalIgnoreCase))
        {
            await BackAsync();
            return;
        }

        _savedTabs ??= _tabs;
        _tabs = new TabSet();
        _link = null;
        _viewing = repo;
        _wasRunning = false;
        await Task.WhenAll(Mirror.ViewAsync(repo, _stop.Token), Chat.ViewAsync(repo));
    }

    /// <summary>Back to the open folder: its tabs and chat return (the chat catches up with its history).</summary>
    private async Task BackAsync()
    {
        if (_viewing is null)
        {
            return;
        }

        _viewing = null;
        _tabs = _savedTabs ?? _tabs;
        _savedTabs = null;
        _link = null;
        Mirror.Leave();
        await Chat.ViewAsync(null);
    }
```

- [ ] **Step 8: Styles**

Append to `wwwroot/css/app.css`:

```css
/* Projects on the server (top bar) */
.projects-host {
    position: relative;
    display: inline-flex;
}

.crumb-button {
    padding: 2px 6px;
    border: 0;
    border-radius: var(--radius);
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
}

.crumb-button:hover:not(:disabled),
.crumb-button[aria-expanded="true"] {
    background: var(--bg-hover);
}

.projects-menu {
    left: 0;
    right: auto;
    width: 420px;
}

.pill-readonly {
    border-color: var(--warn);
    color: var(--warn);
}

.topbar-back {
    margin-left: 6px;
}
```

- [ ] **Step 9: Build and run the client tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class AiChromeProxy.Tests.Client.ProjectsUiTests --filter-class AiChromeProxy.Tests.Architecture.ClientMarkupTests`
Expected: build without errors or new warnings; tests pass.

- [ ] **Step 10: Commit**

```bash
git add src/AiChromeProxy.Client tests/AiChromeProxy.Tests/Client/ProjectsUiTests.cs
git commit -m "Shell: projects menu in the top bar and the read-only view mode"
```

---

### Task 9: Read-only Explorer, file views and chat controls

**Files:**
- Create: `src/AiChromeProxy.Client/Shell/MirrorTreeView.razor`
- Modify: `src/AiChromeProxy.Client/Shell/Explorer.razor`, `src/AiChromeProxy.Client/Shell/MainArea.razor`, `src/AiChromeProxy.Client/Shell/FileView.razor`, `src/AiChromeProxy.Client/Shell/ChatView.razor`, `src/AiChromeProxy.Client/Navigator/FileContent.cs`, `src/AiChromeProxy.Client/Pages/Home.razor`, `src/AiChromeProxy.Client/wwwroot/css/app.css`
- Test: `tests/AiChromeProxy.Tests/Client/FileViewLogicTests.cs`

**Interfaces:**
- Consumes: `MirrorBrowser` (Task 6), Home's `_viewing` (Task 8).
- Produces: `Explorer.Mirror`, `MainArea.Mirror`, `FileView.Mirror` (`MirrorBrowser?`; non-null = read from the mirror), `ChatView.ReadOnly` (`bool`); `FileContent.MissingOnServer` ("This file is no longer on the server"); `FileContent.Decide(FileBytes? bytes, string missing = Missing)`. Test ids: `mirror-tree`, `mirror-refresh`, `mirror-problem`.

- [ ] **Step 1: Write the failing test**

Add to `FileViewLogicTests.cs`:

```csharp
	[Fact]
	public void Decide_MissingNoteIsTheCallers()
	{
		Assert.Equal((null, FileContent.Missing), FileContent.Decide(null));
		Assert.Equal((null, "This file is no longer on the server"), FileContent.Decide(null, FileContent.MissingOnServer));
	}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet build -c Release`
Expected: CS0117 — no `MissingOnServer`.

- [ ] **Step 3: FileContent**

In `FileContent.cs` add after `Missing`:

```csharp
	/// <summary>The note of a mirror file that is not on the server (any more).</summary>
	public const string MissingOnServer = "This file is no longer on the server";
```

and change `Decide` to take the note:

```csharp
	/// <param name="bytes">What <see cref="IFolderAccess.ReadFileAsync"/> (or the mirror) returned: null for a missing file.</param>
	/// <param name="missing">The note for a missing file.</param>
	/// <returns>The text to show, or the note shown instead (exactly one of them is set).</returns>
	public static (string? Text, string? Note) Decide(FileBytes? bytes, string missing = Missing)
	{
		if (bytes is null)
		{
			return (null, missing);
		}
```

(the rest unchanged). Run `dotnet test ... --filter-class AiChromeProxy.Tests.Client.FileViewLogicTests` — passes.

- [ ] **Step 4: The read-only tree**

`src/AiChromeProxy.Client/Shell/MirrorTreeView.razor`:

```razor
@using AiChromeProxy.Domain.Projects

@* The tree of a project viewed read-only: the same rows and keys as FileTreeView, without menus, edits or sync badges. *@
@{
    var tabStop = FileTree.TabStop(_rows, _active);
}
<div class="tree" role="tree" aria-label="Files on the server" data-testid="mirror-tree" @onkeydown="OnKeyDownAsync">
    @foreach (var row in _rows)
    {
        var node = row.Node;
        <div @key="node.Path" @ref="_rowElements[node.Path]" role="treeitem" tabindex="@(node.Path == tabStop ? 0 : -1)"
             aria-level="@(row.Depth + 1)" aria-expanded="@(node.IsFolder ? (_expanded.Contains(node.Path) ? "true" : "false") : null)" aria-selected="@(node.Path == Selected ? "true" : "false")"
             class="tree-row @(node.Path == Selected ? "selected" : null)" style="padding-left: @(8 + (row.Depth * 14))px" title="@node.Path"
             @onclick="() => ClickAsync(node)" @onfocus="() => _active = node.Path">
            @if (node.IsFolder)
            {
                var open = _expanded.Contains(node.Path);
                <Icon Name="@(open ? "chevron-down" : "chevron-right")" Class="chevron" />
                <Icon Name="@(open ? "folder-open" : "folder")" Class="kind-folder" />
            }
            else
            {
                var kind = Format.FileKind(node.Name);
                <span class="chevron"></span>
                <Icon Name="@("file-" + kind)" Class="@("kind-" + kind)" />
            }
            <span class="tree-name">@node.Name</span>
        </div>
    }
</div>

@code {
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ElementReference> _rowElements = new(StringComparer.Ordinal);
    private IReadOnlyList<MirrorFile>? _files;
    private IReadOnlyList<TreeNode> _roots = [];
    private IReadOnlyList<TreeRow> _rows = [];
    private string? _active;
    private bool _focusActive;

    [Parameter, EditorRequired]
    public IReadOnlyList<MirrorFile> Files { get; set; } = [];

    /// <summary>The file of the active tab, highlighted.</summary>
    [Parameter]
    public string? Selected { get; set; }

    [Parameter]
    public EventCallback<string> OnSelect { get; set; }

    protected override void OnParametersSet()
    {
        // A new list only after a listing: rebuild the tree then, not on every render.
        if (!ReferenceEquals(Files, _files))
        {
            _files = Files;
            _roots = FileTree.Build(Files.Select(f => f.Path));
        }

        _rows = FileTree.Rows(_roots, _expanded);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_focusActive && _active is not null && _rowElements.TryGetValue(_active, out var row))
        {
            _focusActive = false;
            try
            {
                await row.FocusAsync();
            }
            catch (JSException)
            {
                // The row is gone (the listing changed).
            }
        }
    }

    private async Task ClickAsync(TreeNode node)
    {
        _active = node.Path;
        if (!node.IsFolder)
        {
            await OnSelect.InvokeAsync(node.Path);
            return;
        }

        if (!_expanded.Remove(node.Path))
        {
            _expanded.Add(node.Path);
        }

        _rows = FileTree.Rows(_roots, _expanded);
    }

    private async Task OnKeyDownAsync(KeyboardEventArgs e)
    {
        var (active, open) = FileTree.OnKey(e.Key, _rows, _active, _expanded);
        _focusActive = e.Key is "ArrowUp" or "ArrowDown" or "Home" or "End" or "ArrowLeft" or "ArrowRight";
        _active = active;
        _rows = FileTree.Rows(_roots, _expanded);
        if (open is not null)
        {
            await OnSelect.InvokeAsync(open);
        }
    }
}
```

- [ ] **Step 5: Explorer**

In `Explorer.razor` add `@using AiChromeProxy.Client.Projects`; in the Explorer `tabpanel`, put this branch first (the existing `@if (Engine.Folder == FolderStatus.Unsupported)` becomes `else if`):

```razor
        @if (Mirror is not null)
        {
            <div class="folder-row">
                <Icon Name="folder" Class="kind-folder" />
                <span class="folder-name" title="@Mirror.Repo">@Mirror.Repo</span>
                <button type="button" class="button link" data-testid="mirror-refresh" disabled="@Mirror.Loading" @onclick="() => Mirror.RefreshAsync(CancellationToken.None)">Refresh</button>
            </div>
            <p class="muted mirror-note">Read-only: the files on the server.</p>
            @if (Mirror.Problem is { } problem)
            {
                <div class="empty-state" role="alert" data-testid="mirror-problem">
                    <Icon Name="warning" Size="32" Class="empty-icon" />
                    <p>@problem</p>
                </div>
            }
            else if (Mirror.Loading && Mirror.Files.Count == 0)
            {
                <p class="muted mirror-note" role="status">Loading…</p>
            }
            else
            {
                <MirrorTreeView Files="Mirror.Files" Selected="@Selected" OnSelect="OnSelect" />
            }
        }
```

Wrap `<SyncStatus ... />` in `@if (Mirror is null) { ... }`, and add the parameter:

```csharp
    /// <summary>The project viewed read-only (its tree replaces the folder's), or null.</summary>
    [Parameter]
    public MirrorBrowser? Mirror { get; set; }
```

- [ ] **Step 6: FileView**

In `FileView.razor`:

1. Add `@using AiChromeProxy.Client.Projects` and the parameter:

```csharp
    /// <summary>The project viewed read-only: the file is read from the server; null reads the open folder.</summary>
    [Parameter]
    public MirrorBrowser? Mirror { get; set; }
```

2. Header: replace `var file = Engine.FileAt(Path);` with `var file = Mirror is null ? Engine.FileAt(Path) : null;` and, before `@if (file is not null)`, add:

```razor
        @if (Mirror?.FileAt(Path) is { } remote)
        {
            <span class="muted" title="@(Format.Count(remote.Size) + " bytes")">@Format.Bytes(remote.Size)</span>
            <span class="state state-synced">On the server</span>
        }
```

3. Stamp: change the field `private (string? Sha256, int Writes)? _read;` to `private string? _read;`, keep `_read = null;` resets, and replace `Stamp()` with:

```csharp
    /// <summary>What changes when the file should be read again: the listing's size and time on the server, else the sync's hash and write count.</summary>
    private string Stamp() => Mirror is { } mirror
        ? mirror.FileAt(Path) is { } remote ? $"{remote.Size}:{remote.Modified.UtcTicks}" : "missing"
        : $"{Engine.FileAt(Path)?.Sha256}:{Engine.WriteCount}";
```

4. In `ReadAndShowAsync`, replace `bytes = await Folder.ReadFileAsync(Path);` with:

```csharp
            bytes = Mirror is { } mirror ? await mirror.ReadFileAsync(Path, CancellationToken.None) : await Folder.ReadFileAsync(Path);
```

and `FileContent.Decide(bytes)` with `FileContent.Decide(bytes, Mirror is null ? FileContent.Missing : FileContent.MissingOnServer)`.

- [ ] **Step 7: MainArea and ChatView**

In `MainArea.razor`:

1. `@using AiChromeProxy.Client.Projects`; parameter:

```csharp
    /// <summary>The project viewed read-only (file tabs read from the server, the chat cannot send), or null.</summary>
    [Parameter]
    public MirrorBrowser? Mirror { get; set; }

    private bool ReadOnly => Mirror is not null;
```

2. `ChatEnabled => ChatConnected && ChatFolderOpen && !ReadOnly;`
3. `ChatInput`: `Placeholder="@Format.ChatPlaceholder(ChatConnected, ChatFolderOpen, ReadOnly)"`.
4. `ChatView`: `FolderOpen="@(ChatFolderOpen && !ReadOnly)" ReadOnly="ReadOnly"`.
5. New chat menu item: add `disabled="@ReadOnly" title="@(ReadOnly ? ChatEngine.ReadOnlyNote : null)"`.
6. `FileView`: `@key="(Mirror?.Generation ?? Engine.FolderGeneration, ReadOnly, id)" ... Mirror="Mirror"`.
7. `SyncPool`: change `private int _generation;` to `private (bool ReadOnly, int Generation) _generation;` and the check to:

```csharp
        var generation = (ReadOnly, Mirror?.Generation ?? Engine.FolderGeneration);
        if (_generation != generation)
        {
            _generation = generation;
            Pool.Clear();
        }
```

In `ChatView.razor` add:

```csharp
    /// <summary>Another project is viewed: permission requests are shown but cannot be answered from here.</summary>
    [Parameter]
    public bool ReadOnly { get; set; }
```

and on the three approval buttons change `disabled="@answered"` to `disabled="@(answered || ReadOnly)"`.

In `Home.razor` now add `Mirror="@(_viewing is null ? null : Mirror)"` to `<Explorer ...>` and `<MainArea ...>`.

Append to `app.css`:

```css
.mirror-note {
    margin: 0 10px 6px;
    font-size: 12px;
}
```

- [ ] **Step 8: Full gate**

Run:

```bash
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Expected: build clean; all tests pass; coverage ≥ 85 %.

- [ ] **Step 9: Commit**

```bash
git add src/AiChromeProxy.Client tests/AiChromeProxy.Tests/Client/FileViewLogicTests.cs
git commit -m "Shell: read-only tree, file views and chat while viewing another project"
```

---

### Task 10: E2E — another computer's project

**Files:**
- Create: `tests/AiChromeProxy.E2E/Features/Projects.feature`, `tests/AiChromeProxy.E2E/StepDefinitions/ProjectsSteps.cs`

**Interfaces:**
- Consumes: test ids from Tasks 8–9; existing steps `the app is connected with a folder open` (ShellSteps), `I click {string} and then {string} in the file tree` and `the viewer shows {string}` (NavigatorSteps), `a file tab {string} is active` (ChatSteps); `BrowserHooks.MirrorRoot` (namespace `AiChromeProxy.E2E.Hooks`).

- [ ] **Step 1: Feature**

`tests/AiChromeProxy.E2E/Features/Projects.feature`:

```gherkin
Feature: Server projects
	Projects synced from other computers can be looked at read-only: their files and chat come from the server.
	Another computer's project stands for itself as a folder written straight into the server's mirror.

Background:
	Given the app is connected with a folder open
	And the server holds the project "other-pc" with the file "src/A.cs" reading "class FromTheOtherComputer {}"

Scenario: Another computer's project opens read-only and the way back restores my folder
	When I open the projects menu
	Then the projects menu lists "other-pc" as "no chat yet"
	When I pick the project "other-pc"
	Then the shell shows "other-pc" read-only
	When I click "src" and then "A.cs" in the file tree
	Then a file tab "A.cs" is active
	And the viewer shows "FromTheOtherComputer"
	When I go back to my folder
	Then the shell shows my folder "e2e-repo" again
```

- [ ] **Step 2: Steps**

`tests/AiChromeProxy.E2E/StepDefinitions/ProjectsSteps.cs`:

```csharp
using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/ProjectsMenu.razor, TopBar.razor and MirrorTreeView.razor.</summary>
[Binding]
public sealed class ProjectsSteps(IPage page)
{
	[Given("the server holds the project {string} with the file {string} reading {string}")]
	public void GivenTheServerHoldsTheProject(string repo, string path, string text)
	{
		var full = Path.Combine(BrowserHooks.MirrorRoot, repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}

	[When("I open the projects menu")]
	public async Task WhenIOpenTheProjectsMenuAsync() => await page.GetByTestId("projects-button").ClickAsync();

	[Then("the projects menu lists {string} as {string}")]
	public async Task ThenTheProjectsMenuListsAsync(string repo, string line) =>
		await Expect(page.Locator($"[data-testid=project-item][data-repo='{repo}']")).ToContainTextAsync(line);

	[When("I pick the project {string}")]
	public async Task WhenIPickTheProjectAsync(string repo) =>
		await page.Locator($"[data-testid=project-item][data-repo='{repo}']").ClickAsync();

	[Then("the shell shows {string} read-only")]
	public async Task ThenTheShellShowsReadOnlyAsync(string repo)
	{
		await Expect(page.GetByTestId("read-only")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(repo);
		await Expect(page.GetByTestId("mirror-tree")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("chat-input")).ToBeDisabledAsync();
		await Expect(page.GetByTestId("open-settings")).ToBeDisabledAsync();
	}

	[When("I go back to my folder")]
	public async Task WhenIGoBackToMyFolderAsync() => await page.GetByTestId("back-to-folder").ClickAsync();

	[Then("the shell shows my folder {string} again")]
	public async Task ThenTheShellShowsMyFolderAgainAsync(string folder)
	{
		await Expect(page.GetByTestId("read-only")).ToHaveCountAsync(0);
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(folder);
		await Expect(page.GetByTestId("mirror-tree")).ToHaveCountAsync(0);
	}
}
```

`the viewer shows {string}` is the existing step in `NavigatorSteps` (the visible Monaco editor's `.view-lines`); do not bind it again.

- [ ] **Step 3: Run the E2E suite**

Run (Chromium installed per `docs/testing.md`):

```bash
dotnet build tests/AiChromeProxy.E2E
dotnet test --project tests/AiChromeProxy.E2E
```

Expected: every scenario passes, the new one included; no CSP violations reported by the hooks.

- [ ] **Step 4: Commit**

```bash
git add tests/AiChromeProxy.E2E/Features/Projects.feature tests/AiChromeProxy.E2E/StepDefinitions/ProjectsSteps.cs
git commit -m "E2E: another computer's project, read-only"
```

---

### Task 11: Docs

**Files:**
- Create: `docs/projects.md`
- Modify: `CLAUDE.md` (Stack list), `docs/superpowers/specs/2026-10-23-server-projects-design.md`

- [ ] **Step 1: `docs/projects.md`**

```markdown
# Server projects

Every folder synced to the server is a project there, whichever computer synced it. The folder name in the top bar opens **Projects on the server**: each project with "this folder" (the one open in this browser), "Claude is working", "online" (a browser has the folder open now) and when its chat was last active. Design: [server projects spec](superpowers/specs/2026-10-23-server-projects-design.md).

Picking another project shows it **read-only**: its chat (history and live answers), its file tree and its files, all read from the server. **Back to my folder** returns to the folder open in this browser, with its tabs and chat. The folder keeps syncing meanwhile; its errors and conflicts show on the **Back** button.

Nothing in the view changes the other project: sending, **Stop**, permission answers, attachments, **New chat**, tree actions and **Project settings** are disabled. To work in that project, open its folder on its computer.

## Protocol

| Request → reply | Payload |
|---|---|
| `repos.list` → `repos` | `{}` → `{repos: [{name, lastActivity, running, open}], truncated}` |
| `mirror.list` → `mirror.files` | `{repo, after}` → `{repo, files: [{path, size, modified}], next}` (pages of at most 500 entries / 24 000 bytes) |
| `mirror.read` → `mirror.data` | `{repo, path, offset}` → `{repo, path, offset, data, last, sha256}` (16 KB chunks, base64url) |

Excluded paths (the mirror's `.gitignore` and the project's extra excludes) are neither listed nor served. None of the three writes anything.

## Known limitations

- While another project is viewed, the open folder's chat gets no live pushes (one chat subscription per connection); its runs go on and the history catches up on return.
- Folders with the same name on two computers share one mirror and one chat.
- Empty folders of the mirror are not shown.
- Files Claude creates in the other project appear within 30 s (while the tab is visible), when its run ends, or at once with **Refresh**.

## Manual checklist

- [ ] Sync folder A on computer 1 and folder B on computer 2. On computer 1 the menu lists A ("this folder") and B ("online"); close B's tab on computer 2: B loses "online" on the next open of the menu.
- [ ] Pick B: the read-only pill and **Back to my folder** show; the tree is B's; a file opens with highlighting; the chat input, **Stop**, **New chat** and **Project settings** are disabled.
- [ ] Start a run in B on computer 2: computer 1 sees "Claude is working" in the menu and the live answer in B's chat; when it ends, the files Claude changed refresh in the tree and an open file marks its changed lines.
- [ ] A chat link `path:line` in B's chat opens B's file from the server.
- [ ] **Back to my folder**: A's tree, tabs and chat return; a run that went on in A meanwhile shows in full.
- [ ] Delete B's mirror folder on the server while viewing it: **Refresh** shows "This project is no longer on the server".
- [ ] Disconnect while viewing: the projects button is disabled ("Not connected"); after reconnect the view's chat and files load again.
```

- [ ] **Step 2: CLAUDE.md**

In the Stack list, after the "Private NuGet packages" bullet, add:

```markdown
- Server projects (read-only view of folders synced from other computers): [docs/projects.md](docs/projects.md) — `Application/Projects/MirrorBrowseHandler` (`repos.list`, `mirror.list`, `mirror.read`; reads only), `Client/Projects` (`MirrorBrowser`, `ProjectLine`), `Shell/ProjectsMenu.razor`, `Shell/MirrorTreeView.razor`, `ChatEngine.ViewAsync`.
```

- [ ] **Step 3: Spec touch-ups (the plan's decisions)**

In the spec's Decisions table:
- **Client: view mode** — replace "a banner under the top bar" with "in the top bar, after the projects button: a **Read-only** pill and **Back to my folder**" (the shell grid has no row for a banner).
- **Client: files** — replace the `IFileReader` design with: "`FileView` gets an optional `Mirror` (`MirrorBrowser`): set, it reads through `MirrorBrowser.ReadFileAsync` (up to 8 chunks in flight; a file listed over 5 MB is not read) and re-reads when the listing's size or time changes; the file bar shows the size and "On the server"."
- **Client: refresh** — replace "its tab closes with" by "its tab shows".

In the Errors table:
- **Excluded path from a chat link** — the tab shows "Could not read the file: '…' is excluded from sync.".
- **Connection lost while viewing** — "the connection pill shows Reconnecting…; after reconnect the view sends `chat.open` and `mirror.list` again" (no extra banner text).

In Testing, E2E: "the server's mirror holds another computer's project (written by the step); the browser lists it, opens it, reads a file, cannot send, and **Back to my folder** restores its own folder" — the live chat of another computer is covered by `ChatEngineTests` and the manual checklist.

- [ ] **Step 4: Commit**

```bash
git add docs/projects.md CLAUDE.md docs/superpowers/specs/2026-10-23-server-projects-design.md
git commit -m "Docs: server projects"
```

---

### Final check

- [ ] `dotnet build -c Release` clean; the coverage gate command from Global Constraints passes.
- [ ] `dotnet test --project tests/AiChromeProxy.E2E` passes.
- [ ] `rg -n "using var " src tests` finds nothing new; `git diff master --stat` touches only the files listed above.
- [ ] Push the branch, open the PR, and before saying "done" check `gh pr checks <pr-number>` is green (never `| tail` a `--watch`).
