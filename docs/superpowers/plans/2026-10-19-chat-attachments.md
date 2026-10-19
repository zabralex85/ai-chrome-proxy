# Chat attachments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Paste, drop or pick files and pictures into the chat box; they upload to the home server and Claude reads them by path with the message.

**Architecture:** The browser keeps each `File` under a handle (`Scripts/chat.ts`); a client `ChatAttachments` uploads it in 16 KB base64url chunks over the hub (`chat.attach` → `chat.attached`, same scheme as `sync.chunk`). The server's `ChatAttachments` (Application) checks and stores them through `IAttachmentStore` (file system, `<DataDir>\attachments\<repo>\<id>\<name>`, outside the mirror). `chat.send` carries the ids; `ChatService` appends their absolute paths to the text given to Claude, passes `--add-dir <attachments\repo>` and stores `{name, size}` on the `prompt` event for chips in the history.

**Tech Stack:** .NET 10, C#, ASP.NET Core SignalR, Blazor WebAssembly, TypeScript (MSBuild), xunit v3, Reqnroll + Playwright.

**Spec:** [2026-10-19-chat-attachments-design.md](../specs/2026-10-19-chat-attachments-design.md)

## Global Constraints

- Everything in English; no personal paths, domains or secrets in committed files.
- UTF-8 without BOM, tabs, CRLF, no `this.`, private fields `_camelCase`, StyleCop errors fail the build.
- Disposables in block form `using (...) { }` — never `using var`.
- Interfaces prefixed `I`; async methods end in `Async` and return `Task`/`Task<T>`.
- Browser code only as strict TypeScript in `src/AiChromeProxy.Client/Scripts` (no `any`, no hand-written `.js`).
- JS interop behind a seam with a thin `[ExcludeFromCodeCoverage]` wrapper; logic stays in tested C#.
- The `Envelope` JSON (camelCase) is a public contract shared with the Flutter app: only optional additions.
- Every path from the protocol is checked to stay under its root.
- Limits: 20 MB per file (`20L * 1024 * 1024`), 10 attachments per message, 16 KB raw per chunk, 50 uploads in progress per repo, names ≤ 100 chars, clean-up after 30 days (`.part` after 1 day).
- Exact user-facing texts: "'{name}' is larger than 20 MB.", "At most 10 files per message.", "Too many uploads in progress.", "An attachment has expired; attach it again.", "Attach files", "Remove {name}".
- Gate: `dotnet build -c Release` clean, `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` green.

## Deviations from the spec (decided while planning; Task 7 updates the spec)

- Clean-up runs lazily: when an upload starts and the last sweep is over a day old (so the first upload after a start sweeps). No timer.
- An unknown attachment id on `chat.send` is `bad_request` (not `not_found`): the client already shows a `bad_request`'s own message, so no client mapping is needed.
- After a refused send the chips stay; the text box is cleared as it is today.

## File structure

| File | Responsibility |
|---|---|
| `src/AiChromeProxy.Domain/Chat/ChatAttachPayload.cs` (new) | `chat.attach` payload |
| `src/AiChromeProxy.Domain/Chat/ChatAttachedPayload.cs` (new) | `chat.attached` payload |
| `src/AiChromeProxy.Domain/Chat/ChatAttachment.cs` (new) | `{name, size}` on a `prompt` event |
| `src/AiChromeProxy.Domain/Chat/ChatSendPayload.cs`, `ChatEvent.cs`, `ChatLimits.cs`, `ChatEventSplitter.cs`, `MessageTypes.cs` | optional `attachments`, limits, message types |
| `src/AiChromeProxy.Application/Chat/IAttachmentStore.cs` (new) | storage port |
| `src/AiChromeProxy.Application/Chat/StoredAttachment.cs` (new) | a finished attachment (path, name, size) |
| `src/AiChromeProxy.Application/Chat/AttachmentNames.cs` (new) | file name sanitizing (pure) |
| `src/AiChromeProxy.Application/Chat/ChatAttachments.cs` (new) | upload checks, state, limits, resolve, lazy sweep |
| `src/AiChromeProxy.Application/Chat/ChatAttachHandler.cs` (new) | routes `chat.attach` |
| `src/AiChromeProxy.Application/Chat/ChatService.cs`, `AgentRun.cs`, `DependencyInjection.cs` | send with attachments |
| `src/AiChromeProxy.Infrastructure/Chat/AttachmentsOptions.cs` (new) | section `Attachments`, root resolution |
| `src/AiChromeProxy.Infrastructure/Chat/FileSystemAttachmentStore.cs` (new) | file-system store |
| `src/AiChromeProxy.Infrastructure/Chat/ClaudeArguments.cs`, `Hosting/DataDirectory.cs`, `DependencyInjection.cs` | `--add-dir`, default root, DI |
| `src/AiChromeProxy.Server/Program.cs`, `Hosting/ClientPage.cs` | root resolution, CSP `img-src blob:` |
| `src/AiChromeProxy.Client/Chat/IAttachmentSource.cs`, `AttachmentFile.cs`, `PendingAttachment.cs`, `ChatAttachments.cs` (new) | client upload logic |
| `src/AiChromeProxy.Client/Chat/ChatEngine.cs`, `ChatSession.cs`, `ChatItem.cs` | send ids, history chips |
| `src/AiChromeProxy.Client/Scripts/chat.ts`, `Shell/JsChatView.cs`, `Shell/ChatInput.razor`, `Shell/ChatView.razor`, `Shell/MainArea.razor`, `Shell/Icon.razor`, `Program.cs`, `wwwroot/css/app.css` | browser glue and UI |
| `tests/AiChromeProxy.E2E/...` | scenario, steps, fixture, attachments root |
| `docs/chat.md`, the spec | docs |

---

### Task 1: Wire contract

**Files:**
- Create: `src/AiChromeProxy.Domain/Chat/ChatAttachPayload.cs`, `src/AiChromeProxy.Domain/Chat/ChatAttachedPayload.cs`, `src/AiChromeProxy.Domain/Chat/ChatAttachment.cs`
- Modify: `src/AiChromeProxy.Domain/Chat/ChatLimits.cs`, `src/AiChromeProxy.Domain/MessageTypes.cs`, `src/AiChromeProxy.Domain/Chat/ChatSendPayload.cs`, `src/AiChromeProxy.Domain/Chat/ChatEvent.cs`, `src/AiChromeProxy.Domain/Chat/ChatEventSplitter.cs`
- Test: `tests/AiChromeProxy.Tests/Domain/ChatContractTests.cs`

**Interfaces:**
- Produces: `ChatLimits.MaxAttachmentBytes` (long), `ChatLimits.MaxAttachments` (int), `ChatLimits.AttachmentChunkSize` (int); `MessageTypes.ChatAttach`, `MessageTypes.ChatAttached`; `record ChatAttachPayload(string Repo, string Id, string Name, long Size, long Offset, string Data, bool Last)`; `record ChatAttachedPayload(string Repo, string Id)`; `record ChatAttachment(string Name, long Size)`; `ChatSendPayload(..., IReadOnlyList<string>? Attachments = null)`; `ChatEvent(..., IReadOnlyList<ChatAttachment>? Attachments = null)`. Both new optional fields are left out of the JSON when null, so existing shapes do not change.

- [ ] **Step 1: Write the failing tests** — append to `ChatContractTests`:

```csharp
	[Fact]
	public void Attachments_AreOptional_CamelCase_AndLeftOutWhenNull()
	{
		Assert.Equal("""{"repo":"r","sessionId":null,"text":"hi","attachments":["a"]}""", Json(new ChatSendPayload("r", null, "hi", ["a"])));
		Assert.Equal("""{"repo":"r","sessionId":null,"text":"hi"}""", Json(new ChatSendPayload("r", null, "hi")));
		Assert.Equal("""{"repo":"r","id":"i","name":"n.png","size":3,"offset":0,"data":"AAEC","last":true}""", Json(new ChatAttachPayload("r", "i", "n.png", 3, 0, "AAEC", true)));
		Assert.Equal("""{"repo":"r","id":"i"}""", Json(new ChatAttachedPayload("r", "i")));
		Assert.EndsWith(""","truncated":null,"attachments":[{"name":"n.png","size":3}]}""", Json(new ChatEvent("s", "u", 1, ChatEventKinds.Prompt, Text: string.Empty, Attachments: [new ChatAttachment("n.png", 3)])));
		Assert.Null(JsonSerializer.Deserialize<ChatSendPayload>("""{"repo":"r","text":"hi"}""", JsonSerializerOptions.Web)!.Attachments);
		Assert.Equal(new[] { "a" }, JsonSerializer.Deserialize<ChatSendPayload>("""{"repo":"r","text":"hi","attachments":["a"]}""", JsonSerializerOptions.Web)!.Attachments);
	}

	[Fact]
	public void Split_KeepsAttachmentsOnTheFirstPartOnly()
	{
		var e = new ChatEvent("s", "u", 1, ChatEventKinds.Prompt, Text: new string('a', ChatLimits.MaxTextChars + 10), Attachments: [new ChatAttachment("a.png", 1)]);

		var parts = ChatEventSplitter.Split(e);

		Assert.Equal(2, parts.Count);
		Assert.Equal("a.png", Assert.Single(parts[0].Attachments!).Name);
		Assert.Null(parts[1].Attachments);
	}

	[Fact]
	public void AttachmentLimits_MatchTheSpec()
	{
		Assert.Equal(20L * 1024 * 1024, ChatLimits.MaxAttachmentBytes);
		Assert.Equal(10, ChatLimits.MaxAttachments);
		Assert.Equal(16 * 1024, ChatLimits.AttachmentChunkSize);
		Assert.Equal("chat.attach", MessageTypes.ChatAttach);
		Assert.Equal("chat.attached", MessageTypes.ChatAttached);
	}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet build -c Release`
Expected: compile errors (`ChatAttachPayload`, `ChatLimits.MaxAttachmentBytes` ... not found).

- [ ] **Step 3: Implement**

`ChatLimits.cs` — add after `MaxNameBytes`:

```csharp
	/// <summary>Largest attachment (<c>chat.attach</c>), in bytes.</summary>
	public const long MaxAttachmentBytes = 20L * 1024 * 1024;

	/// <summary>Most attachments of one <c>chat.send</c>.</summary>
	public const int MaxAttachments = 10;

	/// <summary>Largest <c>chat.attach</c> chunk before encoding, in bytes; base64url keeps the message well under SignalR's 32 KB limit.</summary>
	public const int AttachmentChunkSize = 16 * 1024;
```

`MessageTypes.cs` — add after `ChatEvent`:

```csharp
	/// <summary>Client → server: part of a file attached to the next message (<c>Chat.ChatAttachPayload</c>); the last one is answered with <see cref="ChatAttached"/>.</summary>
	public const string ChatAttach = "chat.attach";

	/// <summary>Server → client: an attachment is stored (<c>Chat.ChatAttachedPayload</c>).</summary>
	public const string ChatAttached = "chat.attached";
```

`ChatAttachPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>chat.attach</c>: part of a file for the next message. <paramref name="Id"/> is 32 lowercase hex characters chosen by the client;
/// <paramref name="Data"/> is base64url without padding, at most <see cref="ChatLimits.AttachmentChunkSize"/> bytes raw. Chunks come in order
/// (offset 0 starts the upload over) and only the <paramref name="Last"/> one is answered (<c>chat.attached</c>, or the upload's first error).
/// </summary>
public sealed record ChatAttachPayload(string Repo, string Id, string Name, long Size, long Offset, string Data, bool Last);
```

`ChatAttachedPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.attached</c>: the attachment is stored; <c>chat.send</c> may name <paramref name="Id"/>.</summary>
public sealed record ChatAttachedPayload(string Repo, string Id);
```

`ChatAttachment.cs`:

```csharp
namespace AiChromeProxy.Domain.Chat;

/// <summary>A file attached to a message, as the history lists it (the content stays on the server).</summary>
public sealed record ChatAttachment(string Name, long Size);
```

`ChatSendPayload.cs`:

```csharp
using System.Text.Json.Serialization;

namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>chat.send</c>: a user message (at most <see cref="ChatLimits.MaxTextChars"/> characters); no <paramref name="SessionId"/> starts a new session.
/// <paramref name="Attachments"/> lists ids of finished <c>chat.attach</c> uploads (at most <see cref="ChatLimits.MaxAttachments"/>); with one, the text may be empty.
/// </summary>
public sealed record ChatSendPayload(
	string Repo,
	string? SessionId,
	string Text,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<string>? Attachments = null);
```

`ChatEvent.cs` — add `using System.Text.Json.Serialization;` at the top, document the new parameter in the summary ("<paramref name="Attachments"/> lists a <c>prompt</c>'s files (left out of the JSON when null)."), and add after `bool? Truncated = null`:

```csharp
	bool? Truncated = null,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ChatAttachment>? Attachments = null);
```

`ChatEventSplitter.cs` — in the split loop replace `parts.Add(e with { Text = text.Substring(start, length) });` with:

```csharp
			// Attachments belong to the message once: the first part carries them.
			parts.Add(e with { Text = text.Substring(start, length), Attachments = parts.Count == 0 ? e.Attachments : null });
```

and add to the `Split` summary: "only the first part keeps <c>attachments</c>".

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*ChatContractTests"`
Expected: PASS (all, including the unchanged `EventShapes`).

- [ ] **Step 5: Commit**

```bash
git add src/AiChromeProxy.Domain tests/AiChromeProxy.Tests/Domain/ChatContractTests.cs
git commit -m "Chat attachments: wire contract (chat.attach, attachments on send and prompt)"
```

---

### Task 2: File-system attachment store

**Files:**
- Create: `src/AiChromeProxy.Application/Chat/IAttachmentStore.cs`, `src/AiChromeProxy.Application/Chat/StoredAttachment.cs`, `src/AiChromeProxy.Infrastructure/Chat/AttachmentsOptions.cs`, `src/AiChromeProxy.Infrastructure/Chat/FileSystemAttachmentStore.cs`
- Modify: `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs`, `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`, `src/AiChromeProxy.Server/Program.cs:41`
- Test: `tests/AiChromeProxy.Tests/Infrastructure/FileSystemAttachmentStoreTests.cs`

**Interfaces:**
- Produces: `IAttachmentStore` (below), `record StoredAttachment(string Path, string Name, long Size)`, `AttachmentsOptions { Section = "Attachments"; string Root; static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot) }`, `FileSystemAttachmentStore(IOptions<AttachmentsOptions>)`, `DataDirectory.Attachments`.

- [ ] **Step 1: Write the failing tests** — `tests/AiChromeProxy.Tests/Infrastructure/FileSystemAttachmentStoreTests.cs`:

```csharp
using AiChromeProxy.Infrastructure.Chat;
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class FileSystemAttachmentStoreTests : IDisposable
{
	private const string Id = "0123456789abcdef0123456789abcdef";
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly FileSystemAttachmentStore _store;

	public FileSystemAttachmentStoreTests() => _store = new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _root }));

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void BeginAppendCommit_LeavesTheNamedFile_AndFindReturnsIt()
	{
		_store.Begin("repo", Id, "shot.png");
		Assert.Equal(2, _store.Append("repo", Id, "shot.png", [1, 2]));
		Assert.Null(_store.Find("repo", Id));
		Assert.Equal(3, _store.Append("repo", Id, "shot.png", [3]));

		_store.Commit("repo", Id, "shot.png");

		var found = _store.Find("repo", Id)!;
		Assert.Equal(Path.Combine(_root, "repo", Id, "shot.png"), found.Path);
		Assert.Equal("shot.png", found.Name);
		Assert.Equal(3, found.Size);
		Assert.Equal([1, 2, 3], File.ReadAllBytes(found.Path));
	}

	[Fact]
	public void Begin_AgainStartsOver()
	{
		_store.Begin("repo", Id, "a.txt");
		_store.Append("repo", Id, "a.txt", [1, 2, 3]);
		_store.Commit("repo", Id, "a.txt");

		_store.Begin("repo", Id, "b.txt");

		Assert.Null(_store.Find("repo", Id));
		Assert.Equal(["b.txt.part"], Directory.GetFiles(Path.Combine(_root, "repo", Id)).Select(Path.GetFileName));
	}

	[Fact]
	public void Delete_RemovesTheFolder_AndIgnoresAMissingOne()
	{
		_store.Begin("repo", Id, "a.txt");

		_store.Delete("repo", Id);
		_store.Delete("repo", Id);

		Assert.False(Directory.Exists(Path.Combine(_root, "repo", Id)));
	}

	[Fact]
	public void RepoFolder_IsUnderTheRoot_AndPathsLeavingItThrow()
	{
		Assert.Equal(Path.Combine(_root, "repo"), _store.RepoFolder("repo"));
		Assert.Throws<ArgumentException>(() => _store.RepoFolder(".."));
		Assert.Throws<ArgumentException>(() => _store.Begin("repo", "..", "a.txt"));
		Assert.Throws<ArgumentException>(() => _store.Begin("repo", Id, "..\\..\\..\\x.txt"));
	}

	[Fact]
	public void Sweep_DeletesOldFolders_AndStaleUploads_KeepsTheRest()
	{
		var now = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
		var old = Finished("11111111111111111111111111111111", now.AddDays(-31));
		var recent = Finished("22222222222222222222222222222222", now.AddDays(-29));
		var stalePart = Started("33333333333333333333333333333333", now.AddDays(-2));
		var freshPart = Started("44444444444444444444444444444444", now.AddHours(-1));

		var deleted = _store.Sweep(now.AddDays(-30), now.AddDays(-1));

		Assert.Equal(2, deleted);
		Assert.False(Directory.Exists(old));
		Assert.True(Directory.Exists(recent));
		Assert.False(Directory.Exists(stalePart));
		Assert.True(Directory.Exists(freshPart));
	}

	[Fact]
	public void Sweep_WithoutARoot_DeletesNothing() => Assert.Equal(0, _store.Sweep(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot()
	{
		var content = Path.Combine(_root, "content");
		var dataDir = new DataDirectory(Path.Combine(_root, "data"));

		Assert.Equal(Path.Combine(_root, "a"), AttachmentsOptions.ResolveRoot(Path.Combine(_root, "a"), dataDir, content));
		Assert.Equal(Path.Combine(content, "rel"), AttachmentsOptions.ResolveRoot("rel", dataDir, content));
		Assert.Equal(Path.Combine(_root, "data", "attachments"), AttachmentsOptions.ResolveRoot(null, dataDir, content));
		Assert.Equal(Path.Combine(content, "data", "attachments"), AttachmentsOptions.ResolveRoot(" ", null, content));
	}

	private string Finished(string id, DateTimeOffset written)
	{
		_store.Begin("repo", id, "a.txt");
		_store.Append("repo", id, "a.txt", [1]);
		_store.Commit("repo", id, "a.txt");
		File.SetLastWriteTimeUtc(Path.Combine(_root, "repo", id, "a.txt"), written.UtcDateTime);
		return Path.Combine(_root, "repo", id);
	}

	private string Started(string id, DateTimeOffset written)
	{
		_store.Begin("repo", id, "a.txt");
		File.SetLastWriteTimeUtc(Path.Combine(_root, "repo", id, "a.txt.part"), written.UtcDateTime);
		return Path.Combine(_root, "repo", id);
	}
}
```

- [ ] **Step 2: Run to see them fail**

Run: `dotnet build -c Release`
Expected: compile errors (`FileSystemAttachmentStore` not found).

- [ ] **Step 3: Implement**

`src/AiChromeProxy.Application/Chat/StoredAttachment.cs`:

```csharp
namespace AiChromeProxy.Application.Chat;

/// <summary>A finished attachment on the server.</summary>
/// <param name="Path">Absolute path of the file.</param>
/// <param name="Name">Its file name.</param>
/// <param name="Size">Its length in bytes.</param>
public sealed record StoredAttachment(string Path, string Name, long Size);
```

`src/AiChromeProxy.Application/Chat/IAttachmentStore.cs`:

```csharp
namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Where chat attachments live: <c>&lt;root&gt;\&lt;repo&gt;\&lt;id&gt;\&lt;name&gt;</c>, a file being uploaded as <c>&lt;name&gt;.part</c>.
/// Callers pass sanitized repo names, checked ids and sanitized names; a path that would still leave the root throws <see cref="ArgumentException"/>.
/// </summary>
public interface IAttachmentStore
{
	/// <summary>The repo's attachments folder (absolute; it may not exist yet).</summary>
	string RepoFolder(string repo);

	/// <summary>Empties the attachment's folder and creates an empty <c>&lt;name&gt;.part</c> in it.</summary>
	void Begin(string repo, string id, string name);

	/// <summary>Appends to <c>&lt;name&gt;.part</c>; returns its new length.</summary>
	long Append(string repo, string id, string name, byte[] data);

	/// <summary>Renames <c>&lt;name&gt;.part</c> to <c>&lt;name&gt;</c>.</summary>
	void Commit(string repo, string id, string name);

	/// <summary>The attachment's finished file, or null.</summary>
	StoredAttachment? Find(string repo, string id);

	/// <summary>Deletes the attachment's folder; no error when it is gone.</summary>
	void Delete(string repo, string id);

	/// <summary>
	/// Deletes attachment folders whose newest file was written before <paramref name="olderThan"/>, or before <paramref name="partsOlderThan"/>
	/// when the folder holds an unfinished <c>.part</c>; returns how many folders were deleted. A folder that cannot be deleted now is skipped.
	/// </summary>
	int Sweep(DateTimeOffset olderThan, DateTimeOffset partsOlderThan);
}
```

`src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs` — add after `Mirror`:

```csharp
	/// <summary>Default <c>Attachments:Root</c> when the Server runs with a data directory.</summary>
	public string Attachments => Path.Combine(Root, "attachments");
```

`src/AiChromeProxy.Infrastructure/Chat/AttachmentsOptions.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>Section <c>Attachments</c>: where files attached to chat messages are kept (one sub-folder per repo, outside the mirror).</summary>
public sealed class AttachmentsOptions
{
	public const string Section = "Attachments";

	/// <summary>Absolute after <see cref="ResolveRoot"/>; empty in configuration means the default.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>The configured root made absolute; by default <c>&lt;DataDir&gt;\attachments</c> (service) or <c>data\attachments</c> under the content root (dev, gitignored).</summary>
	public static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Attachments : Path.Combine(contentRoot, "data", "attachments");
	}
}
```

`src/AiChromeProxy.Infrastructure/Chat/FileSystemAttachmentStore.cs`:

```csharp
using AiChromeProxy.Application.Chat;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary><see cref="IAttachmentStore"/> on the file system under <see cref="AttachmentsOptions.Root"/>.</summary>
public sealed class FileSystemAttachmentStore(IOptions<AttachmentsOptions> options) : IAttachmentStore
{
	private const string PartSuffix = ".part";

	private readonly string _root = Path.GetFullPath(options.Value.Root);

	public string RepoFolder(string repo) => Under(Path.Combine(_root, repo));

	public void Begin(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		var part = Under(Path.Combine(folder, name + PartSuffix));
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}

		Directory.CreateDirectory(folder);
		File.WriteAllBytes(part, []);
	}

	public long Append(string repo, string id, string name, byte[] data)
	{
		using (var stream = new FileStream(Under(Path.Combine(Folder(repo, id), name + PartSuffix)), FileMode.Append, FileAccess.Write, FileShare.None))
		{
			stream.Write(data);
			return stream.Length;
		}
	}

	public void Commit(string repo, string id, string name)
	{
		var folder = Folder(repo, id);
		File.Move(Under(Path.Combine(folder, name + PartSuffix)), Under(Path.Combine(folder, name)));
	}

	public StoredAttachment? Find(string repo, string id)
	{
		var folder = Folder(repo, id);
		var file = Directory.Exists(folder)
			? Directory.EnumerateFiles(folder).FirstOrDefault(f => !f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
			: null;
		return file is null ? null : new StoredAttachment(file, Path.GetFileName(file), new FileInfo(file).Length);
	}

	public void Delete(string repo, string id)
	{
		var folder = Folder(repo, id);
		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	public int Sweep(DateTimeOffset olderThan, DateTimeOffset partsOlderThan)
	{
		if (!Directory.Exists(_root))
		{
			return 0;
		}

		var deleted = 0;
		foreach (var folder in Directory.EnumerateDirectories(_root).SelectMany(Directory.EnumerateDirectories).ToList())
		{
			try
			{
				var files = Directory.GetFiles(folder);
				var written = files.Select(File.GetLastWriteTimeUtc).DefaultIfEmpty(Directory.GetLastWriteTimeUtc(folder)).Max();
				var limit = files.Any(f => f.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase)) ? partsOlderThan : olderThan;
				if (written < limit.UtcDateTime)
				{
					Directory.Delete(folder, recursive: true);
					deleted++;
				}
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				// A file still open (an upload in progress, a run reading it) is tried again on the next sweep.
			}
		}

		return deleted;
	}

	private string Folder(string repo, string id) => Under(Path.Combine(_root, repo, id));

	private string Under(string path)
	{
		var full = Path.GetFullPath(path);
		if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path leaves the attachments root.", nameof(path));
		}

		return full;
	}
}
```

`src/AiChromeProxy.Infrastructure/DependencyInjection.cs` — after the `IChatStore` line:

```csharp
		services.Configure<AttachmentsOptions>(configuration.GetSection(AttachmentsOptions.Section));
		services.AddSingleton<IAttachmentStore, FileSystemAttachmentStore>();
```

and extend the summary: "the chat attachments store (section <c>Attachments</c>; the host resolves an empty <c>Root</c> with <see cref="AttachmentsOptions.ResolveRoot"/>)".

`src/AiChromeProxy.Server/Program.cs` — after line 41 (`PostConfigure<MirrorOptions>`):

```csharp
builder.Services.PostConfigure<AttachmentsOptions>(o => o.Root = AttachmentsOptions.ResolveRoot(o.Root, dataDir, builder.Environment.ContentRootPath));
```

(add `using AiChromeProxy.Infrastructure.Chat;` if missing).

- [ ] **Step 4: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*FileSystemAttachmentStoreTests" --filter-class "*LayerDependencyTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests/AiChromeProxy.Tests/Infrastructure/FileSystemAttachmentStoreTests.cs
git commit -m "Chat attachments: file-system store under Attachments:Root"
```

---

### Task 3: Server uploads (`chat.attach`)

**Files:**
- Create: `src/AiChromeProxy.Application/Chat/AttachmentNames.cs`, `src/AiChromeProxy.Application/Chat/ChatAttachments.cs`, `src/AiChromeProxy.Application/Chat/ChatAttachHandler.cs`
- Modify: `src/AiChromeProxy.Application/DependencyInjection.cs`
- Test: `tests/AiChromeProxy.Tests/Application/AttachmentNamesTests.cs`, `tests/AiChromeProxy.Tests/Application/ChatAttachmentsTests.cs`

**Interfaces:**
- Consumes: `IAttachmentStore`, `StoredAttachment`, `FileSystemAttachmentStore`, `AttachmentsOptions` (Task 2); `ChatAttachPayload`, `ChatAttachedPayload`, `ChatLimits.*`, `MessageTypes.ChatAttach/ChatAttached` (Task 1); existing `RepoName.Sanitize`, `SyncData.Decode/Encode`, `EnvelopeException`, `ErrorCodes`.
- Produces: `AttachmentNames.Sanitize(string? name) : string`, `AttachmentNames.MaxChars = 100`; `ChatAttachments(IAttachmentStore store, TimeProvider time)` with `ChatAttachedPayload? Append(ChatAttachPayload payload)`, `IReadOnlyList<StoredAttachment> Resolve(string repo, IReadOnlyList<string> ids)`, `string Folder(string repo)`, constants `Expired` (message), `MaxUploadsPerRepo = 50`, `MaxAge` (30 days), `PartMaxAge` (1 day); `ChatAttachHandler(ChatAttachments)` (type `chat.attach`).

- [ ] **Step 1: Write the failing name tests** — `tests/AiChromeProxy.Tests/Application/AttachmentNamesTests.cs`:

```csharp
using AiChromeProxy.Application.Chat;

namespace AiChromeProxy.Tests.Application;

public sealed class AttachmentNamesTests
{
	[Theory]
	[InlineData("shot.png", "shot.png")]
	[InlineData("C:\\Users\\x\\shot.png", "shot.png")]
	[InlineData("a/b/notes.txt", "notes.txt")]
	[InlineData("bad<>:\"|?*.txt", "bad_______.txt")]
	[InlineData("tab\there.txt", "tab_here.txt")]
	[InlineData("  spaced.txt  ", "spaced.txt")]
	[InlineData("dots...", "dots")]
	[InlineData("..", "file")]
	[InlineData("", "file")]
	[InlineData(null, "file")]
	[InlineData("CON", "_CON")]
	[InlineData("nul.txt", "_nul.txt")]
	[InlineData("com1.tar.gz", "_com1.tar.gz")]
	[InlineData("console.txt", "console.txt")]
	[InlineData("x.part", "x.part_")]
	public void Sanitize(string? name, string expected) => Assert.Equal(expected, AttachmentNames.Sanitize(name));

	[Fact]
	public void Sanitize_CutsLongNames_KeepingTheExtension()
	{
		var name = AttachmentNames.Sanitize(new string('a', 300) + ".png");

		Assert.Equal(AttachmentNames.MaxChars, name.Length);
		Assert.EndsWith("a.png", name);
	}

	[Fact]
	public void Sanitize_NeverCutsASurrogatePair()
	{
		var name = AttachmentNames.Sanitize(new string('a', 95) + "😀😀😀😀😀😀" + ".txt");

		Assert.True(name.Length <= AttachmentNames.MaxChars);
		Assert.False(char.IsHighSurrogate(name[^5]));
		Assert.EndsWith(".txt", name);
	}
}
```

- [ ] **Step 2: Write the failing upload tests** — `tests/AiChromeProxy.Tests/Application/ChatAttachmentsTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Chat;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Application;

public sealed class ChatAttachmentsTests : IDisposable
{
	private const string Id = "0123456789abcdef0123456789abcdef";
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	// Starts at the real time: the sweep compares it with the files' real write times.
	private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
	private readonly ChatAttachments _attachments;

	public ChatAttachmentsTests() =>
		_attachments = new ChatAttachments(new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _root })), _time);

	public void Dispose()
	{
		if (Directory.Exists(_root))
		{
			Directory.Delete(_root, recursive: true);
		}
	}

	[Fact]
	public void Chunks_InOrder_AnswerOnlyTheLast_AndTheFileIsResolvable()
	{
		Assert.Null(_attachments.Append(Chunk(0, [1, 2], size: 3, last: false)));
		var reply = _attachments.Append(Chunk(2, [3], size: 3, last: true));

		Assert.Equal(new ChatAttachedPayload("repo", Id), reply);
		var file = Assert.Single(_attachments.Resolve("repo", [Id]));
		Assert.Equal("shot.png", file.Name);
		Assert.Equal(3, file.Size);
		Assert.Equal([1, 2, 3], File.ReadAllBytes(file.Path));
		Assert.Equal(Path.Combine(_root, "repo"), _attachments.Folder("repo"));
	}

	[Fact]
	public void EmptyFile_IsOneLastChunk() => Assert.NotNull(_attachments.Append(Chunk(0, [], size: 0, last: true)));

	[Fact]
	public void TheNameIsSanitized()
	{
		_attachments.Append(Chunk(0, [1], size: 1, last: true, name: "..\\evil\\CON"));

		Assert.Equal("_CON", Assert.Single(_attachments.Resolve("repo", [Id])).Name);
	}

	[Theory]
	[InlineData("ABCDEF0123456789abcdef0123456789")]
	[InlineData("0123")]
	[InlineData("..")]
	[InlineData(null)]
	public void ABadId_IsABadRequest(string? id) =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 1, true) with { Id = id! })).Code);

	[Fact]
	public void NoRepo_IsABadRequest() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 1, true) with { Repo = " " })).Code);

	[Fact]
	public void NotBase64Url_FailsTheUpload() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [], 1, true) with { Data = "a+b/" })).Code);

	[Fact]
	public void OverTheSizeLimit_IsTooLargeUpFront()
	{
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], ChatLimits.MaxAttachmentBytes + 1, true)));

		Assert.Equal(ErrorCodes.TooLarge, error.Code);
		Assert.Equal("'shot.png' is larger than 20 MB.", error.Message);
	}

	[Fact]
	public void AChunkOverSixteenKilobytes_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, new byte[ChatLimits.AttachmentChunkSize + 1], ChatLimits.AttachmentChunkSize + 1, true))).Code);

	[Fact]
	public void MoreBytesThanTheSize_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1, 2], 1, true))).Code);

	[Fact]
	public void FewerBytesThanTheSize_FailsOnTheLastChunk() =>
		Assert.Equal(ErrorCodes.BadRequest, Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(0, [1], 2, true))).Code);

	[Fact]
	public void AFailedChunk_DropsTheRest_AndTheLastGetsTheFirstError()
	{
		_attachments.Append(Chunk(0, [1], size: 3, last: false));

		Assert.Null(_attachments.Append(Chunk(5, [2], size: 3, last: false)));
		Assert.Null(_attachments.Append(Chunk(6, [3], size: 3, last: false)));
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(7, [4], size: 3, last: true)));

		Assert.Contains("out of order", error.Message);
		Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [Id]));
	}

	[Fact]
	public void OffsetZero_StartsOver_EvenAfterAFailure()
	{
		_attachments.Append(Chunk(0, [1], size: 2, last: false));
		_attachments.Append(Chunk(9, [1], size: 2, last: false));

		_attachments.Append(Chunk(0, [7], size: 2, last: false));
		_attachments.Append(Chunk(1, [8], size: 2, last: true));

		Assert.Equal([7, 8], File.ReadAllBytes(Assert.Single(_attachments.Resolve("repo", [Id])).Path));
	}

	[Fact]
	public void AtMostFiftyUploadsInProgressPerRepo()
	{
		for (var i = 0; i < ChatAttachments.MaxUploadsPerRepo; i++)
		{
			_attachments.Append(Chunk(0, [1], 2, false) with { Id = i.ToString("x32") });
		}

		// A first chunk gets no reply: the refusal reaches the client on the upload's last chunk.
		var extra = "f".PadLeft(32, 'f');
		Assert.Null(_attachments.Append(Chunk(0, [1], 2, false) with { Id = extra }));
		var error = Assert.Throws<EnvelopeException>(() => _attachments.Append(Chunk(1, [2], 2, true) with { Id = extra }));
		Assert.Equal(ErrorCodes.TooLarge, error.Code);
		Assert.Equal("Too many uploads in progress.", error.Message);
		Assert.Null(_attachments.Append(Chunk(0, [1], 2, false) with { Repo = "other" }));
	}

	[Fact]
	public void Resolve_UnknownForeignOrBadIds_AreExpired()
	{
		_attachments.Append(Chunk(0, [1], 1, true));

		foreach (var (repo, id) in new[] { ("repo", "ffffffffffffffffffffffffffffffff"), ("other", Id), ("repo", "..") })
		{
			var error = Assert.Throws<EnvelopeException>(() => _attachments.Resolve(repo, [id]));
			Assert.Equal(ErrorCodes.BadRequest, error.Code);
			Assert.Equal(ChatAttachments.Expired, error.Message);
		}
	}

	[Fact]
	public void Resolve_MoreThanTenIds_IsTooLarge() =>
		Assert.Equal(ErrorCodes.TooLarge, Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [.. Enumerable.Range(0, 11).Select(i => i.ToString("x32"))])).Code);

	[Fact]
	public void Resolve_DuplicateIds_CountOnce()
	{
		_attachments.Append(Chunk(0, [1], 1, true));

		Assert.Single(_attachments.Resolve("repo", [Id, Id]));
	}

	[Fact]
	public void AnUploadStart_SweepsOldAttachments_AtMostOnceADay()
	{
		_attachments.Append(Chunk(0, [1], 1, true)); // the first upload of this instance sweeps (nothing to delete yet)
		var file = _attachments.Resolve("repo", [Id])[0].Path;
		File.SetLastWriteTimeUtc(file, _time.GetUtcNow().AddDays(-31).UtcDateTime);

		_time.Advance(TimeSpan.FromHours(23));
		_attachments.Append(Chunk(0, [1], 1, true) with { Id = "1".PadLeft(32, '1') });
		Assert.True(File.Exists(file)); // not a day since the last sweep

		_time.Advance(TimeSpan.FromHours(2));
		_attachments.Append(Chunk(0, [1], 1, true) with { Id = "2".PadLeft(32, '2') });
		Assert.False(File.Exists(file));
		Assert.Throws<EnvelopeException>(() => _attachments.Resolve("repo", [Id]));
	}

	[Fact]
	public async Task Handler_AnswersTheLastChunkWithItsCorrelationId_AndNothingElse()
	{
		var router = new EnvelopeRouter([new ChatAttachHandler(_attachments)]);
		var context = new EnvelopeContext("c1", (_, _) => Task.CompletedTask);

		var first = await router.RouteAsync(Envelope.Create(MessageTypes.ChatAttach, Chunk(0, [1], 2, false)), context, TestContext.Current.CancellationToken);
		var last = await router.RouteAsync(Envelope.Create(MessageTypes.ChatAttach, Chunk(1, [2], 2, true), "corr"), context, TestContext.Current.CancellationToken);
		var bad = await router.RouteAsync(new Envelope(MessageTypes.ChatAttach, JsonDocument.Parse("[]").RootElement, "x"), context, TestContext.Current.CancellationToken);

		Assert.Null(first);
		Assert.Equal(MessageTypes.ChatAttached, last!.Type);
		Assert.Equal("corr", last.CorrelationId);
		Assert.Equal(MessageTypes.Error, bad!.Type);
	}

	private static ChatAttachPayload Chunk(long offset, byte[] data, long size, bool last, string name = "shot.png") =>
		new("repo", Id, name, size, offset, SyncData.Encode(data), last);
}
```

Note for the implementer: check how `EnvelopeContext` and `Envelope` are constructed in `tests/AiChromeProxy.Tests/Application/EnvelopeRouterTests.cs` and use the same constructors in `Handler_AnswersTheLastChunkWithItsCorrelationId_AndNothingElse` (the shapes above are the expected ones: `EnvelopeContext(connectionId, push)`, `Envelope(Type, Payload, CorrelationId)`, `Envelope.Create(type, payload, correlationId)`).

- [ ] **Step 3: Run to see them fail**

Run: `dotnet build -c Release`
Expected: compile errors (`AttachmentNames`, `ChatAttachments`, `ChatAttachHandler` not found).

- [ ] **Step 4: Implement**

`src/AiChromeProxy.Application/Chat/AttachmentNames.cs`:

```csharp
using System.Buffers;

namespace AiChromeProxy.Application.Chat;

/// <summary>Makes a file name from the protocol safe to store on Windows.</summary>
public static class AttachmentNames
{
	/// <summary>Longest stored name, in UTF-16 characters.</summary>
	public const int MaxChars = 100;

	private const string PartSuffix = ".part";

	/// <summary>Longest extension kept when a name is cut.</summary>
	private const int MaxExtensionChars = 20;

	private static readonly SearchValues<char> Invalid = SearchValues.Create("<>:\"/\\|?*");

	private static readonly HashSet<string> Reserved = new(
		["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
		StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The last path segment; invalid and control characters → <c>_</c>; spaces trimmed and trailing dots dropped; reserved device names
	/// (the part before the first dot) prefixed with <c>_</c>; a name ending in <c>.part</c> gets a trailing <c>_</c> (the store's temp suffix);
	/// at most <see cref="MaxChars"/> characters, keeping the extension and never cutting a surrogate pair; <c>file</c> when nothing is left.
	/// </summary>
	public static string Sanitize(string? name)
	{
		var last = (name ?? string.Empty).Split('/', '\\')[^1];
		var clean = string.Concat(last.Select(c => char.IsControl(c) || Invalid.Contains(c) ? '_' : c)).Trim().TrimEnd('.', ' ');
		if (clean.Length == 0)
		{
			return "file";
		}

		if (Reserved.Contains(clean.Split('.')[0]))
		{
			clean = "_" + clean;
		}

		if (clean.EndsWith(PartSuffix, StringComparison.OrdinalIgnoreCase))
		{
			clean += "_";
		}

		if (clean.Length > MaxChars)
		{
			var extension = Path.GetExtension(clean);
			extension = extension.Length > MaxExtensionChars ? string.Empty : extension;
			var keep = MaxChars - extension.Length;
			if (char.IsHighSurrogate(clean[keep - 1]))
			{
				keep--;
			}

			clean = clean[..keep].TrimEnd('.', ' ') + extension;
		}

		return clean;
	}
}
```

`src/AiChromeProxy.Application/Chat/ChatAttachments.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// <c>chat.attach</c> uploads and their use by <c>chat.send</c>: checks ids, names, order and limits and keeps the state of uploads in progress
/// (lost on a restart; the sweep removes their files). When an upload starts and the last sweep is over a day old, attachments older than
/// <see cref="MaxAge"/> and unfinished uploads older than <see cref="PartMaxAge"/> are deleted (the session transcript keeps what Claude read).
/// </summary>
public sealed class ChatAttachments(IAttachmentStore store, TimeProvider time)
{
	/// <summary>Uploads one repo may have in progress at once.</summary>
	public const int MaxUploadsPerRepo = 50;

	/// <summary>What <c>chat.send</c> answers for an id that is not a finished upload of the repo.</summary>
	public const string Expired = "An attachment has expired; attach it again.";

	/// <summary>How long a finished attachment is kept.</summary>
	public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

	/// <summary>How long an unfinished upload is kept.</summary>
	public static readonly TimeSpan PartMaxAge = TimeSpan.FromDays(1);

	private static readonly TimeSpan SweepInterval = TimeSpan.FromDays(1);

	private readonly Lock _gate = new();
	private readonly Dictionary<(string Repo, string Id), Upload> _uploads = [];
	private DateTimeOffset? _swept;

	/// <summary>The repo's attachments folder, for <c>--add-dir</c>.</summary>
	public string Folder(string repo) => store.RepoFolder(repo);

	/// <summary>
	/// Stores one chunk and returns the reply to the last one (null for the others). A chunk that fails ends the upload: its later chunks are
	/// dropped and the last one gets that first error; offset 0 starts the upload over.
	/// </summary>
	public ChatAttachedPayload? Append(ChatAttachPayload payload)
	{
		var repo = RepoName.Sanitize(payload.Repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, "chat.attach needs the folder name in 'repo'.");
		if (!IsId(payload.Id))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "chat.attach needs 'id': 32 lowercase hex characters.");
		}

		var key = (repo, payload.Id);

		// ponytail: one lock for every upload (16 KB writes); per-upload locks if many tabs ever upload at once.
		lock (_gate)
		{
			if (payload.Offset != 0 && _uploads.TryGetValue(key, out var failed) && failed.Error is { } error)
			{
				if (payload.Last)
				{
					_uploads.Remove(key);
					throw error;
				}

				return null;
			}

			try
			{
				return AppendLocked(repo, key, payload);
			}
			catch (Exception ex) when (ex is EnvelopeException or IOException or UnauthorizedAccessException)
			{
				var failure = ex as EnvelopeException ?? new EnvelopeException(ErrorCodes.Internal, "Could not store the attachment.");
				store.Delete(repo, payload.Id);
				if (payload.Last)
				{
					_uploads.Remove(key);
					throw failure;
				}

				_uploads[key] = new Upload(string.Empty, 0, time.GetUtcNow()) { Error = failure };
				return null;
			}
		}
	}

	/// <summary>The finished attachments of the repo named by <paramref name="ids"/> (duplicates once), in order.</summary>
	/// <exception cref="EnvelopeException"><c>too_large</c> over <see cref="ChatLimits.MaxAttachments"/>; <c>bad_request</c> <see cref="Expired"/> for an unknown id.</exception>
	public IReadOnlyList<StoredAttachment> Resolve(string repo, IReadOnlyList<string> ids)
	{
		if (ids.Count > ChatLimits.MaxAttachments)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"At most {ChatLimits.MaxAttachments} files per message.");
		}

		return [.. ids.Distinct(StringComparer.Ordinal).Select(id => (IsId(id) ? store.Find(repo, id) : null) ?? throw new EnvelopeException(ErrorCodes.BadRequest, Expired))];
	}

	private static bool IsId(string? id) => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

	private ChatAttachedPayload? AppendLocked(string repo, (string Repo, string Id) key, ChatAttachPayload payload)
	{
		byte[] data;
		try
		{
			data = SyncData.Decode(payload.Data ?? string.Empty);
		}
		catch (FormatException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The chunk is not base64url.");
		}

		if (data.Length > ChatLimits.AttachmentChunkSize)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"Chunks carry at most {ChatLimits.AttachmentChunkSize} bytes.");
		}

		if (payload.Offset == 0)
		{
			var name = AttachmentNames.Sanitize(payload.Name);
			if (payload.Size < 0)
			{
				throw new EnvelopeException(ErrorCodes.BadRequest, "chat.attach needs a 'size' of 0 or more.");
			}

			if (payload.Size > ChatLimits.MaxAttachmentBytes)
			{
				throw new EnvelopeException(ErrorCodes.TooLarge, $"'{name}' is larger than 20 MB.");
			}

			if (_uploads.Count(u => u.Key.Repo == repo && u.Key != key && u.Value.Error is null) >= MaxUploadsPerRepo)
			{
				throw new EnvelopeException(ErrorCodes.TooLarge, "Too many uploads in progress.");
			}

			SweepIfDue();
			store.Begin(repo, payload.Id, name);
			_uploads[key] = new Upload(name, payload.Size, time.GetUtcNow());
		}
		else if (!_uploads.TryGetValue(key, out var started) || started.Received != payload.Offset)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"The chunk at offset {payload.Offset} is out of order.");
		}

		var upload = _uploads[key];
		if (upload.Received + data.Length > upload.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{upload.Name}' is longer than its size {upload.Size}.");
		}

		upload.Received = store.Append(repo, payload.Id, upload.Name, data);
		if (!payload.Last)
		{
			return null;
		}

		if (upload.Received != upload.Size)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"'{upload.Name}' arrived shorter than its size {upload.Size}.");
		}

		store.Commit(repo, payload.Id, upload.Name);
		_uploads.Remove(key);
		return new ChatAttachedPayload(repo, payload.Id);
	}

	private void SweepIfDue()
	{
		var now = time.GetUtcNow();
		if (_swept is { } at && now - at < SweepInterval)
		{
			return;
		}

		_swept = now;
		store.Sweep(now - MaxAge, now - PartMaxAge);
		foreach (var stale in _uploads.Where(u => u.Value.Started < now - PartMaxAge).Select(u => u.Key).ToList())
		{
			_uploads.Remove(stale);
		}
	}

	private sealed class Upload(string name, long size, DateTimeOffset started)
	{
		public string Name { get; } = name;

		public long Size { get; } = size;

		public DateTimeOffset Started { get; } = started;

		public long Received { get; set; }

		public EnvelopeException? Error { get; init; }
	}
}
```

Note: `RepoName` lives where `ChatService` gets it (`AiChromeProxy.Domain.Sync` — check `ChatService.RepoOf`'s usings and copy them).

`src/AiChromeProxy.Application/Chat/ChatAttachHandler.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Routes <c>chat.attach</c> to <see cref="ChatAttachments"/>; only the last chunk of an upload gets a reply.</summary>
public sealed class ChatAttachHandler(ChatAttachments attachments) : IEnvelopeHandler
{
	public string Type => MessageTypes.ChatAttach;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		ChatAttachPayload payload;
		try
		{
			payload = request.Payload.Deserialize<ChatAttachPayload>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, "chat.attach needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "Invalid chat.attach payload.");
		}

		var reply = attachments.Append(payload);
		return Task.FromResult(reply is null ? null : Envelope.Create(MessageTypes.ChatAttached, reply, request.CorrelationId));
	}
}
```

`src/AiChromeProxy.Application/DependencyInjection.cs` — after `services.AddSingleton<PermissionBroker>();`:

```csharp
		services.AddSingleton<ChatAttachments>();
		services.AddSingleton<IEnvelopeHandler, ChatAttachHandler>();
```

and add `an <see cref="IAttachmentStore"/>` to the summary's list of Infrastructure needs.

- [ ] **Step 5: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*AttachmentNamesTests" --filter-class "*ChatAttachmentsTests" --filter-class "*ChatHubTests"`
Expected: PASS (`ChatHubTests` proves the Server's DI still resolves).

- [ ] **Step 6: Commit**

```bash
git add src/AiChromeProxy.Application tests/AiChromeProxy.Tests/Application/AttachmentNamesTests.cs tests/AiChromeProxy.Tests/Application/ChatAttachmentsTests.cs
git commit -m "Chat attachments: chat.attach uploads with limits, names and a daily sweep"
```

---

### Task 4: Send with attachments (`ChatService`, `AgentRun`, `--add-dir`)

**Files:**
- Modify: `src/AiChromeProxy.Application/Chat/AgentRun.cs`, `src/AiChromeProxy.Application/Chat/ChatService.cs`, `src/AiChromeProxy.Infrastructure/Chat/ClaudeArguments.cs`
- Test: `tests/AiChromeProxy.Tests/Application/ChatServiceTests.cs`, `tests/AiChromeProxy.Tests/Infrastructure/ClaudeArgumentsTests.cs`

**Interfaces:**
- Consumes: `ChatAttachments.Resolve/Folder`, `StoredAttachment` (Task 3); `ChatSendPayload.Attachments`, `ChatEvent.Attachments`, `ChatAttachment` (Task 1).
- Produces: `AgentRun(..., IReadOnlyList<string>? AddDirs = null)`; `ChatService(..., ChatAttachments? attachments = null)` (last constructor parameter); `ClaudeArguments.Build` emits `--add-dir <dirs>` right after the `--allowedTools` list and before `--append-system-prompt`; `ChatService.AttachedFilesHeader = "Attached files (read them with the Read tool):"`.

- [ ] **Step 1: Write the failing argument test** — append to `ClaudeArgumentsTests` (use the file's existing `Run(...)` helper; it builds an `AgentRun` — add `AddDirs` with `with`):

```csharp
	[Fact]
	public void AddDirs_ComeAfterTheAllowedToolsAndBeforeTheSystemPrompt()
	{
		var run = Run("all", null, null) with { AllowedTools = ["Bash(ls)"], AddDirs = ["C:\\data\\attachments\\repo", " ", "-x"] };

		var args = ClaudeArguments.Build(run, new AgentOptions());

		var at = args.ToList().IndexOf("--add-dir");
		Assert.Equal(["--allowedTools", "Bash(ls)", "--add-dir", "C:\\data\\attachments\\repo", "--append-system-prompt"], args.Skip(at - 2).Take(5));
	}

	[Fact]
	public void NoAddDirs_NoFlag() => Assert.DoesNotContain("--add-dir", ClaudeArguments.Build(Run("all", null, null), new AgentOptions()));
```

(If `Run` has another signature, adapt the call; `Run("all", null, null)` stands for "a run in mode all without approval endpoint".)

- [ ] **Step 2: Write the failing service tests** — in `ChatServiceTests`:

Add fields and wire the service (constructor):

```csharp
	private readonly string _attachRoot = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly ChatAttachments _attachments;
```

```csharp
		_attachments = new ChatAttachments(new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = _attachRoot })), _time);
		_service = new ChatService(_store, mirror, _projects, _runner, _time, _logger, _broker, _endpoint, attachments: _attachments);
```

In `Dispose` add:

```csharp
		if (Directory.Exists(_attachRoot))
		{
			Directory.Delete(_attachRoot, recursive: true);
		}
```

Add `using AiChromeProxy.Infrastructure.Chat;`. Tests:

```csharp
	[Fact]
	public async Task Send_WithAttachments_GivesClaudeTheirPaths_AndTheFolder_AndStoresNamesOnThePrompt()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);
		var shot = Attach("0123456789abcdef0123456789abcdef", "shot.png", [1, 2, 3]);
		var notes = Attach("fedcba9876543210fedcba9876543210", "notes.txt", [4]);

		await RouteAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "Look at these", [shot.Id, notes.Id]));
		var process = await _runner.NextAsync();

		Assert.Equal($"Look at these\n\n{ChatService.AttachedFilesHeader}\n- {shot.Path}\n- {notes.Path}", process.Run.Prompt);
		Assert.Equal([Path.Combine(_attachRoot, Repo)], process.Run.AddDirs);
		var prompt = await client.WaitAsync(e => e.Kind == ChatEventKinds.Prompt);
		Assert.Equal("Look at these", prompt.Text);
		Assert.Equal([new ChatAttachment("shot.png", 3), new ChatAttachment("notes.txt", 1)], prompt.Attachments);
	}

	[Fact]
	public async Task Send_OnlyAttachments_IsAccepted_AndTheTitleIsTheFirstName()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);
		var shot = Attach("0123456789abcdef0123456789abcdef", "shot.png", [1]);

		await RouteAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, " ", [shot.Id]));
		var process = await _runner.NextAsync();

		Assert.Equal($"{ChatService.AttachedFilesHeader}\n- {shot.Path}", process.Run.Prompt);
		Assert.Equal("shot.png", Assert.Single(await SessionsAsync(client, Repo)).Title);
	}

	[Fact]
	public async Task Send_WithoutAttachments_RunHasNoAddDirs()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);

		await SendAsync(client, Repo, null, "hi");

		Assert.Null((await _runner.NextAsync()).Run.AddDirs);
	}

	[Fact]
	public async Task Send_UnknownOrForeignAttachment_IsRefused_AndNoSessionIsCreated()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);
		var foreign = _attachments.Append(new ChatAttachPayload("other", "0123456789abcdef0123456789abcdef", "a.txt", 1, 0, SyncData.Encode([1]), true))!;

		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "hi", [foreign.Id])));
		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "hi", ["ffffffffffffffffffffffffffffffff"])));
		Assert.Empty(await SessionsAsync(client, Repo));
	}

	[Fact]
	public async Task Send_NoTextAndNoAttachments_IsABadRequest()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);

		Assert.Equal(ErrorCodes.BadRequest, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, " ", [])));
	}

	[Fact]
	public async Task Send_ElevenAttachments_IsTooLarge()
	{
		var client = new Client("c1");
		await OpenAsync(client, Repo);

		Assert.Equal(ErrorCodes.TooLarge, await ErrorAsync(client, MessageTypes.ChatSend, new ChatSendPayload(Repo, null, "hi", [.. Enumerable.Range(0, 11).Select(i => i.ToString("x32"))])));
	}

	private (string Id, string Path) Attach(string id, string name, byte[] data)
	{
		_attachments.Append(new ChatAttachPayload(Repo, id, name, data.Length, 0, SyncData.Encode(data), true));
		return (id, _attachments.Resolve(Repo, [id])[0].Path);
	}
```

(`client.WaitAsync(predicate)` returns the matching `ChatEvent` — check the `Client` test helper's signature at the end of the file and adapt.)

- [ ] **Step 3: Run to see them fail**

Run: `dotnet build -c Release`
Expected: compile errors (`AgentRun.AddDirs`, `ChatService.AttachedFilesHeader`, `attachments:` parameter).

- [ ] **Step 4: Implement**

`AgentRun.cs` — document and add the parameter after `ApprovedMcpServers`:

```csharp
/// <param name="AddDirs">Extra folders the agent may read without asking (the repo's attachments folder when the message has attachments).</param>
```

```csharp
	IReadOnlyList<string>? ApprovedMcpServers = null,
	IReadOnlyList<string>? AddDirs = null)
```

and in `PrintMembers` change the second `Append` to end with `ApprovedMcpServers = {ApprovedMcpServers}, AddDirs = {AddDirs}, ");`.

`ClaudeArguments.Build` — after the `--allowedTools` block, before `args.AddRange(["--append-system-prompt", Convention]);`:

```csharp
		// Variadic as well; --append-system-prompt ends its list too.
		var dirs = (run.AddDirs ?? []).Where(d => !string.IsNullOrWhiteSpace(d) && !d.StartsWith('-')).ToList();
		if (dirs.Count > 0)
		{
			args.Add("--add-dir");
			args.AddRange(dirs);
		}
```

and in the `Build` summary: "`--allowedTools` and `--add-dir` are variadic, so they come right before `--append-system-prompt`, which ends their list".

`ChatService.cs`:

1. Constant and field:

```csharp
	/// <summary>Starts the list of attached files' paths in the text given to Claude.</summary>
	public const string AttachedFilesHeader = "Attached files (read them with the Read tool):";
```

```csharp
	private readonly ChatAttachments? _attachments;
```

2. Constructor — add a last parameter `ChatAttachments? attachments = null` and `_attachments = attachments;`.

3. In `Send` replace the opening checks (from `var text = payload.Text;` up to the closing brace of the too-large check) with:

```csharp
		var repo = RepoOf(payload.Repo, MessageTypes.ChatSend);
		var text = payload.Text ?? string.Empty;
		var ids = payload.Attachments ?? [];
		if (string.IsNullOrWhiteSpace(text) && ids.Count == 0)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "chat.send needs the message in 'text' or at least one attachment.");
		}

		// Characters, then bytes once escaped: what the client checks before sending (the hub could not receive more anyway).
		if (text.Length > ChatLimits.MaxTextChars
			|| JsonSerializer.SerializeToUtf8Bytes(new ChatEvent(string.Empty, string.Empty, 0, ChatEventKinds.Prompt, Text: text), JsonSerializerOptions.Web).Length > ChatLimits.MaxSendBytes)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"A message may have at most {ChatLimits.MaxTextChars} characters and {ChatLimits.MaxSendBytes} bytes once encoded.");
		}
```

(keep the existing `var repo = ...` line only once). Right after `var folder = _mirror.RepoFolder(repo) ?? ...;` add:

```csharp
		IReadOnlyList<StoredAttachment> files = ids.Count == 0 ? [] : (_attachments ?? throw new EnvelopeException(ErrorCodes.BadRequest, ChatAttachments.Expired)).Resolve(repo, ids);
```

4. Session title: `_store.CreateSession(repo, Title(string.IsNullOrWhiteSpace(text) ? files[0].Name : text)).Id`.

5. `new AgentRun(folder, text, ...)` → `new AgentRun(folder, AgentPrompt(text, files), ...` and add as the last argument `files.Count == 0 ? null : [_attachments!.Folder(repo)]`.

6. The stored prompt:

```csharp
			Store(run, [.. Parts(run, new ChatEvent(run.SessionId, run.Id, 0, ChatEventKinds.Prompt, Text: text, Attachments: files.Count == 0 ? null : [.. files.Select(f => new ChatAttachment(f.Name, f.Size))]))]);
```

7. New helper next to `Title`:

```csharp
	/// <summary>The text given to Claude: the message, then a line per attached file with its absolute path.</summary>
	private static string AgentPrompt(string text, IReadOnlyList<StoredAttachment> files)
	{
		if (files.Count == 0)
		{
			return text;
		}

		var list = AttachedFilesHeader + string.Concat(files.Select(f => "\n- " + f.Path));
		return string.IsNullOrWhiteSpace(text) ? list : text + "\n\n" + list;
	}
```

8. Update the `Send` summary: "with attachments (ids of finished <c>chat.attach</c> uploads) Claude gets their paths after the text and may read the repo's attachments folder; the stored <c>prompt</c> lists their names and sizes".

- [ ] **Step 5: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*ChatServiceTests" --filter-class "*ClaudeArgumentsTests" --filter-class "*ClaudeRunnerTests"`
Expected: PASS. An existing test asserting the old "chat.send needs the message in 'text'." text (none found while planning) would need the new text.

- [ ] **Step 6: Commit**

```bash
git add src tests/AiChromeProxy.Tests/Application/ChatServiceTests.cs tests/AiChromeProxy.Tests/Infrastructure/ClaudeArgumentsTests.cs
git commit -m "Chat attachments: chat.send passes the files' paths and --add-dir to Claude"
```

---

### Task 5: Client upload logic and history

**Files:**
- Create: `src/AiChromeProxy.Client/Chat/IAttachmentSource.cs`, `src/AiChromeProxy.Client/Chat/AttachmentFile.cs`, `src/AiChromeProxy.Client/Chat/PendingAttachment.cs`, `src/AiChromeProxy.Client/Chat/ChatAttachments.cs`
- Modify: `src/AiChromeProxy.Client/Chat/ChatEngine.cs`, `src/AiChromeProxy.Client/Chat/ChatSession.cs`, `src/AiChromeProxy.Client/Chat/ChatItem.cs`, `tests/AiChromeProxy.Tests/Client/LoopbackServer.cs`
- Test: `tests/AiChromeProxy.Tests/Client/ChatAttachmentsTests.cs`, `tests/AiChromeProxy.Tests/Client/ChatEngineTests.cs`

**Interfaces:**
- Consumes: Tasks 1–4 on the server side (through `LoopbackServer`).
- Produces:
  - `interface IAttachmentSource { Task<byte[]> ReadChunkAsync(string handle, long offset, int length); Task ForgetAsync(string handle); }`
  - `record AttachmentFile(string Handle, string Name, long Size, string Type, string? Preview)`
  - `enum AttachmentState { Uploading, Ready, Failed }`, `record PendingAttachment(AttachmentFile File, AttachmentState State, long Sent = 0, string? Id = null, string? Error = null)`
  - `ChatAttachments(ITransport transport, IAttachmentSource source)`: `event Action? Changed`, `IReadOnlyList<PendingAttachment> Items`, `string? Note`, `bool CanSend`, `IReadOnlyList<string> ReadyIds`, `Task AddAsync(string? repo, IReadOnlyList<AttachmentFile> files)`, `Task RemoveAsync(string handle)`, `Task ClearAsync()`
  - `ChatEngine.Repo` (string?), `ChatEngine.SendAsync(string text, IReadOnlyList<string>? attachments = null) : Task<bool>` (true when the run started)
  - `ChatItem(..., IReadOnlyList<ChatAttachment>? Attachments = null)`

- [ ] **Step 1: Wire attachments into `LoopbackServer`**

Add property and construction (before `Chat = new ChatService(...)`):

```csharp
	public string AttachmentsRoot { get; } = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));

	/// <summary>The server's attachment uploads.</summary>
	public ChatAttachments Attachments { get; }
```

```csharp
		Attachments = new ChatAttachments(new FileSystemAttachmentStore(Options.Create(new AttachmentsOptions { Root = AttachmentsRoot })), TimeProvider.System);
		Chat = new ChatService(Chats, store, Projects, Agent, TimeProvider.System, new ListLogger<ChatService>(), Broker, new ApprovalEndpoint(), attachments: Attachments);
```

Router list: add `new ChatAttachHandler(Attachments),` after the `ChatHandler` entries. `Dispose`: delete `AttachmentsRoot` like `MirrorRoot`. Add `using AiChromeProxy.Infrastructure.Chat;`.

- [ ] **Step 2: Write the failing client tests** — `tests/AiChromeProxy.Tests/Client/ChatAttachmentsTests.cs`:

```csharp
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Tests.Client;

/// <summary>The chat box's uploads against <see cref="LoopbackServer"/> (the real <c>chat.attach</c> handling).</summary>
public sealed class ChatAttachmentsTests : IDisposable
{
	private readonly LoopbackServer _server = new();
	private readonly Source _source = new();
	private readonly ChatAttachments _attachments;

	public ChatAttachmentsTests() => _attachments = new ChatAttachments(_server.Transport, _source);

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task Add_UploadsInSixteenKilobyteChunks_ThenTheIdIsReady()
	{
		var bytes = Enumerable.Range(0, 40_000).Select(i => (byte)i).ToArray();
		_source.Files["h1"] = bytes;

		await _attachments.AddAsync("repo", [File("h1", "big.bin", bytes.Length)]);

		var item = Assert.Single(_attachments.Items);
		Assert.Equal(AttachmentState.Ready, item.State);
		Assert.Equal(bytes.Length, item.Sent);
		Assert.Equal(3, _server.Transport.Sent.Count(e => e.Type == MessageTypes.ChatAttach));
		Assert.Equal([item.Id!], _attachments.ReadyIds);
		Assert.True(_attachments.CanSend);
		Assert.Equal(bytes, System.IO.File.ReadAllBytes(_server.Attachments.Resolve("repo", [item.Id!])[0].Path));
	}

	[Fact]
	public async Task Add_AnEmptyFile_IsOneChunk()
	{
		_source.Files["h1"] = [];

		await _attachments.AddAsync("repo", [File("h1", "empty.txt", 0)]);

		Assert.Equal(AttachmentState.Ready, Assert.Single(_attachments.Items).State);
	}

	[Fact]
	public async Task Add_OverTwentyMegabytes_IsLeftOut_WithANote_AndForgotten()
	{
		await _attachments.AddAsync("repo", [File("h1", "huge.iso", ChatLimits.MaxAttachmentBytes + 1)]);

		Assert.Empty(_attachments.Items);
		Assert.Equal("'huge.iso' is larger than 20 MB.", _attachments.Note);
		Assert.Equal(["h1"], _source.Forgotten);
	}

	[Fact]
	public async Task Add_TheEleventhFile_IsLeftOut()
	{
		foreach (var i in Enumerable.Range(0, 11))
		{
			_source.Files[$"h{i}"] = [1];
		}

		await _attachments.AddAsync("repo", [.. Enumerable.Range(0, 11).Select(i => File($"h{i}", $"f{i}.txt", 1))]);

		Assert.Equal(10, _attachments.Items.Count);
		Assert.Equal("At most 10 files per message.", _attachments.Note);
	}

	[Fact]
	public async Task Add_WithoutAFolder_SaysSo()
	{
		await _attachments.AddAsync(null, [File("h1", "a.txt", 1)]);

		Assert.Empty(_attachments.Items);
		Assert.Equal("Open a folder first.", _attachments.Note);
	}

	[Fact]
	public async Task AServerRefusal_TurnsTheChipRed_AndBlocksSending()
	{
		_source.Files["h1"] = [1];
		_server.Transport.Reply = e => Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(ErrorCodes.TooLarge, "Too many uploads in progress."), e.CorrelationId));

		await _attachments.AddAsync("repo", [File("h1", "a.txt", 1)]);

		var item = Assert.Single(_attachments.Items);
		Assert.Equal(AttachmentState.Failed, item.State);
		Assert.Equal("Too many uploads in progress.", item.Error);
		Assert.False(_attachments.CanSend);
	}

	[Fact]
	public async Task AFileThatChanged_Fails()
	{
		_source.Files["h1"] = [1];

		await _attachments.AddAsync("repo", [File("h1", "a.txt", 5)]);

		Assert.Equal("The file changed while it was uploaded; attach it again.", Assert.Single(_attachments.Items).Error);
	}

	[Fact]
	public async Task Remove_DropsTheChip_AndForgetsTheFile()
	{
		_source.Files["h1"] = [1];
		await _attachments.AddAsync("repo", [File("h1", "a.txt", 1)]);

		await _attachments.RemoveAsync("h1");

		Assert.Empty(_attachments.Items);
		Assert.Equal(["h1"], _source.Forgotten);
	}

	[Fact]
	public async Task Remove_WhileUploading_StopsTheUpload()
	{
		_source.Files["h1"] = new byte[40_000];
		_source.Gate = new TaskCompletionSource();
		var adding = _attachments.AddAsync("repo", [File("h1", "a.bin", 40_000)]);

		await _attachments.RemoveAsync("h1");
		_source.Gate.SetResult();
		await adding;

		Assert.Empty(_attachments.Items);
		Assert.DoesNotContain(_server.Transport.Sent, e => e.Type == MessageTypes.ChatAttach);
	}

	[Fact]
	public async Task Clear_ForgetsEverything()
	{
		_source.Files["h1"] = [1];
		_source.Files["h2"] = [2];
		await _attachments.AddAsync("repo", [File("h1", "a.txt", 1), File("h2", "b.txt", 1)]);

		await _attachments.ClearAsync();

		Assert.Empty(_attachments.Items);
		Assert.Equal(["h1", "h2"], _source.Forgotten);
		Assert.True(_attachments.CanSend);
	}

	[Fact]
	public async Task Changed_IsRaised()
	{
		var raised = 0;
		_attachments.Changed += () => raised++;
		_source.Files["h1"] = [1];

		await _attachments.AddAsync("repo", [File("h1", "a.txt", 1)]);

		Assert.True(raised >= 2);
	}

	private static AttachmentFile File(string handle, string name, long size) => new(handle, name, size, "application/octet-stream", null);

	private sealed class Source : IAttachmentSource
	{
		public Dictionary<string, byte[]> Files { get; } = [];

		public List<string> Forgotten { get; } = [];

		/// <summary>When set, reads wait for it.</summary>
		public TaskCompletionSource? Gate { get; set; }

		public async Task<byte[]> ReadChunkAsync(string handle, long offset, int length)
		{
			if (Gate is not null)
			{
				await Gate.Task;
			}

			var bytes = Files[handle];
			return bytes.Skip((int)offset).Take(length).ToArray();
		}

		public Task ForgetAsync(string handle)
		{
			Forgotten.Add(handle);
			return Task.CompletedTask;
		}
	}
}
```

Append to `ChatEngineTests` (use the file's existing helpers for opening a synced folder and waiting; the names below — `OpenFolderAsync`, `_server.Agent.NextAsync()`, `WaitUntilAsync` — stand for whatever the file already uses in its send tests, e.g. the setup of `SendAsync`-based tests):

```csharp
	[Fact]
	public async Task Send_WithAttachments_StartsTheRun_AndTheUserItemListsThem()
	{
		await OpenFolderAsync();
		var repo = _chat.Repo!;
		_server.Attachments.Append(new ChatAttachPayload(repo, "0123456789abcdef0123456789abcdef", "shot.png", 1, 0, "AQ", true));

		Assert.True(await _chat.SendAsync("look", ["0123456789abcdef0123456789abcdef"]));
		var process = await _server.Agent.NextAsync();

		Assert.Contains("shot.png", process.Run.Prompt);
		await WaitUntilAsync(() => _chat.Current.Items.Any(i => i.Kind == ChatItemKind.User));
		var user = _chat.Current.Items.First(i => i.Kind == ChatItemKind.User);
		Assert.Equal("look", user.Text);
		Assert.Equal([new ChatAttachment("shot.png", 1)], user.Attachments);
	}

	[Fact]
	public async Task Send_OnlyAttachments_TitlesTheSessionWithTheFirstName()
	{
		await OpenFolderAsync();
		var repo = _chat.Repo!;
		_server.Attachments.Append(new ChatAttachPayload(repo, "0123456789abcdef0123456789abcdef", "shot.png", 1, 0, "AQ", true));

		Assert.True(await _chat.SendAsync(string.Empty, ["0123456789abcdef0123456789abcdef"]));

		await WaitUntilAsync(() => _chat.Current.Title == "shot.png");
	}

	[Fact]
	public async Task Send_AnExpiredAttachment_ReturnsFalse_WithTheServersMessage()
	{
		await OpenFolderAsync();

		Assert.False(await _chat.SendAsync("look", ["ffffffffffffffffffffffffffffffff"]));

		Assert.Contains(_chat.Current.Items, i => i.Kind == ChatItemKind.Error && i.Text == "An attachment has expired; attach it again.");
	}
```

- [ ] **Step 3: Run to see them fail**

Run: `dotnet build -c Release`
Expected: compile errors (`IAttachmentSource`, `ChatAttachments` (client), `ChatEngine.Repo`, `ChatItem.Attachments`).

- [ ] **Step 4: Implement**

`IAttachmentSource.cs`:

```csharp
namespace AiChromeProxy.Client.Chat;

/// <summary>Files pasted, dropped or picked into the chat box, kept by the browser under a handle (<c>Scripts/chat.ts</c>).</summary>
public interface IAttachmentSource
{
	/// <summary>Up to <paramref name="length"/> bytes of the file from <paramref name="offset"/>; fewer when the file is shorter, none when it is forgotten.</summary>
	Task<byte[]> ReadChunkAsync(string handle, long offset, int length);

	/// <summary>Lets the browser drop the file and its preview.</summary>
	Task ForgetAsync(string handle);
}
```

`AttachmentFile.cs`:

```csharp
namespace AiChromeProxy.Client.Chat;

/// <summary>A file the browser reported for the chat box.</summary>
/// <param name="Handle">The browser's key for it (<see cref="IAttachmentSource"/>).</param>
/// <param name="Name">Its name (pasted pictures are named by the time).</param>
/// <param name="Size">Its length in bytes.</param>
/// <param name="Type">Its media type, or empty.</param>
/// <param name="Preview">An object URL of the picture for <c>image/*</c>, else null.</param>
public sealed record AttachmentFile(string Handle, string Name, long Size, string Type, string? Preview);
```

`PendingAttachment.cs`:

```csharp
namespace AiChromeProxy.Client.Chat;

/// <summary>Where a file of the chat box stands.</summary>
public enum AttachmentState
{
	/// <summary>Being sent to the server.</summary>
	Uploading,

	/// <summary>On the server; the message can name <see cref="PendingAttachment.Id"/>.</summary>
	Ready,

	/// <summary>The upload failed (<see cref="PendingAttachment.Error"/>); remove it and attach it again.</summary>
	Failed,
}

/// <summary>A file waiting in the chat box.</summary>
public sealed record PendingAttachment(AttachmentFile File, AttachmentState State, long Sent = 0, string? Id = null, string? Error = null);
```

`ChatAttachments.cs` (client):

```csharp
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Chat;

/// <summary>
/// The files waiting in the chat box: checks the limits, uploads them one at a time with <c>chat.attach</c> as soon as they are added
/// (16 KB chunks; only the last one waits for its reply) and hands the ids of the uploaded ones to the message.
/// </summary>
public sealed class ChatAttachments(ITransport transport, IAttachmentSource source)
{
	/// <summary>Progress is shown every this many chunks (256 KB), not on every chunk.</summary>
	private const int ProgressEvery = 16;

	private readonly Lock _gate = new();
	private readonly List<PendingAttachment> _items = [];

	// No timer inside: nothing to free when one is dropped.
	private readonly Dictionary<string, CancellationTokenSource> _cancels = new(StringComparer.Ordinal);
	private readonly SemaphoreSlim _uploading = new(1, 1);
	private string? _note;

	/// <summary>Raised after every visible change.</summary>
	public event Action? Changed;

	/// <summary>Gets the files in the order they were added.</summary>
	public IReadOnlyList<PendingAttachment> Items
	{
		get
		{
			lock (_gate)
			{
				return [.. _items];
			}
		}
	}

	/// <summary>Gets why the last files added were left out, or null.</summary>
	public string? Note
	{
		get
		{
			lock (_gate)
			{
				return _note;
			}
		}
	}

	/// <summary>Gets a value indicating whether every file is uploaded (also true with none): the message may go out.</summary>
	public bool CanSend => Items.All(i => i.State == AttachmentState.Ready);

	/// <summary>Gets the server ids of the uploaded files, in order.</summary>
	public IReadOnlyList<string> ReadyIds => [.. Items.Where(i => i.State == AttachmentState.Ready).Select(i => i.Id!)];

	/// <summary>Adds the files (those over the limits are left out, forgotten, and <see cref="Note"/> says why) and uploads them to <paramref name="repo"/>.</summary>
	/// <returns>A task that completes when the files added are uploaded or failed.</returns>
	public async Task AddAsync(string? repo, IReadOnlyList<AttachmentFile> files)
	{
		var accepted = new List<AttachmentFile>();
		var rejected = new List<string>();
		lock (_gate)
		{
			_note = null;
			foreach (var file in files)
			{
				var why = repo is null ? "Open a folder first."
					: file.Size > ChatLimits.MaxAttachmentBytes ? $"'{file.Name}' is larger than 20 MB."
					: _items.Count >= ChatLimits.MaxAttachments ? $"At most {ChatLimits.MaxAttachments} files per message."
					: null;
				if (why is not null)
				{
					_note = why;
					rejected.Add(file.Handle);
					continue;
				}

				_items.Add(new PendingAttachment(file, AttachmentState.Uploading));
				_cancels[file.Handle] = new CancellationTokenSource();
				accepted.Add(file);
			}
		}

		foreach (var handle in rejected)
		{
			await source.ForgetAsync(handle);
		}

		Changed?.Invoke();
		foreach (var file in accepted)
		{
			await UploadAsync(repo!, file);
		}
	}

	/// <summary>Removes a file (stopping its upload) and lets the browser forget it.</summary>
	public async Task RemoveAsync(string handle)
	{
		lock (_gate)
		{
			_items.RemoveAll(i => i.File.Handle == handle);
			if (_cancels.Remove(handle, out var cancel))
			{
				cancel.Cancel();
			}

			_note = null;
		}

		await source.ForgetAsync(handle);
		Changed?.Invoke();
	}

	/// <summary>Empties the box after the message went out.</summary>
	public async Task ClearAsync()
	{
		foreach (var item in Items)
		{
			await RemoveAsync(item.File.Handle);
		}
	}

	private async Task UploadAsync(string repo, AttachmentFile file)
	{
		await _uploading.WaitAsync();
		try
		{
			CancellationToken ct;
			lock (_gate)
			{
				if (!_cancels.TryGetValue(file.Handle, out var cancel))
				{
					return; // removed while it waited
				}

				ct = cancel.Token;
			}

			var id = Guid.NewGuid().ToString("N");
			for (long offset = 0, chunk = 0; ; offset += ChatLimits.AttachmentChunkSize, chunk++)
			{
				var length = (int)Math.Min(ChatLimits.AttachmentChunkSize, file.Size - offset);
				var data = length == 0 ? [] : await source.ReadChunkAsync(file.Handle, offset, length);
				ct.ThrowIfCancellationRequested();
				if (data.Length != length)
				{
					throw new IOException("The file changed while it was uploaded; attach it again.");
				}

				var last = offset + length == file.Size;
				var envelope = Envelope.Create(MessageTypes.ChatAttach, new ChatAttachPayload(repo, id, file.Name, file.Size, offset, SyncData.Encode(data), last));
				if (last)
				{
					await transport.RequestAsync(envelope, ChatEngine.RequestTimeout, ct);
					Update(file.Handle, p => p with { State = AttachmentState.Ready, Sent = file.Size, Id = id }, raise: true);
					return;
				}

				// Not awaited for a reply: the server answers only the last chunk (or the upload's first error, which the last chunk then gets).
				await transport.SendAsync(envelope, ct);
				Update(file.Handle, p => p with { Sent = offset + length }, raise: chunk % ProgressEvery == 0);
			}
		}
		catch (OperationCanceledException) when (!Items.Any(i => i.File.Handle == file.Handle))
		{
			// Removed while uploading.
		}
		catch (Exception ex)
		{
			Update(file.Handle, p => p with { State = AttachmentState.Failed, Error = Describe(ex) }, raise: true);
		}
		finally
		{
			_uploading.Release();
		}
	}

	private string Describe(Exception ex) => ex switch
	{
		RequestFailedException failed => failed.Message,
		IOException io => io.Message,
		_ => transport.State == TransportState.Connected ? "Could not reach the server." : SyncEngine.ConnectionLost,
	};

	/// <summary>No-op for a file removed meanwhile.</summary>
	private void Update(string handle, Func<PendingAttachment, PendingAttachment> change, bool raise)
	{
		lock (_gate)
		{
			var i = _items.FindIndex(p => p.File.Handle == handle);
			if (i < 0)
			{
				return;
			}

			_items[i] = change(_items[i]);
		}

		if (raise)
		{
			Changed?.Invoke();
		}
	}
}
```

(Check that `RequestFailedException.Message` carries the server's text — `ChatEngine.Describe` already relies on that for `bad_request`.)

`ChatItem.cs` — document and add the last parameter:

```csharp
/// <param name="Attachments">The files of a <see cref="ChatItemKind.User"/> message.</param>
```

```csharp
	bool Streaming = false,
	IReadOnlyList<ChatAttachment>? Attachments = null);
```

(add `using AiChromeProxy.Domain.Chat;`).

`ChatSession.cs`:
- the title case becomes:

```csharp
				case ChatEventKinds.Prompt when Title.Length == 0 && (string.IsNullOrWhiteSpace(e.Text) ? e.Attachments?.FirstOrDefault()?.Name : e.Text) is { } text:
					Title = Shorten(text);
					break;
```

- `Append` keeps the attachments of a prompt (a split prompt's later parts have none):

```csharp
	private static void Append(List<ChatItem> items, ChatItemKind kind, ChatEvent e)
	{
		var text = e.Text ?? string.Empty;
		if (items.Count > 0 && items[^1] is { Streaming: false } last && last.Kind == kind && last.RunId == e.RunId)
		{
			items[^1] = last with { Text = last.Text + text, Attachments = last.Attachments ?? e.Attachments };
			return;
		}

		items.Add(new ChatItem(kind, e.RunId, text, Attachments: e.Attachments));
	}
```

`ChatEngine.cs`:
- property after `LastCost`:

```csharp
	/// <summary>Gets the repo the chat sends to (the open folder's), or null without one.</summary>
	public string? Repo
	{
		get
		{
			lock (_gate)
			{
				return _repo ?? sync.Repo;
			}
		}
	}
```

- `SendAsync`: signature `public async Task<bool> SendAsync(string text, IReadOnlyList<string>? attachments = null)`; doc: "`<param name="attachments">` Ids of uploaded files (<see cref="ChatAttachments.ReadyIds"/>). `<returns>` Whether the run started (false: the refusal is an error line of the session)."; the payload becomes `new ChatSendPayload(repo, session.Id, text, attachments is { Count: > 0 } ? attachments : null)`; the early `return;` becomes `return false;`; in the `try` after the lock add `Raise(); return true;` and the method's final `Raise();` is followed by `return false;` (the catch path).

- [ ] **Step 5: Run the tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*Client.ChatAttachmentsTests" --filter-class "*ChatEngineTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/AiChromeProxy.Client/Chat tests/AiChromeProxy.Tests/Client
git commit -m "Chat attachments: client uploads, send with ids, attachments in the history"
```

---

### Task 6: Browser glue and UI (paste, drop, 📎, chips)

**Files:**
- Modify: `src/AiChromeProxy.Client/Scripts/chat.ts`, `src/AiChromeProxy.Client/Shell/JsChatView.cs`, `src/AiChromeProxy.Client/Program.cs`, `src/AiChromeProxy.Client/Shell/ChatInput.razor`, `src/AiChromeProxy.Client/Shell/ChatView.razor`, `src/AiChromeProxy.Client/Shell/MainArea.razor`, `src/AiChromeProxy.Client/Shell/Icon.razor`, `src/AiChromeProxy.Client/wwwroot/css/app.css`, `src/AiChromeProxy.Server/Hosting/ClientPage.cs:65`
- Test: `tests/AiChromeProxy.Tests/Server/ClientPageTests.cs` (UI itself: E2E in Task 7)

**Interfaces:**
- Consumes: `ChatAttachments`, `IAttachmentSource`, `AttachmentFile`, `ChatEngine.Repo`, `ChatEngine.SendAsync(text, ids)`, `ChatItem.Attachments` (Task 5); existing `Format.Bytes`, `Format.FileKind`, `Icon`.
- Produces: TS exports `acceptFiles(zone, picker, callback)`, `readAttachment(handle, offset, length)`, `forgetAttachment(handle)`; `JsChatView : IAttachmentSource` + `AcceptFilesAsync(zone, picker, add)`; `ChatInput.OnSend : Func<string, IReadOnlyList<string>, Task<bool>>`; test ids `chat-compose`, `chat-file`, `chat-attachment`, `chat-attachment-remove`, `chat-attach-note`, `chat-user-attachment`.

- [ ] **Step 1: Write the failing CSP test** — in `ClientPageTests.ContentSecurityPolicy_LetsTheCodeViewerRun_ItsWorkerFromABlobAndItsIconFontFromData` add:

```csharp
		// Previews of pictures pasted into the chat box are object URLs.
		Assert.Equal("img-src 'self' data: blob:", directives["img-src"]);
```

- [ ] **Step 2: Run to see it fail**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*ClientPageTests"`
Expected: FAIL (`img-src 'self' data:`).

- [ ] **Step 3: CSP** — in `ClientPage.cs` line 65 change `img-src 'self' data:` to `img-src 'self' data: blob:`. Run the test: PASS.

- [ ] **Step 4: TypeScript** — append to `Scripts/chat.ts` (and extend the header comment: "files pasted, dropped or picked into the chat box are kept here under a handle and read in chunks by ChatAttachments.cs"):

```ts
/** A file reported to C# (JsChatView.AttachCallback.Add → AttachmentFile). */
interface AttachedFile {
    handle: string;
    name: string;
    size: number;
    type: string;
    preview: string | null;
}

const attached = new Map<string, { file: File; preview: string | null }>();
let lastHandle = 0;

/** Pasted screenshots arrive as "image.png": name them by the local time instead, so that several stay apart. */
function nameOf(file: File, pasted: boolean): string {
    if (!pasted || !file.type.startsWith('image/') || (file.name !== '' && file.name !== 'image.png')) {
        return file.name === '' ? 'file' : file.name;
    }
    const d = new Date();
    const two = (n: number): string => String(n).padStart(2, '0');
    const ext = file.type.slice('image/'.length).replace('jpeg', 'jpg').replace(/[^a-z0-9]/g, '') || 'png';
    return `pasted-${d.getFullYear()}${two(d.getMonth() + 1)}${two(d.getDate())}-${two(d.getHours())}${two(d.getMinutes())}${two(d.getSeconds())}.${ext}`;
}

/** Keeps the files under new handles and reports them to callback.Add(files); false when there were none. */
function report(list: FileList | null | undefined, pasted: boolean, callback: DotNetObject): boolean {
    if (list === null || list === undefined || list.length === 0) {
        return false;
    }
    const added: AttachedFile[] = [];
    for (const file of Array.from(list)) {
        const handle = `f${++lastHandle}`;
        const preview = file.type.startsWith('image/') ? URL.createObjectURL(file) : null;
        attached.set(handle, { file, preview });
        added.push({ handle, name: nameOf(file, pasted), size: file.size, type: file.type, preview });
    }
    void callback.invokeMethodAsync('Add', added);
    return true;
}

/** Files pasted into, dropped on or picked in the chat box go to callback.Add(files); pasting text and dragging text stay as they are. */
export function acceptFiles(zone: HTMLElement, picker: HTMLInputElement, callback: DotNetObject): void {
    const hasFiles = (e: DragEvent): boolean => e.dataTransfer?.types.includes('Files') === true;
    zone.addEventListener('paste', (e: ClipboardEvent) => {
        if (report(e.clipboardData?.files, true, callback)) {
            e.preventDefault();
        }
    });
    zone.addEventListener('dragover', (e: DragEvent) => {
        if (hasFiles(e)) {
            e.preventDefault();
            zone.classList.add('dragging');
        }
    });
    zone.addEventListener('dragleave', (e: DragEvent) => {
        if (!(e.relatedTarget instanceof Node && zone.contains(e.relatedTarget))) {
            zone.classList.remove('dragging');
        }
    });
    zone.addEventListener('drop', (e: DragEvent) => {
        zone.classList.remove('dragging');
        if (hasFiles(e)) {
            e.preventDefault();
            report(e.dataTransfer?.files, false, callback);
        }
    });
    picker.addEventListener('change', () => {
        report(picker.files, false, callback);
        picker.value = '';
    });
}

/** Up to length bytes of a kept file from offset (none when it was forgotten). */
export async function readAttachment(handle: string, offset: number, length: number): Promise<Uint8Array> {
    const entry = attached.get(handle);
    if (entry === undefined) {
        return new Uint8Array(0);
    }
    return new Uint8Array(await entry.file.slice(offset, offset + length).arrayBuffer());
}

/** Drops a kept file and its preview. */
export function forgetAttachment(handle: string): void {
    const entry = attached.get(handle);
    if (entry?.preview) {
        URL.revokeObjectURL(entry.preview);
    }
    attached.delete(handle);
}
```

- [ ] **Step 5: `JsChatView` implements the seam** — class declaration `public sealed class JsChatView(IJSRuntime js) : IAttachmentSource, IAsyncDisposable`; update the summary ("...and the chat box's attached files (<see cref="IAttachmentSource"/>)"); add:

```csharp
	/// <summary>Makes files pasted into or dropped on <paramref name="zone"/>, or picked with <paramref name="picker"/>, call <paramref name="add"/>.</summary>
	/// <param name="zone">The chat box with its chips.</param>
	/// <param name="picker">The 📎 file input.</param>
	/// <param name="add">What to do with the files.</param>
	/// <returns>The reference to dispose with the box.</returns>
	public async Task<IDisposable> AcceptFilesAsync(ElementReference zone, ElementReference picker, Func<IReadOnlyList<AttachmentFile>, Task> add)
	{
		var callback = DotNetObjectReference.Create(new AttachCallback(add));
		await (await ChatAsync()).InvokeVoidAsync("acceptFiles", zone, picker, callback);
		return callback;
	}

	public async Task<byte[]> ReadChunkAsync(string handle, long offset, int length) =>
		await (await ChatAsync()).InvokeAsync<byte[]>("readAttachment", handle, offset, length);

	public async Task ForgetAsync(string handle) =>
		await (await ChatAsync()).InvokeVoidAsync("forgetAttachment", handle);
```

and the callback class next to the others:

```csharp
	/// <summary>Target of <c>callback.invokeMethodAsync('Add', files)</c>.</summary>
	[ExcludeFromCodeCoverage]
	public sealed class AttachCallback(Func<IReadOnlyList<AttachmentFile>, Task> add)
	{
		[JSInvokable]
		public Task Add(AttachmentFile[] files) => add(files);
	}
```

`Client/Program.cs` — after `builder.Services.AddSingleton<JsChatView>();`:

```csharp
builder.Services.AddSingleton<IAttachmentSource>(s => s.GetRequiredService<JsChatView>());
```

- [ ] **Step 6: Icon** — in `Icon.razor` after the `send` case:

```razor
        case "paperclip":
            <path d="M13 7.5l-5 5a3 3 0 01-4.2-4.2l5.5-5.5a2 2 0 012.8 2.8L6.6 11.1a1 1 0 01-1.4-1.4L10 4.9" />
            break;
```

- [ ] **Step 7: `ChatInput.razor`** — replace the file with:

```razor
@using AiChromeProxy.Client.Chat
@using AiChromeProxy.Client.Transport
@using AiChromeProxy.Domain.Chat
@inject JsChatView View
@inject ITransport Transport
@inject IAttachmentSource Source
@implements IDisposable

@* Enter sends and Shift+Enter adds a line (the keydown handler is in Scripts/chat.ts: Blazor cannot cancel a key conditionally). While Claude works, the send button is Stop.
   Files pasted into the box, dropped on it or picked with the paperclip upload at once (ChatAttachments) and show as chips above it. *@
<div class="chat-compose" data-testid="chat-compose" @ref="_zone">
    @if (_attachments.Items.Count > 0)
    {
        <ul class="attachments" aria-label="Attached files">
            @foreach (var item in _attachments.Items)
            {
                <li class="attachment @item.State.ToString().ToLowerInvariant()" data-testid="chat-attachment" title="@(item.Error ?? item.File.Name)">
                    @if (item.File.Preview is { } preview)
                    {
                        <img src="@preview" alt="" />
                    }
                    else
                    {
                        <Icon Name="@("file-" + Format.FileKind(item.File.Name))" Size="16" Class="@("kind-" + Format.FileKind(item.File.Name))" />
                    }
                    <span class="attachment-name">@item.File.Name</span>
                    <span class="attachment-size">@(item.State switch
                    {
                        AttachmentState.Uploading => $"{(item.File.Size == 0 ? 0 : item.Sent * 100 / item.File.Size)}%",
                        AttachmentState.Failed => item.Error,
                        _ => Format.Bytes(item.File.Size),
                    })</span>
                    <button type="button" class="attachment-remove" data-testid="chat-attachment-remove" aria-label="@($"Remove {item.File.Name}")" title="Remove" @onclick="() => _attachments.RemoveAsync(item.File.Handle)"><Icon Name="close" Size="12" /></button>
                </li>
            }
        </ul>
    }
    @if (_attachments.Note is { } note)
    {
        <div class="attach-note" data-testid="chat-attach-note" role="status">@note</div>
    }
    <div class="chat">
        <textarea data-testid="chat-input" rows="2" disabled="@(!Enabled)" aria-label="Message to Claude" maxlength="@ChatLimits.MaxTextChars" @ref="_box"
                  placeholder="@Placeholder"
                  @bind="_text" @bind:event="oninput"></textarea>
        <label class="attach" title="Attach files">
            <input type="file" multiple data-testid="chat-file" aria-label="Attach files" disabled="@(!Enabled)" @ref="_picker" />
            <Icon Name="paperclip" />
        </label>
        @if (Chat.Running)
        {
            <button type="button" class="send stop" data-testid="chat-stop" title="Stop" aria-label="Stop" disabled="@(!Enabled)" @onclick="Chat.StopAsync"><Icon Name="stop" /></button>
        }
        else
        {
            <button type="button" class="send" data-testid="chat-send" title="Send" aria-label="Send" disabled="@(!CanSend(_text))" @onclick="() => SubmitAsync(_text)"><Icon Name="send" /></button>
        }
    </div>
</div>

@code {
    private ElementReference _box;
    private ElementReference _zone;
    private ElementReference _picker;
    private IDisposable? _callback;
    private IDisposable? _filesCallback;
    private ChatAttachments _attachments = null!;
    private string _text = string.Empty;

    [Parameter, EditorRequired]
    public ChatEngine Chat { get; set; } = null!;

    /// <summary>Connected and a folder open.</summary>
    [Parameter]
    public bool Enabled { get; set; }

    /// <summary>What the empty box says (see <see cref="Format.ChatPlaceholder"/>).</summary>
    [Parameter]
    public string Placeholder { get; set; } = string.Empty;

    /// <summary>Sends the message with the ids of its uploaded files; true when the run started. The page shows the chat tab and keeps the tabs in step with the session.</summary>
    [Parameter, EditorRequired]
    public Func<string, IReadOnlyList<string>, Task<bool>> OnSend { get; set; } = null!;

    public void Dispose()
    {
        _callback?.Dispose();
        _filesCallback?.Dispose();
        _attachments.Changed -= OnAttachmentsChanged;
        _ = _attachments.ClearAsync();
    }

    /// <summary>Puts the text cursor in the message box.</summary>
    /// <returns>A task that completes when the box has the focus.</returns>
    public ValueTask FocusAsync() => _box.FocusAsync();

    protected override void OnInitialized()
    {
        _attachments = new ChatAttachments(Transport, Source);
        _attachments.Changed += OnAttachmentsChanged;
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                _callback = await View.SendOnEnterAsync(_box, text => InvokeAsync(() => SubmitAsync(text)));
                _filesCallback = await View.AcceptFilesAsync(_zone, _picker, files =>
                {
                    _ = InvokeAsync(() => _attachments.AddAsync(Chat.Repo, files));
                    return Task.CompletedTask;
                });
            }
            catch (JSException)
            {
                // Without the script Enter adds a line and files cannot be attached; the send button still works.
            }
        }
    }

    private void OnAttachmentsChanged() => _ = InvokeAsync(StateHasChanged);

    private bool CanSend(string text) => Enabled && _attachments.CanSend && (!string.IsNullOrWhiteSpace(text) || _attachments.Items.Count > 0);

    private async Task SubmitAsync(string text)
    {
        // Enter while Claude works, while a file uploads or with nothing to send does nothing (the server would answer "busy").
        if (Chat.Running || !CanSend(text))
        {
            return;
        }

        var ids = _attachments.ReadyIds;
        _text = string.Empty;
        StateHasChanged();
        if (await OnSend(text.Trim(), ids))
        {
            await _attachments.ClearAsync();
        }
    }
}
```

(Check `JSException` comes from `Microsoft.JSInterop` — the existing file already compiles with it, so the `_Imports.razor` covers it.)

- [ ] **Step 8: `MainArea.razor`** — the `SendAsync` method:

```csharp
    private async Task<bool> SendAsync(string text, IReadOnlyList<string> attachments)
    {
        var tab = TabSet.ChatTab(Chat.Current.Id);
        Tabs.Show(tab);
        await OnChanged.InvokeAsync();
        var sent = await Chat.SendAsync(text, attachments);
        if (Chat.Current.Id is { } id && TabSet.ChatTab(id) != tab)
        {
            Tabs.Rename(tab, TabSet.ChatTab(id));
        }

        await OnChanged.InvokeAsync();
        return sent;
    }
```

- [ ] **Step 9: `ChatView.razor`** — the `User` case:

```razor
                    case ChatItemKind.User:
                        <div class="msg msg-user" data-testid="chat-user">@item.Text</div>
                        @if (item.Attachments is { Count: > 0 } files)
                        {
                            <ul class="msg-attachments" aria-label="Attached files">
                                @foreach (var file in files)
                                {
                                    <li class="attachment-chip" data-testid="chat-user-attachment">
                                        <Icon Name="@("file-" + Format.FileKind(file.Name))" Size="14" Class="@("kind-" + Format.FileKind(file.Name))" />
                                        <span>@file.Name</span>
                                        <span class="attachment-size">@Format.Bytes(file.Size)</span>
                                    </li>
                                }
                            </ul>
                        }
                        break;
```

(A message without text renders an empty `msg-user` bubble; hide it with CSS `.msg-user:empty { display: none; }`.)

- [ ] **Step 10: CSS** — append to `app.css` after the `.send:disabled` rule (use the theme variables already used around `.chat`):

```css
.chat-compose {
    flex: none;
    margin: 0 16px 12px;
    border-radius: 8px;
}

.chat-compose .chat {
    margin: 0;
}

.chat-compose.dragging .chat {
    border-color: var(--button-bg);
    box-shadow: 0 0 0 2px var(--button-bg);
}

.attachments,
.msg-attachments {
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
    margin: 0 0 6px;
    padding: 0;
    list-style: none;
}

.msg-attachments {
    justify-content: flex-end;
    margin-top: -4px;
}

.attachment,
.attachment-chip {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    max-width: 260px;
    padding: 4px 6px;
    border: 1px solid var(--border-strong);
    border-radius: 6px;
    background: var(--bg-input);
    font-size: 12px;
}

.attachment img {
    width: 32px;
    height: 32px;
    border-radius: 4px;
    object-fit: cover;
}

.attachment-name,
.attachment-chip span:first-of-type {
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.attachment-size {
    flex: none;
    color: var(--fg-muted);
}

.attachment.failed {
    border-color: var(--error, #d33);
}

.attachment.failed .attachment-size {
    color: var(--error, #d33);
}

.attachment-remove {
    display: grid;
    place-items: center;
    width: 18px;
    height: 18px;
    padding: 0;
    border: 0;
    border-radius: 4px;
    background: none;
    color: var(--fg-muted);
    cursor: pointer;
}

.attachment-remove:hover,
.attachment-remove:focus-visible {
    background: var(--bg-active);
    color: var(--fg);
}

.attach-note {
    margin: 0 0 6px;
    color: var(--fg-muted);
    font-size: 12px;
}

.attach {
    position: relative;
    display: grid;
    place-items: center;
    width: 30px;
    height: 30px;
    border-radius: 6px;
    color: var(--fg-muted);
    cursor: pointer;
}

.attach:hover {
    background: var(--bg-active);
    color: var(--fg);
}

.attach input {
    position: absolute;
    width: 1px;
    height: 1px;
    opacity: 0;
}

.attach:has(input:focus-visible) {
    outline: 2px solid var(--button-bg);
}

.attach:has(input:disabled) {
    cursor: not-allowed;
    opacity: 0.5;
}

.msg-user:empty {
    display: none;
}
```

The old `.chat` rule keeps `margin: 0 16px 12px` for any other use; `.chat-compose .chat` resets it inside the box. If `--error` is not a defined variable, use the one the error rows use (search `app.css` for `.tool-error`).

- [ ] **Step 11: Build and run all unit tests**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build`
Expected: build clean (TypeScript compiles, no StyleCop errors), all tests PASS (`BrowserScriptTests` checks no `any`).

- [ ] **Step 12: Commit**

```bash
git add src tests/AiChromeProxy.Tests/Server/ClientPageTests.cs
git commit -m "Chat attachments: paste, drop or pick files into the chat box; chips in the box and the history"
```

---

### Task 7: E2E, docs, final check

**Files:**
- Create: `tests/AiChromeProxy.E2E/Fixtures/attach.jsonl`, `tests/AiChromeProxy.E2E/StepDefinitions/AttachmentSteps.cs`
- Modify: `tests/AiChromeProxy.E2E/Hooks/AppServer.cs`, `tests/AiChromeProxy.E2E/Hooks/BrowserHooks.cs`, `tests/AiChromeProxy.E2E/Features/Chat.feature`, `docs/chat.md`, `docs/superpowers/specs/2026-10-19-chat-attachments-design.md`

**Interfaces:**
- Consumes: test ids from Task 6; the fake agent's `<ArgsDirectory>/<repo>.args`.

- [ ] **Step 1: Attachments root in the E2E host** — `AppServer`: after `MirrorRoot = ...`:

```csharp
		AttachmentsRoot = Path.Combine(root, "attachments");
```

with `b.UseSetting("Attachments:Root", AttachmentsRoot);` next to `Mirror:Root`, and the property:

```csharp
	/// <summary>Where the server keeps chat attachments (one folder per repo).</summary>
	public string AttachmentsRoot { get; }
```

`BrowserHooks` next to `ArgsDirectory`:

```csharp
	/// <summary>Where the server keeps chat attachments (<c>&lt;repo&gt;\&lt;id&gt;\&lt;name&gt;</c>).</summary>
	public static string AttachmentsRoot => _server!.AttachmentsRoot;
```

- [ ] **Step 2: Fixture** — `tests/AiChromeProxy.E2E/Fixtures/attach.jsonl`:

```
{"type":"system","subtype":"init","session_id":"e2e-attach","cwd":"/srv/mirror/e2e-repo","tools":["Read"],"model":"fake"}
{"type":"assistant","message":{"id":"msg_1","role":"assistant","content":[{"type":"text","text":"Got the files."}]},"session_id":"e2e-attach"}
{"type":"result","subtype":"success","is_error":false,"duration_ms":100,"result":"Got the files.","total_cost_usd":0.01,"session_id":"e2e-attach"}
```

- [ ] **Step 3: Scenario** — append to `Chat.feature`:

```gherkin
Scenario: A pasted picture and a dropped file go to Claude with the message
	Given the app is connected with a synced folder open
	When I paste a picture into the chat box
	And I drop a file "notes.txt" on the chat box
	Then the chat box shows 2 attachments
	And I save screenshots named "attachments"
	When I type "attach please" and press Enter
	Then my message "attach please" is shown
	And the message lists the attachments "pasted-" and "notes.txt"
	And the chat box shows 0 attachments
	And Claude's answer says "Got the files."
	And the run could read the attachments folder, which holds "notes.txt"
	And I save screenshots named "attachments-sent"
```

- [ ] **Step 4: Steps** — `tests/AiChromeProxy.E2E/StepDefinitions/AttachmentSteps.cs`:

```csharp
using System.Text.RegularExpressions;
using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Attachments in the chat box: a paste and a drop dispatched with a real <c>DataTransfer</c> (the OS clipboard and drag cannot be automated).</summary>
[Binding]
public sealed class AttachmentSteps(IPage page)
{
	private const string PastePicture =
		"""
		async () => {
			const canvas = document.createElement('canvas');
			canvas.width = 40; canvas.height = 30;
			const ctx = canvas.getContext('2d');
			ctx.fillStyle = '#c33'; ctx.fillRect(0, 0, 40, 30);
			const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
			const data = new DataTransfer();
			data.items.add(new File([blob], 'image.png', { type: 'image/png' }));
			document.querySelector('[data-testid=chat-input]').dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
		}
		""";

	private const string DropFile =
		"""
		name => {
			const data = new DataTransfer();
			data.items.add(new File(['hello notes'], name, { type: 'text/plain' }));
			const zone = document.querySelector('[data-testid=chat-compose]');
			for (const type of ['dragover', 'drop']) {
				zone.dispatchEvent(new DragEvent(type, { dataTransfer: data, bubbles: true, cancelable: true }));
			}
		}
		""";

	[When("I paste a picture into the chat box")]
	public async Task WhenIPasteAPictureAsync() => await page.EvaluateAsync(PastePicture);

	[When("I drop a file {string} on the chat box")]
	public async Task WhenIDropAFileAsync(string name) => await page.EvaluateAsync(DropFile, name);

	[Then("the chat box shows {int} attachments")]
	public async Task ThenTheChatBoxShowsAttachmentsAsync(int count)
	{
		await Expect(page.GetByTestId("chat-attachment")).ToHaveCountAsync(count);
		if (count > 0)
		{
			await Expect(page.Locator("[data-testid=chat-attachment].ready")).ToHaveCountAsync(count);
		}
	}

	[Then("the message lists the attachments {string} and {string}")]
	public async Task ThenTheMessageListsTheAttachmentsAsync(string first, string second)
	{
		var chips = page.GetByTestId("chat-user-attachment");
		await Expect(chips).ToHaveCountAsync(2);
		await Expect(chips.Nth(0)).ToContainTextAsync(first);
		await Expect(chips.Nth(1)).ToContainTextAsync(second);
	}

	[Then("the run could read the attachments folder, which holds {string}")]
	public async Task ThenTheRunCouldReadTheAttachmentsFolderAsync(string name)
	{
		var repo = (await page.GetByTestId("folder-name").TextContentAsync())!;
		var folder = Path.Combine(BrowserHooks.AttachmentsRoot, repo);
		var args = await File.ReadAllLinesAsync(Path.Combine(BrowserHooks.ArgsDirectory, repo + ".args"));

		Assert.Equal(folder, args[Array.IndexOf(args, "--add-dir") + 1]);
		Assert.Single(Directory.GetFiles(folder, name, SearchOption.AllDirectories));
	}
}
```

(Use the assertion library the E2E project already uses for non-Playwright checks — look at how `ClaudeToolsSteps` asserts on the args file and copy it; `Assert` from xunit is the expected one. `Regex` may be unused — remove the using if so.)

- [ ] **Step 5: Run the E2E suite**

Run (see `docs/testing.md` for the exact command and browser install): `dotnet test --project tests/AiChromeProxy.E2E -c Release`
Expected: PASS, including the new scenario. With `E2E_SCREENSHOT_DIR` set, look at `attachments*` screenshots in dark and light: chips readable, preview visible, the paperclip aligned with Send.

- [ ] **Step 6: Docs** — `docs/chat.md`:

Add a section after *Sessions and runs*:

```markdown
## Attachments

Paste (Ctrl+V of a screenshot or of files copied in Explorer), drop files on the message box, or use the paperclip. Each file shows as a chip above the box — pictures with a preview — and uploads at once; **Send** waits until every chip is uploaded, and a message may consist of files alone. Pasted screenshots are named `pasted-<yyyyMMdd-HHmmss>.png`.

- Up to 10 files per message, 20 MB each ("'name' is larger than 20 MB.", "At most 10 files per message."). A failed upload turns its chip red with the reason: remove it and attach the file again.
- Files are stored on the home computer in `<DataDir>\attachments\<repo>\<id>\<name>` (`Attachments:Root`; in development `data\attachments`), outside the mirror: they never reach your folder or git. Claude gets their absolute paths after your text ("Attached files (read them with the Read tool): ...") and may read that folder without a card (`--add-dir`); pictures and PDFs are read with its Read tool.
- Sent messages list their files (name and size) in the history. Attachments are deleted after 30 days, unfinished uploads after a day (checked when an upload starts, at most once a day); a resumed session keeps what Claude read. Sending a file deleted meanwhile says "An attachment has expired; attach it again." and keeps the chips.
```

In *Limitations* replace "- No attachments, no slash commands or TUI." with "- No slash commands or TUI." In *Security* add: "- Attached files are kept as the service account in the attachments folder for 30 days and are readable by every run of that repo." In the *Manual checklist* add:

```markdown
- [ ] Paste a screenshot (Win+Shift+S, then Ctrl+V in the box) and drop a PDF: chips with preview and size, Send waits for the upload; Claude describes the picture and quotes the PDF. The history shows both files after a reload; neither appears in your folder.
- [ ] Attach a file over 20 MB and an 11th file: refused with the note. Remove a chip during a large upload: it disappears and the message goes without it.
```

Spec: in *Decisions* change the *Clean-up* row to the lazy sweep ("When an upload starts and the last sweep is over a day old ..."), the `chat.send` row's error to `bad_request` "An attachment has expired; attach it again.", and the *Errors* table's send row to "text cleared as before, chips stay".

- [ ] **Step 7: Full gate**

Run:

```bash
dotnet build -c Release
dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total
```

Expected: build clean; all tests PASS; coverage ≥ 85 % (below: add tests, never lower the threshold).

- [ ] **Step 8: Commit**

```bash
git add tests/AiChromeProxy.E2E docs
git commit -m "Chat attachments: E2E scenario, docs and manual checklist"
```

- [ ] **Step 9: PR** — push the branch, open a PR, wait until `gh pr checks <number>` shows every check passed (look at the checks' state, not a piped tail), then report.
