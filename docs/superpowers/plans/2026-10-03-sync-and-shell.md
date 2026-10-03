# One-way Sync and the App Shell (3a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** In Chrome the user clicks **Open folder**, picks the repository folder, and it is mirrored to the home server within seconds and kept in sync (edits arrive within ~10 s); the bare status page is replaced by a small VS Code-like shell (title bar, activity bar, explorer with the synced tree, editor area, status bar, dark/light theme).

**Architecture:** The skeleton-review follow-ups land first: handlers get an `EnvelopeContext` (connection id, Access email, `SendAsync`), failures become `error {code, message?}` replies with the request's correlation id, the client gets `RequestAsync` and reconnects forever. The wire contract (payload records, limits, path validator, repo-name sanitizer) is pure Domain code shared by both ends. The server side is a per-connection `SyncSession` (Application) over an `IMirrorStore` port implemented on the file system in Infrastructure (atomic temp-file commit, SHA-256 check, link-safe deletes). The browser side is a pure C# `SyncEngine` (scan → full manifest or delta → sequential chunked uploads) behind `IFolderAccess`; only `fsaccess.js` touches the File System Access API, IndexedDB and WebCrypto, through the thin `[ExcludeFromCodeCoverage]` wrapper `JsFolderAccess`. The shell is plain Blazor components plus one CSS file with design tokens.

**Tech Stack:** .NET 10; ASP.NET Core SignalR (existing hub, default 32 KB message limit kept); Blazor WebAssembly; System.Text.Json (`JsonSerializerOptions.Web`, camelCase); `System.Security.Cryptography` (`SHA256`, `IncrementalHash`); File System Access API + IndexedDB + `crypto.subtle` in one ES module; xunit v3 (+ `WebApplicationFactory`, existing `ServiceSetupSecurityTests.Junction`, `ListLogger<T>`, `TempRootCleanup`); Reqnroll + Playwright for E2E. One new package reference: `Microsoft.Extensions.Logging.Abstractions` 10.0.12 in Application (same version line as the existing `Microsoft.Extensions.*` 10.0.12 packages).

**Spec:** [docs/superpowers/specs/2026-10-03-sync-and-shell-design.md](../specs/2026-10-03-sync-and-shell-design.md). Architecture: [2026-10-02-architecture-design.md](../specs/2026-10-02-architecture-design.md) ("Sync flow", "Security", "Required follow-ups from the skeleton review").

**Verification:** every code block below was built and run on a scratch worktree of this branch (not committed): `dotnet build -c Release` → 0 warnings, 0 errors after every task; the suite grows 457 → 470 → 478 → 537 → 592 → 601 → 619 → 652 → 654 → 675 → 719 tests (Tasks 11–12 add none), all passing, total line coverage 98.1% at the end; the full suite was re-run 3× and `SyncEngineTests` 5× with no flake; E2E: 7 scenarios passed; the shell was screenshot-checked in dark and light (1280×720).

## Global Constraints

- **Safety while implementing:** never run the tray, Setup/Velopack, cloudflared, real services/UAC, never touch the real %ProgramData% or %LocalAppData%\AiChromeProxy (a live install with a running service exists on this machine, port 5180 is taken — local runs/tests use other ports or random ports); no personal domains (use example.com); no Claude/AI attribution in commits; UTF-8 without BOM; never name a source folder Logs/Log/Release/Debug/bin/obj; JS only where C# can't (fsaccess.js); coverage of JS interop glue via [ExcludeFromCodeCoverage] thin wrappers only.
- Tests use temp folders under `TempRootCleanup.Root` (`%TEMP%\aicp-tests`) and set `Mirror:Root` explicitly whenever a test syncs; WebApplicationFactory hosts use in-memory `TestServer` (E2E: Kestrel on a random loopback port). For a manual local run use `Server__Port` other than 5180 (e.g. 5197) and do not set `AICP_DATA_DIR` to the real data directory.
- Dependency direction unchanged and enforced by `tests/AiChromeProxy.Tests/Architecture` (must stay green unchanged): Domain ← Application ← Infrastructure ← Server; Client → Domain only; Application stays free of ASP.NET Core and IdentityModel (`Microsoft.Extensions.Logging.Abstractions` is allowed).
- Style: tabs in C#; CRLF; no `this.`; private fields `_camelCase`; sorted usings (`System*` first); file-scoped namespaces; one class/record per file (SA1402 counts records; an enum may share a file); block-form `using (...) { }` only (`UsingStatementStyleTests`); `Async` suffix on async methods; XML doc comments in the style of the existing code; StyleCop errors fail the build — fix code, never the ruleset. English only.
- The `Envelope` JSON shape is a public camelCase contract (shared with the future Flutter app): only compatible additions — new message types, new optional payload fields.
- Exact values from the spec (copy, don't retype):
  - Messages: `sync.open {repo}` → `sync.manifest {repo, entries[{path,size,sha256}], final}` pages → `sync.need {repo, paths[]}`; `sync.chunk {repo, path, offset, data(base64), last, sha256?}` (≤ 16 KB raw) → `sync.stored {repo, path}` or `error`; later scans `sync.delta {repo, upserts[], deletes[]}`; error codes `bad_request`, `not_found`, `too_large`, `internal`.
  - Pages ≤ 500 entries; chunks ≤ 16 KB raw; files > 20 MB skipped ("too large"); at most 20 000 files; rescan every 10 s while visible, immediately on focus; reconnect delays 1, 2, 5, 10 s then every 30 s forever, then the full manifest again.
  - Built-in excludes: `.git/`, `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `.env`, `.env.*`, `*.pfx`, `*.key`, `*.pem`, `id_rsa*`; plus the root `.gitignore` (`#`, blank, `*`, `**`, `?`, trailing `/`, leading `/`, `!`).
  - Mirror: `Mirror:Root`; default `<DataDir>\mirror` with a data directory, else `data/mirror` under the content root (gitignored); `<root>\<repo>`, repo sanitized to `[A-Za-z0-9._-]`, max 64; temp file `<path>.aicp-tmp`, moved into place after the SHA-256 matched.
  - Paths: relative, `/`-separated, no empty / `.` / `..` segments, no `\`, no `:`, no reserved Windows names, no trailing dot or space, ≤ 260 chars; resolved path inside `<root>\<repo>`.
  - `fsaccess.js`: `showDirectoryPicker({mode:"read"})`, handle in IndexedDB, after reload only `requestPermission`; walk returns `{path, size, mtime}` (named `modified`); SHA-256 via `crypto.subtle`, cached in IndexedDB by size/mtime.
  - UI: title bar (app name, folder name, connection pill Connected / Reconnecting… / Offline with ping latency on hover, theme toggle); activity bar (Explorer active; Chat and Search disabled, "coming soon" tooltips); explorer (**Open folder**, or folder name + **Change** + **Restore access**; summary: files synced / total, bytes, last sync, progress bar; tree with collapsible folders, icons by extension, badge synced / pending / too large / error; excluded items not shown); editor (welcome page; selected file metadata: path, size, hash, sync state); status bar (connection, "Synced 1 234 files" / "Uploading 12/80" / "Rescan in 7 s", errors count opening a list); keyboard tree (arrows/Enter), visible focus, WCAG AA contrast, works from 1024 px, sidebar resizable and collapsible.
- Commands (from the repo root):
  - Build: `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
  - Gate: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` → `failed: 0`, exit code 0.
  - One class: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "<Namespace.ClassName>"`.
  - E2E (Task 11 only): `dotnet build tests/AiChromeProxy.E2E` then `dotnet test --project tests/AiChromeProxy.E2E` → all passed (Chromium installed once per docs/testing.md).
- Commit only the files of the task; messages without any AI attribution.

## Decisions taken while planning (spec ambiguities)

The user delegated these; each is the smallest reading of the spec that satisfies it.

1. **`sync.open` reply.** New type `sync.opened {repo}` carrying the sanitized repo name: every request needs a reply for `RequestAsync`, and the client must use the server's name in later messages.
2. **`sync.need` per page.** Every `sync.manifest` page (and every `sync.delta` page) is answered with `sync.need` for that page's entries; stale mirror files are deleted only after the `final` page. Each page is then an ordinary request with a reply or an error.
3. **Page size.** ≤ 500 entries **and** ≤ 24 000 serialized bytes per page (`SyncLimits.MaxPageBytes`): 500 realistic entries (~150 bytes each, non-ASCII escaped up to 6×) exceed SignalR's 32 KB receive limit, which the architecture says to keep. A test serializes worst-case pages as SignalR invocations and checks < 32 KB.
4. **Chunk replies.** Only the last chunk of a file is a request (answered `sync.stored`); earlier chunks are sent without waiting (SignalR keeps them in order per connection). A failed earlier chunk discards the upload on the server, so the last chunk then fails with `not_found` / `bad_request` and the file is marked as error. Saves a round trip per 16 KB.
5. **Error contract.** `ErrorPayload(code, message?, type?)` in Domain; `ErrorCodes` adds `bad_request`, `not_found`, `too_large`, `internal` to the existing `unknown_type` (kept for compatibility, still with `type`). Null/empty `type` → `bad_request`. Handlers throw `EnvelopeException(code, message)` (mapped by `EnvelopeRouter`); any other exception becomes `internal` without a message in the hub (logged with the type only).
6. **Handler contract.** `IEnvelopeHandler.HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)` still returns the reply; `EnvelopeContext` is a class with `ConnectionId`, `Email` and `SendAsync` (through `IHubContext`, so pushes also work after the handler returned). The email is the validated Access token's `email` claim, put into `HttpContext.Items` by the middleware.
7. **Client helpers.** `RequestAsync(envelope, timeout)` and `ConnectForeverAsync(delay)` are extension methods on `ITransport` (the interface is unchanged); `RequestAsync` subscribes to `Received` per call and owns the correlation id; error replies throw `RequestFailedException(code, message)`, no reply throws `TimeoutException`.
8. **Reconnect.** `ForeverRetryPolicy` for SignalR's automatic reconnect, and the same delays for the first connect (SignalR does not retry a failed `StartAsync`). The "re-send the full manifest" hook is in `SyncEngine`: any non-Connected state clears the session, so the next cycle sends `sync.open` + the full manifest; Connected wakes the loop.
9. **Server sessions.** One `SyncHandler` class registered once per sync type, all routing to the sender's `SyncSession` (kept in `SyncSessions`, closed in `TransportHub.OnDisconnectedAsync`, which deletes an unfinished temp file). No locking: SignalR runs one invocation per connection at a time.
10. **Case.** The server compares manifest paths with the mirror ignoring case (Windows mirror); the client never deletes a path that only changed case (deleting it would delete the upserted file). A case-only rename keeps the old casing in the mirror.
11. **Repo name.** Invalid characters → `_`, trimmed, ≤ 64, trailing dots → `_`; a name that would still be a reserved device name or end in `.aicp-tmp` gets a `_` prefix with dots → `_`; empty → `bad_request`.
12. **Path validator extras.** Also rejects `* ? " < > |`, control characters and the reserved `.aicp-tmp` suffix (so a synced file can never collide with a temp file). The server still checks containment and that no folder on the way (repo folder included) is a link or junction.
13. **Mirror details.** Hash cache in memory per repo+path, keyed by size + last write time (invalidated on commit/delete). Listing skips links/junctions and invalid names (so `.aicp-tmp` leftovers of a crash are never deleted as "stale" and are simply overwritten by the next upload of that file). `DataDirectory.Mirror` added; a relative `Mirror:Root` resolves against the content root; `/src/AiChromeProxy.Server/data/` added to `.gitignore` (the existing `/data/` only covers the repo root).
14. **Limits.** `SyncLimits` (Domain) is shared: the server refuses entries > 20 MB (`too_large`) and manifests > 20 000 entries (`too_large`); the browser skips > 20 MB files (badge "too large") and refuses > 20 000 files to sync with a message. The walk itself stops at 100 000 files (excluded ones included) to protect the tab's memory — gitignored folders are walked, only the built-in folder names are skipped during the walk.
15. **Ignore rules.** Matching ignores case; a `.gitignore` `!` can never re-include a built-in exclude (secrets stay home); a file under an excluded folder cannot be re-included (git semantics); `[...]` classes and `\` escapes are not supported (taken literally).
16. **Hashing.** `hash(paths)` returns null for a file that cannot be read (shown as an error, retried next scan); the IndexedDB cache is one record per folder name, loaded and saved once per call, pruned of files that are gone.
17. **Visibility.** `fsaccess.js` `watchVisibility` calls back on `visibilitychange` and window `focus`; the engine skips scans while hidden and wakes on visible/focus.
18. **Lost permission.** A scan that throws a `JSException` is followed by `restore()`; if access is no longer granted the folder becomes "needs permission" (Explorer shows **Restore access**).
19. **Big repos in the UI.** `SyncEngine.Files` keeps its identity while the set of files is unchanged and progress updates entries in place (`FileAt(path)` for badges); `Changed` fires at most every 200 ms during uploads. The tree is rebuilt only when the list identity changes.
20. **Theme toggle.** Three states: system → light → dark → system (no JS needed to read the system preference); the choice is stored with `localStorage.setItem/removeItem/getItem` called directly through `IJSRuntime` (no extra JS file). `data-theme` on `.shell`; tokens in `app.css`.
21. **Keyboard tree.** One focusable `role="tree"` with `aria-activedescendant`; arrows/Home/End/Enter/Space handled in pure C# (`FileTree.OnKey`). The active row is not scrolled into view (ponytail: needs JS; add with the code navigator).
22. **Ping latency.** One ping on every (re)connect and every 15 s; the pill's tooltip shows "Ping N ms". The status page's **Ping** button is gone; the E2E scenario now checks the tooltip.
23. **Disabled views.** Chat/Search use `aria-disabled="true"` instead of `disabled` so they stay focusable and show their "coming soon" tooltip.
24. **Sidebar.** Collapses with the Explorer activity button; resized by dragging its right edge through a full-screen overlay rendered only during the drag (170–600 px), so pointer moves do not re-render the shell otherwise.
25. **Logging.** Application references `Microsoft.Extensions.Logging.Abstractions` for the per-session summary lines ("Sync {Repo}: manifest of N files, M to upload, K deleted" and "Sync {Repo}: stored N files, B bytes in T"); never file contents.
26. **E2E.** `Connection.feature` keeps "App connects" and checks the pill tooltip instead of the Ping button; new `Shell.feature` covers the shell, the disabled views, the theme toggle (incl. reload), collapsing the explorer and the error list. The folder picker is not automated (spec).
27. **Message size.** The hub keeps `AddSignalR()` defaults; a test asserts `MaximumReceiveMessageSize == 32 KB`.

## File map

| File | Task | Responsibility |
|---|---|---|
| `src/AiChromeProxy.Domain/ErrorCodes.cs`, `ErrorPayload.cs` (new) | 1 | Error contract |
| `src/AiChromeProxy.Application/Transport/EnvelopeContext.cs`, `EnvelopeException.cs` (new); `IEnvelopeHandler.cs`, `EnvelopeRouter.cs`, `PingHandler.cs` (modify) | 1 | Handler contract, error mapping |
| `src/AiChromeProxy.Server/Transport/TransportHub.cs`, `Security/CloudflareAccessMiddleware.cs` (modify) | 1, 7 | Context, `internal` errors, email; session close |
| `src/AiChromeProxy.Client/Transport/TransportExtensions.cs`, `RequestFailedException.cs` (new) | 1, 2 | `RequestAsync`, `ConnectForeverAsync` |
| `src/AiChromeProxy.Client/Transport/ForeverRetryPolicy.cs` (new); `src/AiChromeProxy.Client/Program.cs` (modify) | 2, 8, 10 | Reconnect forever; DI |
| `src/AiChromeProxy.Domain/Sync/SyncPath.cs`, `RepoName.cs` (new) | 3 | Path validator, repo-name sanitizer |
| `src/AiChromeProxy.Client/Sync/IgnoreRules.cs` (new) | 4 | Built-in + `.gitignore` excludes |
| `src/AiChromeProxy.Domain/MessageTypes.cs` (modify); `Domain/Sync/*Payload.cs`, `ManifestEntry.cs`, `SyncLimits.cs` (new); `src/AiChromeProxy.Client/Sync/ManifestPlanner.cs` (new) | 5 | Wire contract; manifest/delta pages |
| `src/AiChromeProxy.Application/Sync/IMirrorStore.cs`; `src/AiChromeProxy.Infrastructure/Sync/MirrorOptions.cs`, `FileSystemMirrorStore.cs` (new); `Hosting/DataDirectory.cs` (modify) | 6 | Mirror on disk |
| `src/AiChromeProxy.Application/Sync/SyncSession.cs`, `SyncSessions.cs`, `SyncHandler.cs` (new); both `DependencyInjection.cs`, Server `Program.cs`, `appsettings.json`, `.gitignore`, Application csproj (modify) | 7 | Server-side protocol, wiring |
| `src/AiChromeProxy.Client/wwwroot/js/fsaccess.js`; `Client/Sync/IFolderAccess.cs`, `FolderGrant.cs`, `FolderScan.cs`, `FileMeta.cs`, `JsFolderAccess.cs` (new) | 8 | Browser folder access |
| `src/AiChromeProxy.Client/Sync/SyncEngine.cs`, `SyncFile.cs` (new) | 9 | Browser-side sync orchestration |
| `src/AiChromeProxy.Client/Shell/*` (new); `Pages/Home.razor`, `_Imports.razor`, `wwwroot/css/app.css`, `wwwroot/index.html` (modify) | 10 | The app shell |
| `tests/AiChromeProxy.E2E/Features/*.feature`, `StepDefinitions/*.cs` | 11 | E2E for the shell |
| `docs/sync.md` (new); `README.md`, `CLAUDE.md`, `docs/testing.md` (modify) | 12 | Docs, manual checklist |

---

### Task 1: Handler context and error contract; client `RequestAsync`

**Files:**
- Create: `src/AiChromeProxy.Domain/ErrorCodes.cs`, `src/AiChromeProxy.Domain/ErrorPayload.cs`
- Create: `src/AiChromeProxy.Application/Transport/EnvelopeContext.cs`, `src/AiChromeProxy.Application/Transport/EnvelopeException.cs`
- Modify: `src/AiChromeProxy.Application/Transport/IEnvelopeHandler.cs`, `EnvelopeRouter.cs`, `PingHandler.cs`
- Modify: `src/AiChromeProxy.Server/Security/CloudflareAccessMiddleware.cs`, `src/AiChromeProxy.Server/Transport/TransportHub.cs`
- Create: `src/AiChromeProxy.Client/Transport/RequestFailedException.cs`, `src/AiChromeProxy.Client/Transport/TransportExtensions.cs`
- Modify: `benchmarks/AiChromeProxy.Benchmarks/RouterBenchmarks.cs`
- Test: `tests/AiChromeProxy.Tests/Application/EnvelopeRouterTests.cs` (rewrite), `Application/PingHandlerTests.cs`, `Server/CloudflareAccessMiddlewareTests.cs`, `Server/TransportHubTests.cs` (modify); `Client/FakeTransport.cs`, `Client/TransportExtensionsTests.cs` (new)

**Interfaces:**
- Consumes: `Envelope(string Type, JsonElement Payload, string? CorrelationId = null)` and `Envelope.Create<T>(string, T, string?)`, `MessageTypes`, `ITransport` (`StateChanged`, `Received`, `State`, `ConnectAsync`, `SendAsync`), `CloudflareAccessTokenValidator.ValidateAsync(string, CancellationToken) : Task<bool>` (existing).
- Produces:
  - `public static class ErrorCodes` — `BadRequest = "bad_request"`, `UnknownType = "unknown_type"`, `NotFound = "not_found"`, `TooLarge = "too_large"`, `Internal = "internal"`.
  - `public sealed record ErrorPayload(string Code, string? Message = null, string? Type = null)`.
  - `public sealed class EnvelopeContext(string connectionId, string? email, Func<Envelope, CancellationToken, Task> send)` — `ConnectionId`, `Email`, `Task SendAsync(Envelope, CancellationToken)`.
  - `public sealed class EnvelopeException(string code, string message) : Exception` — `Code`.
  - `IEnvelopeHandler.HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct) : Task<Envelope?>`.
  - `EnvelopeRouter.RouteAsync(Envelope request, EnvelopeContext context, CancellationToken ct) : Task<Envelope?>`; `public static Envelope EnvelopeRouter.Error(Envelope request, ErrorPayload error)`.
  - `CloudflareAccessMiddleware.EmailItem = "aicp.access.email"` (`HttpContext.Items` key).
  - `TransportHub(EnvelopeRouter router, IHubContext<TransportHub> hub, ILogger<TransportHub> logger)`; `Send(Envelope? envelope)`.
  - `public sealed class RequestFailedException(string code, string? message) : Exception` — `Code`.
  - `public static Task<Envelope> TransportExtensions.RequestAsync(this ITransport transport, Envelope request, TimeSpan timeout, CancellationToken ct = default)`.
  - Test helper `AiChromeProxy.Tests.Client.FakeTransport : ITransport` — `Sent`, `Reply : Func<Envelope, Task<Envelope?>>?`, `FailConnects`, `ConnectAttempts`, `ReceivedHandlers`, `SetState(TransportState)`, `Push(Envelope)`.

- [ ] **Step 1: Write the failing tests**

Replace `tests/AiChromeProxy.Tests/Application/EnvelopeRouterTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Application;

public sealed class EnvelopeRouterTests
{
	private static readonly EnvelopeContext Context = new("conn-1", "user@example.com", (_, _) => Task.CompletedTask);

	[Fact]
	public async Task KnownType_CallsHandlerWithContext_ReturnsReply()
	{
		var handler = new EchoHandler("echo");
		var router = new EnvelopeRouter([handler]);

		var reply = await router.RouteAsync(Envelope.Create("echo", new { v = 1 }, "c1"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("echo-reply", reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
		Assert.Same(Context, handler.LastContext);
	}

	[Fact]
	public async Task UnknownType_ReturnsErrorWithSameCorrelationId()
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(Envelope.Create("nope", new { }, "c2"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c2", reply.CorrelationId);
		Assert.Equal("unknown_type", reply.Payload.GetProperty("code").GetString());
		Assert.Equal("nope", reply.Payload.GetProperty("type").GetString());
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	public async Task MissingType_BadRequest(string? type)
	{
		var router = new EnvelopeRouter([]);

		var reply = await router.RouteAsync(new Envelope(type!, JsonSerializer.SerializeToElement(new { }), "c3"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.Error, reply.Type);
		Assert.Equal("c3", reply.CorrelationId);
		Assert.Equal(ErrorCodes.BadRequest, reply.Payload.GetProperty("code").GetString());
	}

	[Fact]
	public async Task HandlerThrowsEnvelopeException_ErrorWithCodeAndMessage()
	{
		var router = new EnvelopeRouter([new ThrowingHandler("t", new EnvelopeException(ErrorCodes.NotFound, "no such file"))]);

		var reply = await router.RouteAsync(Envelope.Create("t", new { }, "c4"), Context, TestContext.Current.CancellationToken);

		Assert.NotNull(reply);
		Assert.Equal("c4", reply.CorrelationId);
		var error = reply.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web);
		Assert.Equal(new ErrorPayload(ErrorCodes.NotFound, "no such file"), error);
	}

	[Fact]
	public async Task HandlerThrowsOtherException_Propagates()
	{
		var router = new EnvelopeRouter([new ThrowingHandler("t", new InvalidOperationException("boom"))]);

		await Assert.ThrowsAsync<InvalidOperationException>(() => router.RouteAsync(Envelope.Create("t", new { }), Context, TestContext.Current.CancellationToken));
	}

	[Fact]
	public void DuplicateHandlerType_Throws()
	{
		var ex = Assert.Throws<InvalidOperationException>(() => new EnvelopeRouter([new EchoHandler("x"), new EchoHandler("x")]));

		Assert.Contains("'x'", ex.Message);
	}

	private sealed class EchoHandler(string type) : IEnvelopeHandler
	{
		public string Type => type;

		public EnvelopeContext? LastContext { get; private set; }

		public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
		{
			LastContext = context;
			return Task.FromResult<Envelope?>(Envelope.Create(type + "-reply", new { }, request.CorrelationId));
		}
	}

	private sealed class ThrowingHandler(string type, Exception exception) : IEnvelopeHandler
	{
		public string Type => type;

		public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct) => throw exception;
	}
}
```

In `tests/AiChromeProxy.Tests/Application/PingHandlerTests.cs`, change the call to pass a context:

```csharp
		var reply = await handler.HandleAsync(Envelope.Create(MessageTypes.Ping, new { }, "p1"), new EnvelopeContext("conn-1", null, (_, _) => Task.CompletedTask), TestContext.Current.CancellationToken);
```

In `tests/AiChromeProxy.Tests/Server/CloudflareAccessMiddlewareTests.cs`, insert before `private static (CloudflareAccessTokenValidator Validator, IOptions<CloudflareAccessOptions> Options) Build(`:

```csharp
	[Fact]
	public async Task ValidToken_StoresEmailClaimForTheHub()
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: true);
		var ctx = new DefaultHttpContext();
		ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token();

		await new CloudflareAccessMiddleware(Ok, options, validator).InvokeAsync(ctx);

		Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
		Assert.Equal("user@example.com", ctx.Items[CloudflareAccessMiddleware.EmailItem]);
	}

```

Create `tests/AiChromeProxy.Tests/Client/FakeTransport.cs`:

```csharp
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="ITransport"/>: records what is sent and answers through <see cref="Reply"/>.</summary>
public sealed class FakeTransport : ITransport
{
	public event Action<TransportState>? StateChanged;

	public event Action<Envelope>? Received;

	public TransportState State { get; private set; } = TransportState.Disconnected;

	public List<Envelope> Sent { get; } = [];

	/// <summary>Answer to each sent envelope (null: no answer). Runs before <see cref="SendAsync"/> completes, like a fast server.</summary>
	public Func<Envelope, Task<Envelope?>>? Reply { get; set; }

	/// <summary>The first this many <see cref="ConnectAsync"/> calls fail.</summary>
	public int FailConnects { get; set; }

	public int ConnectAttempts { get; private set; }

	public int ReceivedHandlers => Received?.GetInvocationList().Length ?? 0;

	public Task ConnectAsync(CancellationToken ct = default)
	{
		ConnectAttempts++;
		if (ConnectAttempts <= FailConnects)
		{
			return Task.FromException(new HttpRequestException("offline"));
		}

		SetState(TransportState.Connected);
		return Task.CompletedTask;
	}

	public async Task SendAsync(Envelope envelope, CancellationToken ct = default)
	{
		Sent.Add(envelope);
		if (Reply is not null && await Reply(envelope) is { } reply)
		{
			Received?.Invoke(reply);
		}
	}

	public void SetState(TransportState state)
	{
		State = state;
		StateChanged?.Invoke(state);
	}

	public void Push(Envelope envelope) => Received?.Invoke(envelope);

	public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

Create `tests/AiChromeProxy.Tests/Client/TransportExtensionsTests.cs`:

```csharp
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Tests.Client;

public sealed class TransportExtensionsTests
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

	[Fact]
	public async Task Request_ReturnsTheReplyWithItsCorrelationId_IgnoresOthers()
	{
		var transport = new FakeTransport();
		transport.Reply = e =>
		{
			transport.Push(Envelope.Create("noise", new { }, "someone-else"));
			return Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Pong, new { }, e.CorrelationId));
		};

		var reply = await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }, "ignored"), Timeout, TestContext.Current.CancellationToken);

		Assert.Equal(MessageTypes.Pong, reply.Type);
		var sent = Assert.Single(transport.Sent);
		Assert.NotNull(sent.CorrelationId);
		Assert.NotEqual("ignored", sent.CorrelationId);
		Assert.Equal(sent.CorrelationId, reply.CorrelationId);
		Assert.Equal(0, transport.ReceivedHandlers);
	}

	[Fact]
	public async Task Request_ErrorReply_ThrowsWithCodeAndMessage()
	{
		var transport = new FakeTransport
		{
			Reply = e => Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.Error, new ErrorPayload(ErrorCodes.TooLarge, "file too large"), e.CorrelationId)),
		};

		var ex = await Assert.ThrowsAsync<RequestFailedException>(
			() => transport.RequestAsync(Envelope.Create("x", new { }), Timeout, TestContext.Current.CancellationToken));

		Assert.Equal(ErrorCodes.TooLarge, ex.Code);
		Assert.Equal("file too large", ex.Message);
		Assert.Equal(0, transport.ReceivedHandlers);
	}

	[Fact]
	public async Task Request_NoReply_TimesOut_AndUnsubscribes()
	{
		var transport = new FakeTransport();

		await Assert.ThrowsAsync<TimeoutException>(
			() => transport.RequestAsync(Envelope.Create("x", new { }), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken));

		Assert.Equal(0, transport.ReceivedHandlers);
	}
}
```

In `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs`:

1. Add the usings (keep them sorted):

```csharp
using System.Net;
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Client.Transport;
```

2. In the constructor, replace the `b.ConfigureServices(...)` call with one that also registers the probe handler:

```csharp
			b.ConfigureServices(s =>
			{
				s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler());
				s.AddSingleton<IEnvelopeHandler, ProbeHandler>();
			});
```

3. Insert these tests before `public async Task NoToken_ConnectionRejected()` (its `[Fact]` line):

```csharp
	[Fact]
	public async Task Request_Ping_ReturnsPongWithCorrelationId()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var reply = await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }), TimeSpan.FromSeconds(10), ct);

			Assert.Equal(MessageTypes.Pong, reply.Type);
		}
	}

	[Theory]
	[InlineData("fail", ErrorCodes.NotFound, "missing")]
	[InlineData("boom", ErrorCodes.Internal, ErrorCodes.Internal)]
	public async Task Request_HandlerFails_ErrorReplyWithCode_NoInternalDetails(string mode, string code, string message)
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var ex = await Assert.ThrowsAsync<RequestFailedException>(
				() => transport.RequestAsync(Envelope.Create(ProbeHandler.MessageType, new { mode }), TimeSpan.FromSeconds(10), ct));

			Assert.Equal(code, ex.Code);
			Assert.Equal(message, ex.Message);
		}
	}

	[Fact]
	public async Task Request_NullType_BadRequest()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			await transport.ConnectAsync(ct);

			var ex = await Assert.ThrowsAsync<RequestFailedException>(
				() => transport.RequestAsync(new Envelope(null!, JsonSerializer.SerializeToElement(new { })), TimeSpan.FromSeconds(10), ct));

			Assert.Equal(ErrorCodes.BadRequest, ex.Code);
		}
	}

	[Fact]
	public async Task Handler_PushesThroughContext_WithAccessEmailAndConnectionId()
	{
		var ct = TestContext.Current.CancellationToken;
		await using (var transport = new SignalRTransport(Connection(_issuer.Token())))
		{
			var pushed = new TaskCompletionSource<Envelope>();
			transport.Received += e => pushed.TrySetResult(e);
			await transport.ConnectAsync(ct);

			await transport.SendAsync(Envelope.Create(ProbeHandler.MessageType, new { mode = "push" }), ct);
			var push = await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);

			Assert.Equal("test.pushed", push.Type);
			Assert.Equal("user@example.com", push.Payload.GetProperty("email").GetString());
			Assert.False(string.IsNullOrEmpty(push.Payload.GetProperty("connectionId").GetString()));
		}
	}

```

4. Add this nested class as the last member of `TransportHubTests` (after the private `Get` method):

```csharp

	/// <summary>Exercises the error contract and the context: <c>{mode: "fail" | "boom" | "push"}</c>.</summary>
	private sealed class ProbeHandler : IEnvelopeHandler
	{
		public const string MessageType = "test.probe";

		public string Type => MessageType;

		public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
		{
			switch (request.Payload.GetProperty("mode").GetString())
			{
				case "fail":
					throw new EnvelopeException(ErrorCodes.NotFound, "missing");
				case "push":
					await context.SendAsync(Envelope.Create("test.pushed", new { email = context.Email, connectionId = context.ConnectionId }), ct);
					return null;
				default:
					throw new InvalidOperationException("secret detail");
			}
		}
	}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246`/`CS0103` for `EnvelopeContext`, `ErrorCodes`, `ErrorPayload`, `EnvelopeException`, `RequestFailedException`, `RequestAsync`, `CloudflareAccessMiddleware.EmailItem`.

- [ ] **Step 3: Domain error contract**

Create `src/AiChromeProxy.Domain/ErrorCodes.cs`:

```csharp
namespace AiChromeProxy.Domain;

/// <summary>Values of <c>code</c> in an <see cref="MessageTypes.Error"/> payload.</summary>
public static class ErrorCodes
{
	/// <summary>Malformed envelope or payload (e.g. null or empty <c>type</c>, invalid path).</summary>
	public const string BadRequest = "bad_request";

	/// <summary>No handler for the envelope's <c>type</c>; the payload carries <c>type</c>.</summary>
	public const string UnknownType = "unknown_type";

	public const string NotFound = "not_found";

	public const string TooLarge = "too_large";

	/// <summary>The handler failed unexpectedly; details are only in the server log.</summary>
	public const string Internal = "internal";
}
```

Create `src/AiChromeProxy.Domain/ErrorPayload.cs`:

```csharp
namespace AiChromeProxy.Domain;

/// <summary>Payload of <see cref="MessageTypes.Error"/>: <c>{code, message?, type?}</c>; <c>type</c> is set only for <see cref="ErrorCodes.UnknownType"/>.</summary>
public sealed record ErrorPayload(string Code, string? Message = null, string? Type = null);
```

- [ ] **Step 4: Application handler contract and router**

Create `src/AiChromeProxy.Application/Transport/EnvelopeContext.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

/// <summary>Who sent an envelope and how to reach them: connection id, Access identity and a channel for replies and pushes.</summary>
public sealed class EnvelopeContext(string connectionId, string? email, Func<Envelope, CancellationToken, Task> send)
{
	public string ConnectionId => connectionId;

	/// <summary>Email claim of the Cloudflare Access token; null when the Access check is disabled (Development).</summary>
	public string? Email => email;

	/// <summary>Sends an envelope to this connection at any time (also after the handler returned).</summary>
	public Task SendAsync(Envelope envelope, CancellationToken ct) => send(envelope, ct);
}
```

Create `src/AiChromeProxy.Application/Transport/EnvelopeException.cs`:

```csharp
namespace AiChromeProxy.Application.Transport;

/// <summary>Thrown by a handler to answer with <c>error {code, message}</c>. The message reaches the client: no secrets, no file contents.</summary>
public sealed class EnvelopeException(string code, string message) : Exception(message)
{
	/// <summary>One of <see cref="AiChromeProxy.Domain.ErrorCodes"/>.</summary>
	public string Code => code;
}
```

Replace `src/AiChromeProxy.Application/Transport/IEnvelopeHandler.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public interface IEnvelopeHandler
{
	string Type { get; }

	/// <returns>The reply (sent with the request's correlation id by the caller), or null for none.</returns>
	/// <exception cref="EnvelopeException">The request is answered with <c>error {code, message}</c>.</exception>
	Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct);
}
```

Replace `src/AiChromeProxy.Application/Transport/EnvelopeRouter.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class EnvelopeRouter
{
	private readonly Dictionary<string, IEnvelopeHandler> _handlers = new(StringComparer.Ordinal);

	public EnvelopeRouter(IEnumerable<IEnvelopeHandler> handlers)
	{
		foreach (var handler in handlers)
		{
			if (!_handlers.TryAdd(handler.Type, handler))
			{
				throw new InvalidOperationException($"Duplicate envelope handler for type '{handler.Type}'.");
			}
		}
	}

	/// <summary>The <c>error</c> reply to <paramref name="request"/>, carrying its correlation id.</summary>
	public static Envelope Error(Envelope request, ErrorPayload error) =>
		Envelope.Create(MessageTypes.Error, error, request.CorrelationId);

	/// <summary>Dispatches by <see cref="Envelope.Type"/>; expected failures become <c>error</c> replies, unexpected exceptions propagate to the hub.</summary>
	public async Task<Envelope?> RouteAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		if (string.IsNullOrEmpty(request.Type))
		{
			return Error(request, new ErrorPayload(ErrorCodes.BadRequest, "Envelope type is required."));
		}

		if (!_handlers.TryGetValue(request.Type, out var handler))
		{
			return Error(request, new ErrorPayload(ErrorCodes.UnknownType, Type: request.Type));
		}

		try
		{
			return await handler.HandleAsync(request, context, ct);
		}
		catch (EnvelopeException ex)
		{
			return Error(request, new ErrorPayload(ex.Code, ex.Message));
		}
	}
}
```

Replace `src/AiChromeProxy.Application/Transport/PingHandler.cs`:

```csharp
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Transport;

public sealed class PingHandler(TimeProvider time) : IEnvelopeHandler
{
	public string Type => MessageTypes.Ping;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var pong = Envelope.Create(MessageTypes.Pong, new { serverTime = time.GetUtcNow().ToString("O") }, request.CorrelationId);
		return Task.FromResult<Envelope?>(pong);
	}
}
```

Replace `benchmarks/AiChromeProxy.Benchmarks/RouterBenchmarks.cs` (it calls the router):

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using BenchmarkDotNet.Attributes;

namespace AiChromeProxy.Benchmarks;

[MemoryDiagnoser]
public class RouterBenchmarks
{
	private static readonly Envelope Ping = Envelope.Create(MessageTypes.Ping, new { }, "c1");
	private static readonly Envelope Unknown = Envelope.Create("unknown", new { }, "c2");
	private static readonly EnvelopeContext Context = new("bench", null, (_, _) => Task.CompletedTask);

	private readonly EnvelopeRouter _router = new([new PingHandler(TimeProvider.System)]);

	[Benchmark]
	public Task<Envelope?> RoutePingAsync() => _router.RouteAsync(Ping, Context, CancellationToken.None);

	[Benchmark]
	public Task<Envelope?> RouteUnknownAsync() => _router.RouteAsync(Unknown, Context, CancellationToken.None);
}
```

- [ ] **Step 5: Server — email claim and hub**

Replace `src/AiChromeProxy.Server/Security/CloudflareAccessMiddleware.cs`:

```csharp
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AiChromeProxy.Server.Security;

/// <summary>Rejects every request (static files, hub, WebSocket upgrade) without a valid Access JWT.</summary>
public sealed class CloudflareAccessMiddleware(
	RequestDelegate next,
	IOptions<CloudflareAccessOptions> options,
	CloudflareAccessTokenValidator validator)
{
	public const string HeaderName = "Cf-Access-Jwt-Assertion";
	public const string CookieName = "CF_Authorization";

	/// <summary><see cref="HttpContext.Items"/> key holding the validated token's <c>email</c> claim (absent when Access is disabled).</summary>
	public const string EmailItem = "aicp.access.email";

	public async Task InvokeAsync(HttpContext context)
	{
		if (!options.Value.Enabled)
		{
			await next(context);
			return;
		}

		string? token = context.Request.Headers[HeaderName];
		if (string.IsNullOrEmpty(token))
		{
			token = context.Request.Cookies[CookieName];
		}

		if (string.IsNullOrEmpty(token) || !await validator.ValidateAsync(token, context.RequestAborted))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		// Validated above: reading a claim needs no second check.
		context.Items[EmailItem] = new JsonWebToken(token).TryGetPayloadValue<string>("email", out var email) ? email : null;
		await next(context);
	}
}
```

Replace `src/AiChromeProxy.Server/Transport/TransportHub.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router, IHubContext<TransportHub> hub, ILogger<TransportHub> logger) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";

	public async Task Send(Envelope? envelope)
	{
		var request = envelope ?? new Envelope(string.Empty, default);
		var connectionId = Context.ConnectionId;
		var context = new EnvelopeContext(
			connectionId,
			Context.GetHttpContext()?.Items[CloudflareAccessMiddleware.EmailItem] as string,
			(e, ct) => hub.Clients.Client(connectionId).SendAsync(ReceiveMethod, e, ct));

		Envelope? reply;
		try
		{
			reply = await router.RouteAsync(request, context, Context.ConnectionAborted);
		}
		catch (Exception ex) when (!Context.ConnectionAborted.IsCancellationRequested)
		{
			// Details stay in the log; the client only learns that it failed.
			logger.LogError(ex, "Handler for {Type} failed", request.Type);
			reply = EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}

		if (reply is not null)
		{
			await Clients.Caller.SendAsync(ReceiveMethod, reply, Context.ConnectionAborted);
		}
	}
}
```

- [ ] **Step 6: Client `RequestAsync`**

Create `src/AiChromeProxy.Client/Transport/RequestFailedException.cs`:

```csharp
namespace AiChromeProxy.Client.Transport;

/// <summary>The server answered a request with <c>error {code, message?}</c>.</summary>
public sealed class RequestFailedException(string code, string? message) : Exception(message ?? code)
{
	/// <summary>One of <see cref="AiChromeProxy.Domain.ErrorCodes"/>.</summary>
	public string Code => code;
}
```

Create `src/AiChromeProxy.Client/Transport/TransportExtensions.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Client.Transport;

public static class TransportExtensions
{
	/// <summary>Sends <paramref name="request"/> under a fresh correlation id and waits for the reply that carries it.</summary>
	/// <exception cref="RequestFailedException">The server answered with <c>error</c>.</exception>
	/// <exception cref="TimeoutException">No reply within <paramref name="timeout"/>.</exception>
	public static async Task<Envelope> RequestAsync(this ITransport transport, Envelope request, TimeSpan timeout, CancellationToken ct = default)
	{
		var id = Guid.NewGuid().ToString("N");
		var reply = new TaskCompletionSource<Envelope>(TaskCreationOptions.RunContinuationsAsynchronously);
		void OnReceived(Envelope e)
		{
			if (e.CorrelationId == id)
			{
				reply.TrySetResult(e);
			}
		}

		transport.Received += OnReceived;
		try
		{
			await transport.SendAsync(request with { CorrelationId = id }, ct);
			var envelope = await reply.Task.WaitAsync(timeout, ct);
			if (envelope.Type == MessageTypes.Error)
			{
				var error = envelope.Payload.Deserialize<ErrorPayload>(JsonSerializerOptions.Web);
				throw new RequestFailedException(error?.Code ?? ErrorCodes.Internal, error?.Message);
			}

			return envelope;
		}
		finally
		{
			transport.Received -= OnReceived;
		}
	}
}
```

- [ ] **Step 7: Build and run the gate**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 470`, `failed: 0`, exit code 0.

- [ ] **Step 8: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Domain src/AiChromeProxy.Application/Transport src/AiChromeProxy.Server src/AiChromeProxy.Client/Transport benchmarks/AiChromeProxy.Benchmarks/RouterBenchmarks.cs tests/AiChromeProxy.Tests
git commit -m "feat: envelope context, error replies and client RequestAsync"
```


### Task 2: Reconnect forever

**Files:**
- Create: `src/AiChromeProxy.Client/Transport/ForeverRetryPolicy.cs`
- Modify: `src/AiChromeProxy.Client/Transport/TransportExtensions.cs`, `src/AiChromeProxy.Client/Program.cs`
- Test: `tests/AiChromeProxy.Tests/Client/ReconnectTests.cs` (new)

**Interfaces:**
- Consumes: `ITransport.ConnectAsync`, `FakeTransport` (`FailConnects`, `ConnectAttempts`) from Task 1; `Microsoft.AspNetCore.SignalR.Client.IRetryPolicy`, `RetryContext`.
- Produces:
  - `public sealed class ForeverRetryPolicy : IRetryPolicy` — `public static TimeSpan Delay(long previousAttempts)` (1, 2, 5, 10 s, then `Steady` = 30 s), `NextRetryDelay(RetryContext)` never null.
  - `public static Task TransportExtensions.ConnectForeverAsync(this ITransport transport, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct = default)` — the app passes `Task.Delay`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Client/ReconnectTests.cs`:

```csharp
using AiChromeProxy.Client.Transport;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Tests.Client;

public sealed class ReconnectTests
{
	[Theory]
	[InlineData(0, 1)]
	[InlineData(1, 2)]
	[InlineData(2, 5)]
	[InlineData(3, 10)]
	[InlineData(4, 30)]
	[InlineData(1000, 30)]
	public void Policy_NeverGivesUp(long previousRetries, int seconds)
	{
		var delay = new ForeverRetryPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetries, ElapsedTime = TimeSpan.FromHours(5) });

		Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
	}

	[Fact]
	public async Task ConnectForever_RetriesWithPolicyDelays_UntilConnected()
	{
		var transport = new FakeTransport { FailConnects = 5 };
		var delays = new List<TimeSpan>();

		await transport.ConnectForeverAsync(
			(d, _) =>
			{
				delays.Add(d);
				return Task.CompletedTask;
			},
			TestContext.Current.CancellationToken);

		Assert.Equal(6, transport.ConnectAttempts);
		Assert.Equal([1, 2, 5, 10, 30], delays.Select(d => (int)d.TotalSeconds));
		Assert.Equal(TransportState.Connected, transport.State);
	}

	[Fact]
	public async Task ConnectForever_Cancelled_Stops()
	{
		var transport = new FakeTransport { FailConnects = int.MaxValue };
		using (var cts = new CancellationTokenSource())
		{
			var task = transport.ConnectForeverAsync(
				async (d, ct) =>
				{
					await cts.CancelAsync();
					await Task.Delay(d, ct);
				},
				cts.Token);

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
			Assert.Equal(1, transport.ConnectAttempts);
		}
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246 ForeverRetryPolicy`, `CS1061 ConnectForeverAsync`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Client/Transport/ForeverRetryPolicy.cs`:

```csharp
using Microsoft.AspNetCore.SignalR.Client;

namespace AiChromeProxy.Client.Transport;

/// <summary>Reconnects forever: 1, 2, 5, 10 s, then every 30 s (SignalR's default policy gives up after about 42 s).</summary>
public sealed class ForeverRetryPolicy : IRetryPolicy
{
	private static readonly TimeSpan[] FirstDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

	public static TimeSpan Steady { get; } = TimeSpan.FromSeconds(30);

	/// <param name="previousAttempts">Failed attempts so far (0 before the first retry).</param>
	public static TimeSpan Delay(long previousAttempts) =>
		previousAttempts < FirstDelays.Length ? FirstDelays[previousAttempts] : Steady;

	public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delay(retryContext.PreviousRetryCount);
}
```

In `src/AiChromeProxy.Client/Transport/TransportExtensions.cs`, insert as the first member of the class (before `RequestAsync`'s doc comment):

```csharp
	/// <summary>
	/// First connect, retried with the <see cref="ForeverRetryPolicy"/> delays until it succeeds or <paramref name="ct"/> is cancelled
	/// (SignalR's automatic reconnect only covers connections that were up once).
	/// </summary>
	/// <param name="delay">Waits between attempts; <c>Task.Delay</c> in the app, a recorder in tests.</param>
	public static async Task ConnectForeverAsync(this ITransport transport, Func<TimeSpan, CancellationToken, Task> delay, CancellationToken ct = default)
	{
		for (var attempt = 0L; ; attempt++)
		{
			try
			{
				await transport.ConnectAsync(ct);
				return;
			}
			catch (Exception) when (!ct.IsCancellationRequested)
			{
				await delay(ForeverRetryPolicy.Delay(attempt), ct);
			}
		}
	}
```

In `src/AiChromeProxy.Client/Program.cs`, use the policy:

```csharp
builder.Services.AddSingleton<ITransport>(_ => new SignalRTransport(
	new HubConnectionBuilder().WithUrl(hubUrl).WithAutomaticReconnect(new ForeverRetryPolicy()).Build()));
```

(`ConnectForeverAsync` is called by the shell in Task 10.)

- [ ] **Step 4: Build and run the gate**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 478`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Client/Transport src/AiChromeProxy.Client/Program.cs tests/AiChromeProxy.Tests/Client/ReconnectTests.cs
git commit -m "feat: client reconnects forever (1, 2, 5, 10 s, then every 30 s)"
```


### Task 3: Path validator and repo name (Domain)

**Files:**
- Create: `src/AiChromeProxy.Domain/Sync/SyncPath.cs`, `src/AiChromeProxy.Domain/Sync/RepoName.cs`
- Test: `tests/AiChromeProxy.Tests/Domain/SyncPathTests.cs` (new)

**Interfaces:**
- Consumes: BCL only.
- Produces:
  - `public static class SyncPath` — `MaxLength = 260`, `TempSuffix = ".aicp-tmp"`, `static bool IsValid(string? path)`, `static string? GetError(string? path)` (null = valid; otherwise a message safe to show).
  - `public static class RepoName` — `MaxLength = 64`, `static string? Sanitize(string? folderName)` (null when empty), `static bool IsValid(string? repo)` (= already sanitized).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Domain/SyncPathTests.cs`:

```csharp
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Domain;

public sealed class SyncPathTests
{
	[Theory]
	[InlineData("a.txt")]
	[InlineData("src/App/Program.cs")]
	[InlineData(".gitignore")]
	[InlineData(".github/workflows/ci.yml")]
	[InlineData("docs/My File (1).md")]
	[InlineData("con-fig/x.txt")]
	[InlineData("CONSOLE.md")]
	[InlineData("a/b..c/d")]
	[InlineData("ünïcødé/файл.txt")]
	public void Valid(string path)
	{
		Assert.Null(SyncPath.GetError(path));
		Assert.True(SyncPath.IsValid(path));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("/etc/passwd")]
	[InlineData("a//b")]
	[InlineData("a/")]
	[InlineData("./a")]
	[InlineData("a/./b")]
	[InlineData("..")]
	[InlineData("../a")]
	[InlineData("a/../../b")]
	[InlineData("a\\b")]
	[InlineData("..\\a")]
	[InlineData("C:/Windows/win.ini")]
	[InlineData("C:")]
	[InlineData("file.txt:secret")]
	[InlineData("a*b")]
	[InlineData("a?b")]
	[InlineData("a\"b")]
	[InlineData("a<b")]
	[InlineData("a>b")]
	[InlineData("a|b")]
	[InlineData("a\tb")]
	[InlineData("a\0b")]
	[InlineData("CON")]
	[InlineData("con")]
	[InlineData("dir/NUL.txt")]
	[InlineData("aux.tar.gz")]
	[InlineData("COM1")]
	[InlineData("lpt9.log")]
	[InlineData("trailing.")]
	[InlineData("dir./a")]
	[InlineData("trailing ")]
	[InlineData("dir /a")]
	[InlineData("x.cs.aicp-tmp")]
	public void Invalid(string? path)
	{
		Assert.NotNull(SyncPath.GetError(path));
		Assert.False(SyncPath.IsValid(path));
	}

	[Fact]
	public void LongerThan260_Invalid_260_Valid()
	{
		Assert.True(SyncPath.IsValid(new string('a', 260)));
		Assert.False(SyncPath.IsValid(new string('a', 261)));
	}

	[Theory]
	[InlineData("ai-chrome-proxy", "ai-chrome-proxy")]
	[InlineData("My Repo (2)", "My_Repo__2_")]
	[InlineData("  spaced  ", "spaced")]
	[InlineData("репо", "____")]
	[InlineData(".config", ".config")]
	[InlineData("..", "__")]
	[InlineData(".", "_")]
	[InlineData("name.", "name_")]
	[InlineData("CON", "_CON")]
	[InlineData("nul.txt", "_nul_txt")]
	[InlineData("x.aicp-tmp", "_x_aicp-tmp")]
	public void RepoName_Sanitized(string folder, string expected)
	{
		var repo = RepoName.Sanitize(folder);

		Assert.Equal(expected, repo);
		Assert.True(RepoName.IsValid(repo));
		Assert.True(SyncPath.IsValid(repo));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void RepoName_Empty_Null(string? folder)
	{
		Assert.Null(RepoName.Sanitize(folder));
		Assert.False(RepoName.IsValid(folder));
	}

	[Fact]
	public void RepoName_TruncatedTo64()
	{
		Assert.Equal(new string('r', 64), RepoName.Sanitize(new string('r', 100)));
		Assert.False(RepoName.IsValid(new string('r', 65)));
		Assert.False(RepoName.IsValid("a/b"));
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234`: namespace `AiChromeProxy.Domain.Sync` does not exist.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Domain/Sync/SyncPath.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// Rules every path in the sync protocol must meet, on both ends: relative, <c>/</c>-separated, no empty / <c>.</c> / <c>..</c> segments,
/// nothing Windows cannot store or would reinterpret. The server additionally checks that the resolved path stays inside the repo folder.
/// </summary>
public static class SyncPath
{
	public const int MaxLength = 260;

	/// <summary>Suffix of the server's temporary upload files; reserved so a synced file can never collide with one.</summary>
	public const string TempSuffix = ".aicp-tmp";

	private static readonly char[] Forbidden = ['\\', ':', '*', '?', '"', '<', '>', '|'];

	private static readonly HashSet<string> ReservedNames = new(
		["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
		StringComparer.OrdinalIgnoreCase);

	public static bool IsValid(string? path) => GetError(path) is null;

	/// <returns>Null when <paramref name="path"/> is acceptable; otherwise why not (safe to show: it quotes nothing but the path).</returns>
	public static string? GetError(string? path)
	{
		if (string.IsNullOrEmpty(path))
		{
			return "Path is empty.";
		}

		if (path.Length > MaxLength)
		{
			return $"Path is longer than {MaxLength} characters.";
		}

		if (path.IndexOfAny(Forbidden) >= 0 || path.Any(char.IsControl))
		{
			return $"Path '{path}' contains a character that is not allowed (\\ : * ? \" < > | or a control character).";
		}

		if (path.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
		{
			return $"Path '{path}' uses the reserved suffix {TempSuffix}.";
		}

		foreach (var segment in path.Split('/'))
		{
			if (segment is "" or "." or "..")
			{
				return $"Path '{path}' must be relative, without empty, '.' or '..' segments.";
			}

			if (segment.EndsWith('.') || segment.EndsWith(' '))
			{
				return $"Path '{path}' has a segment ending with a dot or a space.";
			}

			var stem = segment.Split('.')[0].TrimEnd(' ');
			if (ReservedNames.Contains(stem))
			{
				return $"Path '{path}' uses the reserved Windows name '{stem}'.";
			}
		}

		return null;
	}
}
```

Create `src/AiChromeProxy.Domain/Sync/RepoName.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary>The mirror folder name for a picked folder: only <c>[A-Za-z0-9._-]</c>, at most 64 characters.</summary>
public static class RepoName
{
	public const int MaxLength = 64;

	/// <returns>The sanitized name, or null when nothing usable is left (empty input).</returns>
	public static string? Sanitize(string? folderName)
	{
		if (string.IsNullOrWhiteSpace(folderName))
		{
			return null;
		}

		var chars = folderName.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').Take(MaxLength).ToArray();
		var name = new string(chars);

		// Trailing dots vanish on Windows ("." and ".." would escape the root); a device name would open the device.
		var trimmed = name.TrimEnd('.');
		name = trimmed + new string('_', name.Length - trimmed.Length);
		return SyncPath.IsValid(name) ? name : "_" + name.Replace('.', '_')[..Math.Min(name.Length, MaxLength - 1)];
	}

	public static bool IsValid(string? repo) => repo is not null && Sanitize(repo) == repo;
}
```

- [ ] **Step 4: Run the tests, then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Domain.SyncPathTests"`
Expected: `total: 59`, `failed: 0`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 537`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Domain/Sync tests/AiChromeProxy.Tests/Domain/SyncPathTests.cs
git commit -m "feat: sync path validator and repo name sanitizer"
```


### Task 4: Exclude rules (built-in + `.gitignore`) in the Client

**Files:**
- Create: `src/AiChromeProxy.Client/Sync/IgnoreRules.cs`
- Test: `tests/AiChromeProxy.Tests/Client/IgnoreRulesTests.cs` (new)

**Interfaces:**
- Consumes: BCL (`System.Text.RegularExpressions`).
- Produces: `public sealed class IgnoreRules` — `static readonly IReadOnlyList<string> BuiltInDirectories` (`.git`, `node_modules`, `bin`, `obj`, `.vs`, `.idea` — the walk skips them), `static readonly IReadOnlyList<string> BuiltInPatterns`, `static IgnoreRules Create(string? gitignore)`, `bool IsIgnored(string path)` (file path, `/`-separated).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Client/IgnoreRulesTests.cs`:

```csharp
using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class IgnoreRulesTests
{
	[Theory]
	[InlineData(".git/config")]
	[InlineData("node_modules/react/index.js")]
	[InlineData("web/node_modules/x.js")]
	[InlineData("src/App/bin/Release/App.dll")]
	[InlineData("src/App/obj/project.assets.json")]
	[InlineData(".vs/x/y")]
	[InlineData(".idea/workspace.xml")]
	[InlineData(".env")]
	[InlineData("config/.env")]
	[InlineData(".env.local")]
	[InlineData(".ENV")]
	[InlineData("certs/site.pfx")]
	[InlineData("a.key")]
	[InlineData("tls/server.PEM")]
	[InlineData("id_rsa")]
	[InlineData("home/id_rsa.pub")]
	public void BuiltIn_Ignored(string path)
	{
		Assert.True(IgnoreRules.Create(null).IsIgnored(path));
	}

	[Theory]
	[InlineData("README.md")]
	[InlineData("src/bin.cs")]
	[InlineData("binary/x")]
	[InlineData("src/objects/a.cs")]
	[InlineData(".envrc")]
	[InlineData("environment.ts")]
	[InlineData("keys/readme.md")]
	[InlineData("a.keystore")]
	[InlineData(".gitignore")]
	[InlineData(".github/workflows/ci.yml")]
	public void BuiltIn_NotIgnored(string path)
	{
		Assert.False(IgnoreRules.Create(null).IsIgnored(path));
	}

	[Theory]
	[InlineData("*.log", "app.log", true)]
	[InlineData("*.log", "logs/deep/app.log", true)]
	[InlineData("*.log", "app.log.txt", false)]
	[InlineData("build/", "build/out.js", true)]
	[InlineData("build/", "src/build/out.js", true)]
	[InlineData("build/", "build", false)]
	[InlineData("/dist", "dist/a.js", true)]
	[InlineData("/dist", "src/dist/a.js", false)]
	[InlineData("docs/*.md", "docs/a.md", true)]
	[InlineData("docs/*.md", "docs/sub/a.md", false)]
	[InlineData("docs/*.md", "x/docs/a.md", false)]
	[InlineData("**/temp", "temp/a", true)]
	[InlineData("**/temp", "a/b/temp/c", true)]
	[InlineData("logs/**", "logs/a/b.txt", true)]
	[InlineData("logs/**", "logs", false)]
	[InlineData("a/**/b", "a/b", true)]
	[InlineData("a/**/b", "a/x/y/b", true)]
	[InlineData("a/**/b", "a/x/y/c", false)]
	[InlineData("file?.txt", "file1.txt", true)]
	[InlineData("file?.txt", "file10.txt", false)]
	[InlineData("file?.txt", "dir/file/.txt", false)]
	[InlineData("Thumbs.db", "pics/thumbs.DB", true)]
	[InlineData("a.b", "aXb", false)]
	[InlineData("# comment", "# comment", false)]
	[InlineData("   ", "x", false)]
	public void Gitignore_SinglePattern(string pattern, string path, bool ignored)
	{
		Assert.Equal(ignored, IgnoreRules.Create(pattern).IsIgnored(path));
	}

	[Fact]
	public void Gitignore_NegationAndOrder()
	{
		var rules = IgnoreRules.Create("# logs\r\n*.log\r\n!keep.log\r\n\r\n/out/*\r\n!/out/public\r\n");

		Assert.True(rules.IsIgnored("a.log"));
		Assert.False(rules.IsIgnored("keep.log"));
		Assert.False(rules.IsIgnored("sub/keep.log"));
		Assert.True(rules.IsIgnored("out/private/x.txt"));
		Assert.False(rules.IsIgnored("out/public/index.html"));
	}

	[Fact]
	public void Gitignore_ExcludedParentCannotBeReincluded()
	{
		var rules = IgnoreRules.Create("tmp/\n!tmp/keep.txt\n");

		Assert.True(rules.IsIgnored("tmp/keep.txt"));
	}

	[Fact]
	public void Gitignore_NegationCannotExposeBuiltInSecrets()
	{
		var rules = IgnoreRules.Create("!.env\n!*.pem\n!node_modules/\n");

		Assert.True(rules.IsIgnored(".env"));
		Assert.True(rules.IsIgnored("certs/a.pem"));
		Assert.True(rules.IsIgnored("node_modules/x.js"));
	}

	[Fact]
	public void BuiltInDirectories_AreAllCoveredByPatterns()
	{
		var rules = IgnoreRules.Create(null);

		Assert.All(IgnoreRules.BuiltInDirectories, d => Assert.True(rules.IsIgnored(d + "/x")));
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234`: namespace `AiChromeProxy.Client.Sync` does not exist.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Client/Sync/IgnoreRules.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace AiChromeProxy.Client.Sync;

/// <summary>
/// Which files never leave the machine: the built-in excludes (secrets, VCS and build folders) plus the root <c>.gitignore</c>
/// (common subset: <c>#</c> comments, blank lines, <c>*</c>, <c>**</c>, <c>?</c>, trailing <c>/</c>, leading <c>/</c>, <c>!</c>).
/// Matching ignores case (the source machine is usually Windows). A <c>.gitignore</c> negation cannot re-include a built-in exclude.
/// </summary>
public sealed class IgnoreRules
{
	/// <summary>Directory names the folder walk skips without descending (cheap pre-filter; <see cref="IsIgnored"/> covers them too).</summary>
	public static readonly IReadOnlyList<string> BuiltInDirectories = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

	public static readonly IReadOnlyList<string> BuiltInPatterns =
		[".git/", "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".env", ".env.*", "*.pfx", "*.key", "*.pem", "id_rsa*"];

	private static readonly IReadOnlyList<Rule> BuiltIn = Parse(BuiltInPatterns);

	private readonly IReadOnlyList<Rule> _gitignore;

	private IgnoreRules(IReadOnlyList<Rule> gitignore) => _gitignore = gitignore;

	/// <param name="gitignore">Content of the root <c>.gitignore</c>, or null when there is none.</param>
	public static IgnoreRules Create(string? gitignore) =>
		new(gitignore is null ? [] : Parse(gitignore.Split('\n')));

	/// <param name="path">A file path relative to the picked folder, <c>/</c>-separated.</param>
	/// <returns>True when the file or one of its parent directories is excluded.</returns>
	public bool IsIgnored(string path)
	{
		var segments = path.Split('/');
		for (var i = 1; i <= segments.Length; i++)
		{
			var prefix = string.Join('/', segments, 0, i);
			var isDirectory = i < segments.Length;
			if (Matches(BuiltIn, prefix, isDirectory) || Matches(_gitignore, prefix, isDirectory))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Git semantics: the last matching rule decides; a <c>!</c> rule re-includes.</summary>
	private static bool Matches(IReadOnlyList<Rule> rules, string path, bool isDirectory)
	{
		var ignored = false;
		foreach (var rule in rules)
		{
			if ((!rule.DirectoryOnly || isDirectory) && rule.Pattern.IsMatch(path))
			{
				ignored = !rule.Negate;
			}
		}

		return ignored;
	}

	private static List<Rule> Parse(IEnumerable<string> lines)
	{
		var rules = new List<Rule>();
		foreach (var raw in lines)
		{
			var line = raw.TrimEnd('\r', ' ', '\t');
			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}

			var negate = line.StartsWith('!');
			if (negate)
			{
				line = line[1..];
			}

			var directoryOnly = line.EndsWith('/');
			line = line.TrimEnd('/');

			// A slash at the start or in the middle anchors the pattern to the root; otherwise it matches at any depth.
			var anchored = line.Contains('/');
			line = line.TrimStart('/');
			if (line.Length == 0)
			{
				continue;
			}

			var regex = (anchored ? "^" : "^(?:.*/)?") + GlobToRegex(line) + "$";
			rules.Add(new Rule(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), negate, directoryOnly));
		}

		return rules;
	}

	private static string GlobToRegex(string glob)
	{
		var sb = new StringBuilder();
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*' && (i == 0 || glob[i - 1] == '/'))
			{
				if (i + 2 == glob.Length)
				{
					// Trailing "**": everything below.
					sb.Append(".*");
					i++;
					continue;
				}

				if (glob[i + 2] == '/')
				{
					// "**/": zero or more directories.
					sb.Append("(?:.*/)?");
					i += 2;
					continue;
				}
			}

			sb.Append(c switch
			{
				'*' => "[^/]*",
				'?' => "[^/]",
				_ => Regex.Escape(c.ToString()),
			});
		}

		return sb.ToString();
	}

	private sealed record Rule(Regex Pattern, bool Negate, bool DirectoryOnly);
}
```

- [ ] **Step 4: Run the tests, then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Client.IgnoreRulesTests"`
Expected: `total: 55`, `failed: 0`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 592`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Client/Sync/IgnoreRules.cs tests/AiChromeProxy.Tests/Client/IgnoreRulesTests.cs
git commit -m "feat: built-in and .gitignore exclude rules"
```


### Task 5: Sync wire contract and manifest/delta paging

**Files:**
- Modify: `src/AiChromeProxy.Domain/MessageTypes.cs`
- Create: `src/AiChromeProxy.Domain/Sync/ManifestEntry.cs`, `SyncOpenPayload.cs`, `SyncManifestPayload.cs`, `SyncNeedPayload.cs`, `SyncChunkPayload.cs`, `SyncStoredPayload.cs`, `SyncDeltaPayload.cs`, `SyncLimits.cs`
- Create: `src/AiChromeProxy.Client/Sync/ManifestPlanner.cs`
- Test: `tests/AiChromeProxy.Tests/Client/ManifestPlannerTests.cs` (new)

**Interfaces:**
- Consumes: `Envelope.Create`, `RepoName.MaxLength` (Task 3).
- Produces:
  - `MessageTypes.SyncOpen = "sync.open"`, `SyncOpened = "sync.opened"`, `SyncManifest = "sync.manifest"`, `SyncNeed = "sync.need"`, `SyncChunk = "sync.chunk"`, `SyncStored = "sync.stored"`, `SyncDelta = "sync.delta"`.
  - Records (namespace `AiChromeProxy.Domain.Sync`): `ManifestEntry(string Path, long Size, string Sha256)`, `SyncOpenPayload(string Repo)`, `SyncManifestPayload(string Repo, IReadOnlyList<ManifestEntry> Entries, bool Final)`, `SyncNeedPayload(string Repo, IReadOnlyList<string> Paths)`, `SyncChunkPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null)`, `SyncStoredPayload(string Repo, string Path)`, `SyncDeltaPayload(string Repo, IReadOnlyList<ManifestEntry> Upserts, IReadOnlyList<string> Deletes)`.
  - `SyncLimits` — `MaxFileSize = 20 MB`, `MaxFiles = 20_000`, `ChunkSize = 16 KB`, `MaxPageEntries = 500`, `MaxPageBytes = 24_000`.
  - `public static class ManifestPlanner` — `List<SyncManifestPayload> ManifestPages(string repo, IReadOnlyList<ManifestEntry> entries)`, `List<SyncDeltaPayload> DeltaPages(string repo, IReadOnlyDictionary<string, ManifestEntry> known, IReadOnlyList<ManifestEntry> current)`, `IEnumerable<IReadOnlyList<T>> Pages<T>(IEnumerable<T> items, Func<T, int> size)`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Client/ManifestPlannerTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class ManifestPlannerTests
{
	/// <summary>SignalR's default <c>MaximumReceiveMessageSize</c>.</summary>
	private const int SignalRLimit = 32 * 1024;

	private static readonly string Hash = new('a', 64);

	[Fact]
	public void Pages_SplitByCount()
	{
		var pages = ManifestPlanner.Pages(Enumerable.Range(0, 1200), _ => 1).ToList();

		Assert.Equal([500, 500, 200], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_SplitByBytes_OversizedItemAlone()
	{
		var pages = ManifestPlanner.Pages([10_000, 10_000, 10_000, 30_000, 1], i => i).ToList();

		Assert.Equal([2, 1, 1, 1], pages.Select(p => p.Count));
	}

	[Fact]
	public void Pages_Empty_None()
	{
		Assert.Empty(ManifestPlanner.Pages(Array.Empty<int>(), _ => 1));
	}

	[Fact]
	public void ManifestPages_Empty_OneFinalPage()
	{
		var page = Assert.Single(ManifestPlanner.ManifestPages("repo", []));

		Assert.True(page.Final);
		Assert.Empty(page.Entries);
		Assert.Equal("repo", page.Repo);
	}

	[Fact]
	public void ManifestPages_OnlyLastIsFinal_AllEntriesInOrder()
	{
		var entries = Enumerable.Range(0, 1000).Select(i => new ManifestEntry($"src/file{i:D4}.cs", i, Hash)).ToList();

		var pages = ManifestPlanner.ManifestPages("repo", entries);

		Assert.True(pages.Count > 1);
		Assert.All(pages[..^1], p => Assert.False(p.Final));
		Assert.True(pages[^1].Final);
		Assert.Equal(entries, pages.SelectMany(p => p.Entries));
	}

	[Fact]
	public void ManifestPages_WorstCasePaths_EveryEnvelopeUnderSignalRLimit()
	{
		// 260 non-ASCII chars: each is escaped to \uXXXX (6 bytes) in JSON.
		var entries = Enumerable.Range(0, 300).Select(i => new ManifestEntry($"{i:D3}/" + new string('ж', 256), long.MaxValue, Hash)).ToList();

		var pages = ManifestPlanner.ManifestPages(new string('r', RepoName.MaxLength), entries);

		Assert.All(pages, p => Assert.True(WireSize(Envelope.Create(MessageTypes.SyncManifest, p, Guid.NewGuid().ToString("N"))) < SignalRLimit));
		Assert.Equal(300, pages.Sum(p => p.Entries.Count));
	}

	[Fact]
	public void DeltaPages_UpsertsAddedAndChanged_DeletesRemoved()
	{
		var known = new Dictionary<string, ManifestEntry>
		{
			["same.txt"] = new("same.txt", 1, Hash),
			["changed.txt"] = new("changed.txt", 1, Hash),
			["gone.txt"] = new("gone.txt", 1, Hash),
		};
		ManifestEntry[] current = [new("same.txt", 1, Hash), new("changed.txt", 2, new string('b', 64)), new("new.txt", 3, Hash)];

		var pages = ManifestPlanner.DeltaPages("repo", known, current);

		Assert.Equal(2, pages.Count);
		Assert.Equal([current[1], current[2]], pages[0].Upserts);
		Assert.Empty(pages[0].Deletes);
		Assert.Empty(pages[1].Upserts);
		Assert.Equal(["gone.txt"], pages[1].Deletes);
	}

	[Fact]
	public void DeltaPages_NothingChanged_Empty()
	{
		var known = new Dictionary<string, ManifestEntry> { ["a"] = new("a", 1, Hash) };

		Assert.Empty(ManifestPlanner.DeltaPages("repo", known, [new("a", 1, Hash)]));
	}

	[Fact]
	public void DeltaPages_CaseOnlyRename_UpsertWithoutDelete()
	{
		var known = new Dictionary<string, ManifestEntry> { ["readme.md"] = new("readme.md", 1, Hash) };

		var page = Assert.Single(ManifestPlanner.DeltaPages("repo", known, [new("README.md", 1, Hash)]));

		Assert.Equal("README.md", Assert.Single(page.Upserts).Path);
		Assert.Empty(page.Deletes);
	}

	private static int WireSize(Envelope envelope) =>
		JsonSerializer.SerializeToUtf8Bytes(new { type = 1, target = "Send", arguments = new[] { envelope } }, JsonSerializerOptions.Web).Length;
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246` for `ManifestEntry`, `ManifestPlanner`, `SyncLimits`; `CS0117 MessageTypes.SyncManifest`.

- [ ] **Step 3: Wire contract (Domain)**

Replace `src/AiChromeProxy.Domain/MessageTypes.cs`:

```csharp
namespace AiChromeProxy.Domain;

public static class MessageTypes
{
	public const string Ping = "ping";
	public const string Pong = "pong";
	public const string Error = "error";

	/// <summary>Client → server: start a sync session for a folder (<c>Sync.SyncOpenPayload</c>); reply <see cref="SyncOpened"/>.</summary>
	public const string SyncOpen = "sync.open";

	/// <summary>Server → client: the session is open (<c>Sync.SyncOpenPayload</c> with the sanitized repo name).</summary>
	public const string SyncOpened = "sync.opened";

	/// <summary>Client → server: one page of the full manifest (<c>Sync.SyncManifestPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncManifest = "sync.manifest";

	/// <summary>Server → client: paths to upload (<c>Sync.SyncNeedPayload</c>).</summary>
	public const string SyncNeed = "sync.need";

	/// <summary>Client → server: part of a needed file (<c>Sync.SyncChunkPayload</c>); the last one is answered with <see cref="SyncStored"/>.</summary>
	public const string SyncChunk = "sync.chunk";

	/// <summary>Server → client: a file is in the mirror (<c>Sync.SyncStoredPayload</c>).</summary>
	public const string SyncStored = "sync.stored";

	/// <summary>Client → server: changes since the last manifest (<c>Sync.SyncDeltaPayload</c>); reply <see cref="SyncNeed"/>.</summary>
	public const string SyncDelta = "sync.delta";
}
```

Create `src/AiChromeProxy.Domain/Sync/ManifestEntry.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary>One synced file: <c>{path, size, sha256}</c> (SHA-256 as 64 lower-case hex characters).</summary>
public sealed record ManifestEntry(string Path, long Size, string Sha256);
```

Create `src/AiChromeProxy.Domain/Sync/SyncOpenPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.open {repo}</c>: the picked folder's name; the reply <c>sync.opened {repo}</c> carries the sanitized name used from then on.</summary>
public sealed record SyncOpenPayload(string Repo);
```

Create `src/AiChromeProxy.Domain/Sync/SyncManifestPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.manifest</c>: one page of the full manifest; the last page has <c>final: true</c>.</summary>
public sealed record SyncManifestPayload(string Repo, IReadOnlyList<ManifestEntry> Entries, bool Final);
```

Create `src/AiChromeProxy.Domain/Sync/SyncNeedPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.need</c>: the paths of a manifest page or delta whose content the server is missing.</summary>
public sealed record SyncNeedPayload(string Repo, IReadOnlyList<string> Paths);
```

Create `src/AiChromeProxy.Domain/Sync/SyncChunkPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.chunk</c>: up to <see cref="SyncLimits.ChunkSize"/> raw bytes (base64 in <c>data</c>) of a needed file, in order.</summary>
public sealed record SyncChunkPayload(string Repo, string Path, long Offset, string Data, bool Last, string? Sha256 = null);
```

Create `src/AiChromeProxy.Domain/Sync/SyncStoredPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.stored</c>: the reply to the last chunk once the file is in the mirror with the expected hash.</summary>
public sealed record SyncStoredPayload(string Repo, string Path);
```

Create `src/AiChromeProxy.Domain/Sync/SyncDeltaPayload.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary><c>sync.delta</c>: changes since the last manifest or delta; answered with <c>sync.need</c> for the upserts.</summary>
public sealed record SyncDeltaPayload(string Repo, IReadOnlyList<ManifestEntry> Upserts, IReadOnlyList<string> Deletes);
```

Create `src/AiChromeProxy.Domain/Sync/SyncLimits.cs`:

```csharp
namespace AiChromeProxy.Domain.Sync;

/// <summary>Bounds shared by the browser and the server; every message stays under SignalR's 32 KB receive limit.</summary>
public static class SyncLimits
{
	/// <summary>Larger files are not synced (shown as "too large").</summary>
	public const long MaxFileSize = 20L * 1024 * 1024;

	/// <summary>More files to sync than this (after excludes) is refused.</summary>
	public const int MaxFiles = 20_000;

	/// <summary>Raw bytes per <c>sync.chunk</c> (about 21.4 KB as base64).</summary>
	public const int ChunkSize = 16 * 1024;

	/// <summary>Entries per <c>sync.manifest</c> / <c>sync.delta</c> page.</summary>
	public const int MaxPageEntries = 500;

	/// <summary>Serialized entries per page; leaves room for the envelope under the 32 KB limit.</summary>
	public const int MaxPageBytes = 24_000;
}
```

- [ ] **Step 4: Paging and delta (Client)**

Create `src/AiChromeProxy.Client/Sync/ManifestPlanner.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Sync;

/// <summary>Turns a scan into protocol pages: the full manifest, or the delta against what the server already has.</summary>
public static class ManifestPlanner
{
	/// <summary>The full manifest in pages; always at least one page, the last one <c>final</c>.</summary>
	public static List<SyncManifestPayload> ManifestPages(string repo, IReadOnlyList<ManifestEntry> entries)
	{
		var pages = Pages(entries, EntrySize).Select(p => new SyncManifestPayload(repo, p, Final: false)).ToList();
		if (pages.Count == 0)
		{
			pages.Add(new SyncManifestPayload(repo, [], Final: true));
		}
		else
		{
			pages[^1] = pages[^1] with { Final = true };
		}

		return pages;
	}

	/// <summary>
	/// Delta pages (upserts first, then deletes) from what the server has (<paramref name="known"/>) to <paramref name="current"/>; empty when nothing changed.
	/// A path that only changed case is not deleted: the mirror is case-insensitive, so deleting it would delete the upserted file.
	/// </summary>
	public static List<SyncDeltaPayload> DeltaPages(string repo, IReadOnlyDictionary<string, ManifestEntry> known, IReadOnlyList<ManifestEntry> current)
	{
		var upserts = current.Where(e => !known.TryGetValue(e.Path, out var k) || k != e).ToList();
		var currentPaths = current.Select(e => e.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
		var deletes = known.Keys.Where(p => !currentPaths.Contains(p)).Order(StringComparer.Ordinal).ToList();
		return
		[
			.. Pages(upserts, EntrySize).Select(p => new SyncDeltaPayload(repo, p, [])),
			.. Pages(deletes, PathSize).Select(p => new SyncDeltaPayload(repo, [], p)),
		];
	}

	/// <summary>Splits into pages of at most <see cref="SyncLimits.MaxPageEntries"/> items and <see cref="SyncLimits.MaxPageBytes"/> bytes (an item bigger than that gets a page of its own).</summary>
	public static IEnumerable<IReadOnlyList<T>> Pages<T>(IEnumerable<T> items, Func<T, int> size)
	{
		var page = new List<T>();
		var bytes = 0;
		foreach (var item in items)
		{
			var itemSize = size(item);
			if (page.Count == SyncLimits.MaxPageEntries || (page.Count > 0 && bytes + itemSize > SyncLimits.MaxPageBytes))
			{
				yield return page;
				page = [];
				bytes = 0;
			}

			page.Add(item);
			bytes += itemSize;
		}

		if (page.Count > 0)
		{
			yield return page;
		}
	}

	/// <summary>Exact serialized size plus the separating comma (non-ASCII is escaped as \uXXXX, so it counts up to 6 bytes per char).</summary>
	private static int EntrySize(ManifestEntry entry) => JsonSerializer.SerializeToUtf8Bytes(entry, JsonSerializerOptions.Web).Length + 1;

	private static int PathSize(string path) => JsonSerializer.SerializeToUtf8Bytes(path, JsonSerializerOptions.Web).Length + 1;
}
```

- [ ] **Step 5: Run the tests, then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Client.ManifestPlannerTests"`
Expected: `total: 9`, `failed: 0`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 601`, `failed: 0`, exit code 0.

- [ ] **Step 6: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Domain src/AiChromeProxy.Client/Sync/ManifestPlanner.cs tests/AiChromeProxy.Tests/Client/ManifestPlannerTests.cs
git commit -m "feat: sync wire contract and manifest paging under the 32 KB limit"
```


### Task 6: Mirror store on the file system (Infrastructure)

**Files:**
- Create: `src/AiChromeProxy.Application/Sync/IMirrorStore.cs`
- Create: `src/AiChromeProxy.Infrastructure/Sync/MirrorOptions.cs`, `src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs`
- Modify: `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs`
- Test: `tests/AiChromeProxy.Tests/Infrastructure/FileSystemMirrorStoreTests.cs` (new)

**Interfaces:**
- Consumes: `SyncPath`, `RepoName` (Task 3), `EnvelopeException`, `ErrorCodes` (Task 1), `DataDirectory` (existing), test helpers `ServiceSetupSecurityTests.Junction(string link, string target)` and `TempRootCleanup.Root` (existing).
- Produces:
  - `public interface IMirrorStore` (namespace `AiChromeProxy.Application.Sync`) — `Task<string?> GetHashAsync(string repo, string path, CancellationToken ct)`, `IReadOnlyList<string> ListFiles(string repo)`, `Stream CreateTemp(string repo, string path)`, `void Commit(string repo, string path)`, `void DiscardTemp(string repo, string path)`, `void Delete(string repo, string path)`; refusals throw `EnvelopeException(ErrorCodes.BadRequest, …)`.
  - `public sealed class MirrorOptions` — `Section = "Mirror"`, `string Root`, `static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)`.
  - `public sealed class FileSystemMirrorStore(IOptions<MirrorOptions> options) : IMirrorStore`.
  - `DataDirectory.Mirror` (`<Root>\mirror`).

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Infrastructure/FileSystemMirrorStoreTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Tray;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class FileSystemMirrorStoreTests : IDisposable
{
	private const string Repo = "repo";

	private readonly string _temp = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly List<string> _links = [];
	private readonly string _root;
	private readonly string _repoRoot;
	private readonly FileSystemMirrorStore _store;

	public FileSystemMirrorStoreTests()
	{
		_root = Path.Combine(_temp, "mirror");
		_repoRoot = Path.Combine(_root, Repo);
		Directory.CreateDirectory(_repoRoot);
		_store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root }));
	}

	public void Dispose()
	{
		// A junction is removed on its own first: a recursive delete is refused on it.
		_links.ForEach(Directory.Delete);
		Directory.Delete(_temp, recursive: true);
	}

	[Fact]
	public async Task CreateTemp_Commit_FileInPlace_TempGone_HashMatches()
	{
		var content = Encoding.UTF8.GetBytes("hello");
		using (var stream = _store.CreateTemp(Repo, "src/a.txt"))
		{
			await stream.WriteAsync(content, TestContext.Current.CancellationToken);
		}

		Assert.True(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.aicp-tmp")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt")));

		_store.Commit(Repo, "src/a.txt");

		Assert.Equal(content, File.ReadAllBytes(Path.Combine(_repoRoot, "src", "a.txt")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "a.txt.aicp-tmp")));
		Assert.Equal(Sha(content), await _store.GetHashAsync(Repo, "src/a.txt", TestContext.Current.CancellationToken));
	}

	[Fact]
	public void DiscardTemp_RemovesTemp_KeepsExistingFile()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "old");
		using (var stream = _store.CreateTemp(Repo, "a.txt"))
		{
			stream.WriteByte(1);
		}

		_store.DiscardTemp(Repo, "a.txt");
		_store.DiscardTemp(Repo, "never-created.txt");

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		Assert.Equal("old", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
	}

	[Fact]
	public async Task GetHash_MissingFileOrDirectory_Null()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "dir"));

		Assert.Null(await _store.GetHashAsync(Repo, "missing.txt", TestContext.Current.CancellationToken));
		Assert.Null(await _store.GetHashAsync(Repo, "dir", TestContext.Current.CancellationToken));
		Assert.Null(await _store.GetHashAsync("other-repo", "a.txt", TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task GetHash_CachedBySizeAndWriteTime()
	{
		var ct = TestContext.Current.CancellationToken;
		var file = Path.Combine(_repoRoot, "a.txt");
		File.WriteAllText(file, "aaaa");
		var written = File.GetLastWriteTimeUtc(file);
		var first = await _store.GetHashAsync(Repo, "a.txt", ct);

		// Same size and write time: the cached hash is returned without reading the file.
		File.WriteAllText(file, "bbbb");
		File.SetLastWriteTimeUtc(file, written);
		Assert.Equal(first, await _store.GetHashAsync(Repo, "a.txt", ct));

		File.SetLastWriteTimeUtc(file, written.AddSeconds(5));
		Assert.Equal(Sha(Encoding.UTF8.GetBytes("bbbb")), await _store.GetHashAsync(Repo, "a.txt", ct));
	}

	[Fact]
	public void ListFiles_RelativeSlashPaths_IncludesHidden_SkipsTempFilesAndJunctions()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Directory.CreateDirectory(Path.Combine(_repoRoot, "src", "deep"));
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "a");
		File.WriteAllText(Path.Combine(_repoRoot, "src", "deep", "b.cs"), "b");
		File.WriteAllText(Path.Combine(_repoRoot, ".hidden"), "h");
		File.SetAttributes(Path.Combine(_repoRoot, ".hidden"), FileAttributes.Hidden);
		File.WriteAllText(Path.Combine(_repoRoot, "c.txt.aicp-tmp"), "t");
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var files = _store.ListFiles(Repo).Order(StringComparer.Ordinal).ToList();

		Assert.Equal([".hidden", "a.txt", "src/deep/b.cs"], files);
	}

	[Fact]
	public void ListFiles_NoRepoFolder_Empty()
	{
		Assert.Empty(_store.ListFiles("not-synced-yet"));
	}

	[Fact]
	public void Delete_RemovesFileAndEmptyParents_KeepsRepoFolderAndNonEmptyParents()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "a", "b", "c"));
		File.WriteAllText(Path.Combine(_repoRoot, "a", "keep.txt"), "k");
		File.WriteAllText(Path.Combine(_repoRoot, "a", "b", "c", "x.txt"), "x");

		_store.Delete(Repo, "a/b/c/x.txt");

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "a", "b")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a", "keep.txt")));

		_store.Delete(Repo, "a/keep.txt");

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "a")));
		Assert.True(Directory.Exists(_repoRoot));
	}

	[Fact]
	public void Delete_Missing_NoError()
	{
		_store.Delete(Repo, "missing/file.txt");

		Assert.True(Directory.Exists(_repoRoot));
	}

	[Fact]
	public void JunctionInsideMirror_NotFollowed_ByDeleteOrWrite()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Junction(Path.Combine(_repoRoot, "link"), outside);

		var delete = Assert.Throws<EnvelopeException>(() => _store.Delete(Repo, "link/secret.txt"));
		var write = Assert.Throws<EnvelopeException>(() => _store.CreateTemp(Repo, "link/new.txt"));

		Assert.Equal(ErrorCodes.BadRequest, delete.Code);
		Assert.Equal(ErrorCodes.BadRequest, write.Code);
		Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
		Assert.False(File.Exists(Path.Combine(outside, "new.txt.aicp-tmp")));
	}

	[Fact]
	public void RepoFolderIsJunction_Refused()
	{
		var outside = Path.Combine(_temp, "outside");
		Directory.CreateDirectory(outside);
		File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
		Junction(Path.Combine(_root, "linked"), outside);

		Assert.Empty(_store.ListFiles("linked"));
		Assert.Throws<EnvelopeException>(() => _store.Delete("linked", "secret.txt"));
		Assert.True(File.Exists(Path.Combine(outside, "secret.txt")));
	}

	[Theory]
	[InlineData(Repo, "../outside.txt")]
	[InlineData(Repo, "a\\b.txt")]
	[InlineData(Repo, "C:/Windows/win.ini")]
	[InlineData(Repo, "a.txt.aicp-tmp")]
	[InlineData("..", "a.txt")]
	[InlineData("a/b", "a.txt")]
	[InlineData("", "a.txt")]
	public void InvalidRepoOrPath_BadRequest(string repo, string path)
	{
		var ex = Assert.Throws<EnvelopeException>(() => _store.Delete(repo, path));

		Assert.Equal(ErrorCodes.BadRequest, ex.Code);
	}

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot()
	{
		var content = Path.Combine(_temp, "content");
		var dataDir = new DataDirectory(Path.Combine(_temp, "data"));

		Assert.Equal(Path.Combine(_temp, "m"), MirrorOptions.ResolveRoot(Path.Combine(_temp, "m"), dataDir, content));
		Assert.Equal(Path.Combine(content, "rel"), MirrorOptions.ResolveRoot("rel", dataDir, content));
		Assert.Equal(Path.Combine(_temp, "data", "mirror"), MirrorOptions.ResolveRoot(" ", dataDir, content));
		Assert.Equal(Path.Combine(content, "data", "mirror"), MirrorOptions.ResolveRoot(null, null, content));
	}

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private void Junction(string link, string target)
	{
		ServiceSetupSecurityTests.Junction(link, target);
		_links.Add(link);
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234`: namespace `AiChromeProxy.Infrastructure.Sync` does not exist.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Application/Sync/IMirrorStore.cs`:

```csharp
namespace AiChromeProxy.Application.Sync;

/// <summary>
/// The server's copy of a synced folder: <c>&lt;root&gt;\&lt;repo&gt;</c>. Paths are protocol paths (relative, <c>/</c>-separated);
/// an invalid path, one resolving outside the repo folder or one crossing a link or junction is refused with <see cref="Transport.EnvelopeException"/>.
/// </summary>
public interface IMirrorStore
{
	/// <summary>SHA-256 (lower-case hex) of the mirror file; null when there is no regular file. Cached per path by size and write time.</summary>
	Task<string?> GetHashAsync(string repo, string path, CancellationToken ct);

	/// <summary>Paths of the regular files in the repo folder; links and junctions (and what is behind them) are skipped.</summary>
	IReadOnlyList<string> ListFiles(string repo);

	/// <summary>Creates (or truncates) the temporary file <c>&lt;path&gt;.aicp-tmp</c> the upload is assembled in, creating parent folders.</summary>
	Stream CreateTemp(string repo, string path);

	/// <summary>Replaces the mirror file with its completed temporary file.</summary>
	void Commit(string repo, string path);

	/// <summary>Deletes the temporary file, if any.</summary>
	void DiscardTemp(string repo, string path);

	/// <summary>Deletes a regular file (never a link's target) and the folders this leaves empty, up to the repo folder.</summary>
	void Delete(string repo, string path);
}
```

In `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs`, add after the `Logs` property:

```csharp

	/// <summary>Default <c>Mirror:Root</c> when the Server runs with a data directory.</summary>
	public string Mirror => Path.Combine(Root, "mirror");
```

Create `src/AiChromeProxy.Infrastructure/Sync/MirrorOptions.cs`:

```csharp
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Sync;

/// <summary>Section <c>Mirror</c>: where synced folders are mirrored (one sub-folder per repo).</summary>
public sealed class MirrorOptions
{
	public const string Section = "Mirror";

	/// <summary>Absolute after <see cref="ResolveRoot"/>; empty in configuration means the default.</summary>
	public string Root { get; set; } = string.Empty;

	/// <summary>The configured root made absolute; by default <c>&lt;DataDir&gt;\mirror</c> (service) or <c>data\mirror</c> under the content root (dev, gitignored).</summary>
	public static string ResolveRoot(string? configured, DataDirectory? dataDir, string contentRoot)
	{
		if (!string.IsNullOrWhiteSpace(configured))
		{
			return Path.GetFullPath(configured, contentRoot);
		}

		return dataDir is not null ? dataDir.Mirror : Path.Combine(contentRoot, "data", "mirror");
	}
}
```

Create `src/AiChromeProxy.Infrastructure/Sync/FileSystemMirrorStore.cs`:

```csharp
using System.Collections.Concurrent;
using System.Security.Cryptography;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Sync;

/// <summary><see cref="IMirrorStore"/> on the local file system under <see cref="MirrorOptions.Root"/>.</summary>
public sealed class FileSystemMirrorStore(IOptions<MirrorOptions> options) : IMirrorStore
{
	private readonly string _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root));
	private readonly ConcurrentDictionary<string, CachedHash> _hashes = new(StringComparer.OrdinalIgnoreCase);

	public async Task<string?> GetHashAsync(string repo, string path, CancellationToken ct)
	{
		var file = new FileInfo(Resolve(repo, path));
		if (!file.Exists || file.Attributes.HasFlag(FileAttributes.ReparsePoint))
		{
			return null;
		}

		var key = Key(repo, path);
		if (_hashes.TryGetValue(key, out var cached) && cached.Size == file.Length && cached.Written == file.LastWriteTimeUtc)
		{
			return cached.Sha256;
		}

		string hash;
		using (var stream = file.OpenRead())
		{
			hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
		}

		_hashes[key] = new CachedHash(file.Length, file.LastWriteTimeUtc, hash);
		return hash;
	}

	public IReadOnlyList<string> ListFiles(string repo)
	{
		var repoRoot = RepoRoot(repo);
		if (!Directory.Exists(repoRoot) || IsLink(repoRoot))
		{
			return [];
		}

		// AttributesToSkip replaces the default (Hidden | System): hidden files are synced files too; links are never entered.
		var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = true };
		return Directory.EnumerateFiles(repoRoot, "*", enumeration)
			.Select(f => Path.GetRelativePath(repoRoot, f).Replace(Path.DirectorySeparatorChar, '/'))
			.Where(SyncPath.IsValid)
			.ToList();
	}

	public Stream CreateTemp(string repo, string path)
	{
		var full = Resolve(repo, path);
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		return new FileStream(full + SyncPath.TempSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
	}

	public void Commit(string repo, string path)
	{
		var full = Resolve(repo, path);
		File.Move(full + SyncPath.TempSuffix, full, overwrite: true);
		_hashes.TryRemove(Key(repo, path), out _);
	}

	public void DiscardTemp(string repo, string path) => File.Delete(Resolve(repo, path) + SyncPath.TempSuffix);

	public void Delete(string repo, string path)
	{
		var full = Resolve(repo, path);
		if (File.Exists(full) && !IsLink(full))
		{
			File.Delete(full);
		}

		_hashes.TryRemove(Key(repo, path), out _);

		var repoRoot = RepoRoot(repo);
		for (var dir = Path.GetDirectoryName(full)!;
			dir.Length > repoRoot.Length && Directory.Exists(dir) && !IsLink(dir) && !Directory.EnumerateFileSystemEntries(dir).Any();
			dir = Path.GetDirectoryName(dir)!)
		{
			Directory.Delete(dir);
		}
	}

	private static string Key(string repo, string path) => repo + "/" + path;

	private static bool IsLink(string path) => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private string RepoRoot(string repo) =>
		RepoName.IsValid(repo) ? Path.Combine(_root, repo) : throw BadRequest("Invalid repo name.");

	/// <summary>The absolute path for a protocol path: valid, inside the repo folder, and no link or junction on the way (the repo folder included).</summary>
	private string Resolve(string repo, string path)
	{
		if (SyncPath.GetError(path) is { } error)
		{
			throw BadRequest(error);
		}

		var repoRoot = RepoRoot(repo);
		var full = Path.GetFullPath(Path.Combine(repoRoot, path));
		if (!full.StartsWith(repoRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw BadRequest($"Path '{path}' resolves outside the mirror.");
		}

		for (var dir = Path.GetDirectoryName(full)!; dir.Length >= repoRoot.Length; dir = Path.GetDirectoryName(dir)!)
		{
			if (Directory.Exists(dir) && IsLink(dir))
			{
				throw BadRequest($"Path '{path}' crosses a link or junction in the mirror.");
			}
		}

		return full;
	}

	private sealed record CachedHash(long Size, DateTime Written, string Sha256);
}
```

- [ ] **Step 4: Run the tests, then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Infrastructure.FileSystemMirrorStoreTests"`
Expected: `total: 18`, `failed: 0`.

(A junction is removed with `Directory.Delete(link)` before the recursive delete of the temp folder; a recursive delete is refused on it — hence `_links` in the test class.)

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 619`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Application/Sync/IMirrorStore.cs src/AiChromeProxy.Infrastructure tests/AiChromeProxy.Tests/Infrastructure/FileSystemMirrorStoreTests.cs
git commit -m "feat: file-system mirror with atomic commit and link-safe deletes"
```


### Task 7: Server-side sync session, handlers, DI and `Mirror:Root`

**Files:**
- Modify: `src/AiChromeProxy.Application/AiChromeProxy.Application.csproj` (package `Microsoft.Extensions.Logging.Abstractions` 10.0.12)
- Create: `src/AiChromeProxy.Application/Sync/SyncSession.cs`, `SyncSessions.cs`, `SyncHandler.cs`
- Modify: `src/AiChromeProxy.Application/DependencyInjection.cs`, `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`
- Modify: `src/AiChromeProxy.Server/Transport/TransportHub.cs`, `src/AiChromeProxy.Server/Program.cs`, `src/AiChromeProxy.Server/appsettings.json`, `.gitignore`
- Test: `tests/AiChromeProxy.Tests/Application/SyncSessionTests.cs`, `tests/AiChromeProxy.Tests/Server/SyncHubTests.cs` (new); `tests/AiChromeProxy.Tests/Infrastructure/DependencyInjectionTests.cs` (modify)

**Interfaces:**
- Consumes: `IMirrorStore`, `FileSystemMirrorStore`, `MirrorOptions` (Task 6); payload records, `SyncLimits`, `MessageTypes.Sync*` (Task 5); `SyncPath`, `RepoName` (Task 3); `EnvelopeContext`, `EnvelopeException`, `EnvelopeRouter.Error`, `RequestAsync` (Task 1); test helper `ListLogger<T>` (existing, `tests/AiChromeProxy.Tests/Server/ListLogger.cs`).
- Produces:
  - `public sealed class SyncSession(IMirrorStore store, ILogger logger, TimeProvider time) : IDisposable` — `Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)` for `sync.open` / `sync.manifest` / `sync.delta` / `sync.chunk`.
  - `public sealed class SyncSessions(IMirrorStore store, ILogger<SyncSession> logger, TimeProvider time)` — `SyncSession Get(string connectionId)`, `void Close(string connectionId)`.
  - `public sealed class SyncHandler(string type, SyncSessions sessions) : IEnvelopeHandler` — `static readonly IReadOnlyList<string> Types`.
  - `AddApplication()` registers `SyncSessions` and one `SyncHandler` per type; `AddInfrastructure()` binds `MirrorOptions` (section `Mirror`) and registers `IMirrorStore` → `FileSystemMirrorStore`.
  - `TransportHub(EnvelopeRouter router, SyncSessions syncSessions, IHubContext<TransportHub> hub, ILogger<TransportHub> logger)`; `OnDisconnectedAsync` closes the connection's session.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Application/SyncSessionTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary>The server side of the protocol against a real mirror in a temp folder.</summary>
public sealed class SyncSessionTests : IDisposable
{
	private const string Repo = "repo";

	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly string _repoRoot;
	private readonly ListLogger<SyncSession> _logger = new();
	private readonly SyncSession _session;

	public SyncSessionTests()
	{
		_repoRoot = Path.Combine(_root, Repo);
		Directory.CreateDirectory(_repoRoot);
		_session = new SyncSession(new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })), _logger, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose()
	{
		_session.Dispose();
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public async Task Open_SanitizesRepo_RepliesOpenedWithCorrelationId()
	{
		var reply = await _session.HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("My Repo"), "c1"), Ct);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.SyncOpened, reply.Type);
		Assert.Equal("c1", reply.CorrelationId);
		Assert.Equal("My_Repo", Read<SyncOpenPayload>(reply).Repo);
	}

	[Theory]
	[InlineData(null)]
	[InlineData("  ")]
	public async Task Open_NoName_BadRequest(string? name)
	{
		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(name!)));
	}

	[Fact]
	public async Task ManifestBeforeOpen_Or_OtherRepo_BadRequest()
	{
		await AssertError(ErrorCodes.BadRequest, Manifest(final: true));
		await OpenAsync();
		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload("other", [], true)));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("\"text\"")]
	[InlineData("{\"repo\":\"repo\",\"entries\":\"x\"}")]
	public async Task MalformedPayload_BadRequest(string json)
	{
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, new Envelope(MessageTypes.SyncManifest, JsonDocument.Parse(json).RootElement.Clone()));
	}

	[Fact]
	public async Task Manifest_NeedsMissingAndChanged_NotUnchanged()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "same.txt"), "same");
		File.WriteAllText(Path.Combine(_repoRoot, "changed.txt"), "old");
		await OpenAsync();

		var need = await NeedAsync(Manifest(true, Entry("same.txt", "same"), Entry("changed.txt", "new"), Entry("new/file.txt", "x")));

		Assert.Equal(["changed.txt", "new/file.txt"], need);
	}

	[Fact]
	public async Task FinalPage_DeletesFilesNotInAnyPage_IgnoringCase()
	{
		Directory.CreateDirectory(Path.Combine(_repoRoot, "old"));
		File.WriteAllText(Path.Combine(_repoRoot, "old", "gone.txt"), "g");
		File.WriteAllText(Path.Combine(_repoRoot, "page1.txt"), "1");
		File.WriteAllText(Path.Combine(_repoRoot, "Readme.md"), "r");
		await OpenAsync();

		Assert.Empty(await NeedAsync(Manifest(false, Entry("page1.txt", "1"))));
		Assert.Empty(await NeedAsync(Manifest(true, Entry("README.md", "r"))));

		Assert.False(Directory.Exists(Path.Combine(_repoRoot, "old")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "page1.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "Readme.md")));
		Assert.Contains(_logger.Messages, m => m.Contains("Sync repo: manifest of 2 files, 0 to upload, 1 deleted", StringComparison.Ordinal));
	}

	[Theory]
	[InlineData("../escape.txt", 1, ErrorCodes.BadRequest)]
	[InlineData("ok.txt", -1, ErrorCodes.BadRequest)]
	[InlineData("ok.txt", SyncLimits.MaxFileSize + 1, ErrorCodes.TooLarge)]
	public async Task Manifest_InvalidEntry_Refused(string path, long size, string code)
	{
		await OpenAsync();

		await AssertError(code, Manifest(true, new ManifestEntry(path, size, Sha("x"))));
	}

	[Theory]
	[InlineData("")]
	[InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
	[InlineData("abc")]
	public async Task Manifest_BadHash_BadRequest(string sha)
	{
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Manifest(true, new ManifestEntry("a.txt", 1, sha)));
	}

	[Fact]
	public async Task Manifest_MoreThanMaxFiles_TooLarge()
	{
		await OpenAsync();
		var entries = Enumerable.Range(0, SyncLimits.MaxFiles + 1).Select(i => new ManifestEntry($"f{i}", 0, Sha(string.Empty))).ToArray();

		await AssertError(ErrorCodes.TooLarge, Manifest(true, entries));
	}

	[Fact]
	public async Task Chunks_InOrder_FileStoredWithHash_StoredReplyOnLastOnly()
	{
		var content = RandomNumberGenerator.GetBytes((SyncLimits.ChunkSize * 2) + 100);
		await OpenAsync();
		await NeedAsync(Manifest(true, new ManifestEntry("src/big.bin", content.Length, Sha(content))));

		var replies = new List<Envelope?>();
		for (var offset = 0; offset < content.Length; offset += SyncLimits.ChunkSize)
		{
			var length = Math.Min(SyncLimits.ChunkSize, content.Length - offset);
			var last = offset + length == content.Length;
			replies.Add(await _session.HandleAsync(Chunk("src/big.bin", offset, content.AsSpan(offset, length).ToArray(), last, last ? Sha(content) : null), Ct));
		}

		Assert.Null(replies[0]);
		Assert.Null(replies[1]);
		Assert.Equal(MessageTypes.SyncStored, replies[2]!.Type);
		Assert.Equal(new SyncStoredPayload(Repo, "src/big.bin"), Read<SyncStoredPayload>(replies[2]!));
		Assert.Equal(content, File.ReadAllBytes(Path.Combine(_repoRoot, "src", "big.bin")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "src", "big.bin.aicp-tmp")));
		Assert.Contains(_logger.Messages, m => m.StartsWith($"Sync repo: stored 1 files, {content.Length} bytes in ", StringComparison.Ordinal));
	}

	[Fact]
	public async Task EmptyFile_OneEmptyLastChunk()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("empty.txt", string.Empty)));

		var reply = await _session.HandleAsync(Chunk("empty.txt", 0, [], last: true), Ct);

		Assert.Equal(MessageTypes.SyncStored, reply!.Type);
		Assert.Empty(File.ReadAllBytes(Path.Combine(_repoRoot, "empty.txt")));
	}

	[Fact]
	public async Task Chunk_NotNeeded_NotFound_AlreadyStored_NotFound()
	{
		await OpenAsync();
		await AssertError(ErrorCodes.NotFound, Chunk("never-asked.txt", 0, [1], last: true));

		await NeedAsync(Manifest(true, Entry("a.txt", "a")));
		await _session.HandleAsync(Chunk("a.txt", 0, "a"u8.ToArray(), last: true), Ct);

		await AssertError(ErrorCodes.NotFound, Chunk("a.txt", 0, "a"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_OutOfOrder_BadRequest_TempRemoved()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 3, "d"u8.ToArray(), last: true));

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 2, "cd"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_HashMismatch_BadRequest_OldFileKept_TempRemoved()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "a.txt"), "old");
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "new")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "bad"u8.ToArray(), last: true));

		Assert.Equal("old", File.ReadAllText(Path.Combine(_repoRoot, "a.txt")));
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task Chunk_ClaimedHashDiffers_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abc")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "abc"u8.ToArray(), last: true, sha256: Sha("zzz")));
	}

	[Fact]
	public async Task Chunk_ShorterThanManifestSize_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abc")));

		await AssertError(ErrorCodes.BadRequest, Chunk("a.txt", 0, "ab"u8.ToArray(), last: true));
	}

	[Fact]
	public async Task Chunk_MoreBytesThanManifestSize_TooLarge_TempRemoved()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "ab")));

		await AssertError(ErrorCodes.TooLarge, Chunk("a.txt", 0, "abc"u8.ToArray(), last: false));

		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task Chunk_LargerThanChunkSize_TooLarge()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, new ManifestEntry("a.bin", SyncLimits.MaxFileSize, Sha("x"))));

		await AssertError(ErrorCodes.TooLarge, Chunk("a.bin", 0, new byte[SyncLimits.ChunkSize + 1], last: false));
	}

	[Fact]
	public async Task Chunk_NotBase64_BadRequest()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "a")));

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, "a.txt", 0, "%%%", true)));
	}

	[Fact]
	public async Task Delta_UpsertsNeeded_DeletesApplied()
	{
		File.WriteAllText(Path.Combine(_repoRoot, "keep.txt"), "k");
		File.WriteAllText(Path.Combine(_repoRoot, "gone.txt"), "g");
		await OpenAsync();

		var reply = await _session.HandleAsync(Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [Entry("keep.txt", "k"), Entry("new.txt", "n")], ["gone.txt"])), Ct);

		Assert.Equal(["new.txt"], Read<SyncNeedPayload>(reply!).Paths);
		Assert.False(File.Exists(Path.Combine(_repoRoot, "gone.txt")));
		Assert.True(File.Exists(Path.Combine(_repoRoot, "keep.txt")));
	}

	[Fact]
	public async Task Delta_InvalidDeletePath_BadRequest()
	{
		await OpenAsync();

		await AssertError(ErrorCodes.BadRequest, Envelope.Create(MessageTypes.SyncDelta, new SyncDeltaPayload(Repo, [], ["../outside.txt"])));
	}

	[Fact]
	public async Task ReopenOrDispose_DiscardsUnfinishedUpload()
	{
		await OpenAsync();
		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);
		Assert.True(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));

		await OpenAsync();
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));

		await NeedAsync(Manifest(true, Entry("a.txt", "abcd")));
		await _session.HandleAsync(Chunk("a.txt", 0, "ab"u8.ToArray(), last: false), Ct);
		_session.Dispose();
		Assert.False(File.Exists(Path.Combine(_repoRoot, "a.txt.aicp-tmp")));
	}

	[Fact]
	public async Task NotASyncType_UnknownType()
	{
		await AssertError(ErrorCodes.UnknownType, Envelope.Create(MessageTypes.Ping, new { }));
	}

	private static string Sha(string text) => Sha(Encoding.UTF8.GetBytes(text));

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private static ManifestEntry Entry(string path, string content) => new(path, Encoding.UTF8.GetByteCount(content), Sha(content));

	private static Envelope Manifest(bool final, params ManifestEntry[] entries) =>
		Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(Repo, entries, final));

	private static Envelope Chunk(string path, long offset, byte[] data, bool last, string? sha256 = null) =>
		Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(Repo, path, offset, Convert.ToBase64String(data), last, sha256));

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private async Task OpenAsync() => await _session.HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload(Repo)), Ct);

	private async Task<IReadOnlyList<string>> NeedAsync(Envelope manifest)
	{
		var reply = await _session.HandleAsync(manifest, Ct);
		Assert.Equal(MessageTypes.SyncNeed, reply!.Type);
		return Read<SyncNeedPayload>(reply).Paths;
	}

	private async Task AssertError(string code, Envelope request)
	{
		var ex = await Assert.ThrowsAsync<EnvelopeException>(() => _session.HandleAsync(request, Ct));
		Assert.Equal(code, ex.Code);
	}
}
```

Create `tests/AiChromeProxy.Tests/Server/SyncHubTests.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Security;
using AiChromeProxy.Server.Transport;
using AiChromeProxy.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Server;

/// <summary>The real Server over SignalR: one small repo goes open → manifest → need → chunks → stored, and lands in a temp mirror.</summary>
public sealed class SyncHubTests : IAsyncDisposable
{
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

	private readonly TestAccessIssuer _issuer = new();
	private readonly string _mirror = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly WebApplicationFactory<Program> _factory;

	public SyncHubTests()
	{
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Production);
			b.UseSetting("Server:PublicHost", ServerHostingTests.PublicHost);
			b.UseSetting("CloudflareAccess:TeamDomain", TestAccessIssuer.TeamDomain);
			b.UseSetting("CloudflareAccess:Audience", TestAccessIssuer.Audience);
			b.UseSetting("Mirror:Root", _mirror);
			b.ConfigureServices(s => s.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient)
				.ConfigurePrimaryHttpMessageHandler(() => _issuer.Handler()));
		});
	}

	[Fact]
	public void HubKeepsSignalRDefaultMessageLimit()
	{
		var options = _factory.Services.GetRequiredService<IOptions<HubOptions>>().Value;

		Assert.Equal(32 * 1024, options.MaximumReceiveMessageSize);
	}

	[Fact]
	public async Task SmallRepo_OpenManifestChunks_FilesInMirror_StaleFileDeleted()
	{
		var ct = TestContext.Current.CancellationToken;
		var repoRoot = Path.Combine(_mirror, "My_Repo");
		Directory.CreateDirectory(repoRoot);
		File.WriteAllText(Path.Combine(repoRoot, "stale.txt"), "old");
		File.WriteAllText(Path.Combine(repoRoot, "same.txt"), "same");
		var big = RandomNumberGenerator.GetBytes(SyncLimits.ChunkSize + 10);
		var files = new Dictionary<string, byte[]>
		{
			["same.txt"] = Encoding.UTF8.GetBytes("same"),
			["src/app.cs"] = Encoding.UTF8.GetBytes("class App { }"),
			["assets/big.bin"] = big,
		};

		await using (var transport = new SignalRTransport(Connection()))
		{
			await transport.ConnectAsync(ct);

			var opened = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("My Repo")), Timeout, ct);
			var repo = Read<SyncOpenPayload>(opened).Repo;
			var entries = files.Select(f => new ManifestEntry(f.Key, f.Value.Length, Sha(f.Value))).ToList();
			var need = await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload(repo, entries, Final: true)), Timeout, ct);
			var paths = Read<SyncNeedPayload>(need).Paths;

			Assert.Equal("My_Repo", repo);
			Assert.Equal(["src/app.cs", "assets/big.bin"], paths);
			foreach (var path in paths)
			{
				var content = files[path];
				for (var offset = 0; offset < content.Length || offset == 0; offset += SyncLimits.ChunkSize)
				{
					var length = Math.Min(SyncLimits.ChunkSize, content.Length - offset);
					var last = offset + length == content.Length;
					var chunk = Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload(repo, path, offset, Convert.ToBase64String(content, offset, length), last, last ? Sha(content) : null));
					if (!last)
					{
						await transport.SendAsync(chunk, ct);
						continue;
					}

					var stored = await transport.RequestAsync(chunk, Timeout, ct);
					Assert.Equal(new SyncStoredPayload(repo, path), Read<SyncStoredPayload>(stored));
					break;
				}
			}
		}

		Assert.False(File.Exists(Path.Combine(repoRoot, "stale.txt")));
		Assert.Equal("same", File.ReadAllText(Path.Combine(repoRoot, "same.txt")));
		Assert.Equal("class App { }", File.ReadAllText(Path.Combine(repoRoot, "src", "app.cs")));
		Assert.Equal(big, File.ReadAllBytes(Path.Combine(repoRoot, "assets", "big.bin")));
	}

	[Fact]
	public async Task Disconnect_DropsUnfinishedUpload()
	{
		var ct = TestContext.Current.CancellationToken;
		var content = new byte[SyncLimits.ChunkSize * 2];
		await using (var transport = new SignalRTransport(Connection()))
		{
			await transport.ConnectAsync(ct);
			await transport.RequestAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("r")), Timeout, ct);
			await transport.RequestAsync(Envelope.Create(MessageTypes.SyncManifest, new SyncManifestPayload("r", [new("a.bin", content.Length, Sha(content))], true)), Timeout, ct);
			await transport.SendAsync(Envelope.Create(MessageTypes.SyncChunk, new SyncChunkPayload("r", "a.bin", 0, Convert.ToBase64String(content, 0, SyncLimits.ChunkSize), false)), ct);

			// A request after the chunk: once it is answered, the chunk (same connection, in order) has been written.
			await transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }), Timeout, ct);
			Assert.True(File.Exists(Path.Combine(_mirror, "r", "a.bin.aicp-tmp")));
		}

		await WaitUntilAsync(() => !File.Exists(Path.Combine(_mirror, "r", "a.bin.aicp-tmp")), ct);
	}

	public async ValueTask DisposeAsync()
	{
		await _factory.DisposeAsync();
		if (Directory.Exists(_mirror))
		{
			Directory.Delete(_mirror, recursive: true);
		}
	}

	private static string Sha(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
	{
		for (var i = 0; i < 100 && !condition(); i++)
		{
			await Task.Delay(50, ct);
		}

		Assert.True(condition());
	}

	private HubConnection Connection()
	{
		var server = _factory.Server;
		return new HubConnectionBuilder()
			.WithUrl(new Uri(server.BaseAddress, TransportHub.Path), o =>
			{
				o.Transports = HttpTransportType.WebSockets;
				o.SkipNegotiation = true;
				o.WebSocketFactory = async (ctx, ct) =>
				{
					var ws = server.CreateWebSocketClient();
					ws.ConfigureRequest = r => r.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token();
					return await ws.ConnectAsync(ctx.Uri, ct);
				};
			})
			.Build();
	}
}
```

Replace `tests/AiChromeProxy.Tests/Infrastructure/DependencyInjectionTests.cs` (logging comes from the host; the router now needs the mirror):

```csharp
using AiChromeProxy.Application;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Infrastructure;
using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class DependencyInjectionTests
{
	private static readonly ServiceProviderOptions Strict = new() { ValidateOnBuild = true, ValidateScopes = true };

	[Fact]
	public void AddInfrastructure_AloneIsSelfSufficient()
	{
		using (var provider = new ServiceCollection().AddInfrastructure(Config()).BuildServiceProvider(Strict))
		{
			Assert.NotNull(provider.GetRequiredService<CloudflareAccessTokenValidator>());
		}
	}

	[Fact]
	public void AddApplication_ThenAddInfrastructure_BuildsWithValidation()
	{
		// The host provides logging.
		using (var provider = new ServiceCollection().AddLogging().AddApplication().AddInfrastructure(Config()).BuildServiceProvider(Strict))
		{
			Assert.NotNull(provider.GetRequiredService<CloudflareAccessTokenValidator>());
			Assert.NotNull(provider.GetRequiredService<EnvelopeRouter>());
		}
	}

	private static IConfiguration Config() => new ConfigurationBuilder()
		.AddInMemoryCollection(new Dictionary<string, string?>
		{
			["CloudflareAccess:TeamDomain"] = "team.cloudflareaccess.com",
			["CloudflareAccess:Audience"] = "aud",
			["Mirror:Root"] = Path.Combine(TempRootCleanup.Root, "di-mirror"),
		})
		.Build();
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246 SyncSession`.

- [ ] **Step 3: Logging abstractions for Application**

In `src/AiChromeProxy.Application/AiChromeProxy.Application.csproj`, add next to the existing package reference:

```xml
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="10.0.12" />
```

- [ ] **Step 4: Session, registry, handler**

Create `src/AiChromeProxy.Application/Sync/SyncSession.cs`:

```csharp
using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>
/// One connection's sync state: the open repo, the paths of the manifest being received, the files requested with
/// <c>sync.need</c> and the upload in progress. SignalR runs one hub invocation per connection at a time, so a session is never
/// used concurrently; uploads are sequential (ponytail: one upload at a time, parallelise if first syncs of large repos are too slow).
/// </summary>
public sealed class SyncSession(IMirrorStore store, ILogger logger, TimeProvider time) : IDisposable
{
	private readonly HashSet<string> _manifestPaths = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, ManifestEntry> _expected = new(StringComparer.Ordinal);
	private string? _repo;
	private Upload? _upload;
	private int _storedFiles;
	private long _storedBytes;
	private long _started;

	public async Task<Envelope?> HandleAsync(Envelope request, CancellationToken ct)
	{
		switch (request.Type)
		{
			case MessageTypes.SyncOpen:
				return Open(Read<SyncOpenPayload>(request), request);
			case MessageTypes.SyncManifest:
				return await ManifestAsync(Read<SyncManifestPayload>(request), request, ct);
			case MessageTypes.SyncDelta:
				return await DeltaAsync(Read<SyncDeltaPayload>(request), request, ct);
			case MessageTypes.SyncChunk:
				try
				{
					return await ChunkAsync(Read<SyncChunkPayload>(request), request, ct);
				}
				catch
				{
					DiscardUpload();
					throw;
				}

			default:
				throw new EnvelopeException(ErrorCodes.UnknownType, $"Not a sync message: {request.Type}");
		}
	}

	public void Dispose() => DiscardUpload();

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

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

	private static Envelope Reply<T>(string type, T payload, Envelope request) => Envelope.Create(type, payload, request.CorrelationId);

	private static void Validate(ManifestEntry? entry)
	{
		if (entry is null)
		{
			throw BadRequest("Manifest entry is null.");
		}

		if (SyncPath.GetError(entry.Path) is { } error)
		{
			throw BadRequest(error);
		}

		if (entry.Size < 0)
		{
			throw BadRequest($"'{entry.Path}' has a negative size.");
		}

		if (entry.Size > SyncLimits.MaxFileSize)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{entry.Path}' is larger than {SyncLimits.MaxFileSize} bytes.");
		}

		if (entry.Sha256 is not { Length: 64 } || !entry.Sha256.All(char.IsAsciiHexDigitLower))
		{
			throw BadRequest($"'{entry.Path}' needs a SHA-256 as 64 lower-case hex characters.");
		}
	}

	private Envelope Open(SyncOpenPayload payload, Envelope request)
	{
		var repo = RepoName.Sanitize(payload.Repo) ?? throw BadRequest("sync.open needs the folder name in 'repo'.");
		DiscardUpload();
		_manifestPaths.Clear();
		_expected.Clear();
		_repo = repo;
		ResetStats();
		return Reply(MessageTypes.SyncOpened, new SyncOpenPayload(repo), request);
	}

	private async Task<Envelope> ManifestAsync(SyncManifestPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var entries = payload.Entries ?? throw BadRequest("sync.manifest needs 'entries'.");
		if (_manifestPaths.Count + entries.Count > SyncLimits.MaxFiles)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"A manifest may list at most {SyncLimits.MaxFiles} files.");
		}

		var need = await NeedAsync(repo, entries, ct);
		_manifestPaths.UnionWith(entries.Select(e => e.Path));
		if (payload.Final)
		{
			// Case-insensitive: the mirror is on Windows, where "Readme.md" on disk is the manifest's "README.md".
			var stale = store.ListFiles(repo).Where(p => !_manifestPaths.Contains(p)).ToList();
			stale.ForEach(p => store.Delete(repo, p));
			logger.LogInformation(
				"Sync {Repo}: manifest of {Files} files, {Need} to upload, {Deleted} deleted",
				repo,
				_manifestPaths.Count,
				_expected.Count,
				stale.Count);
			_manifestPaths.Clear();
		}

		return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
	}

	private async Task<Envelope> DeltaAsync(SyncDeltaPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var upserts = payload.Upserts ?? throw BadRequest("sync.delta needs 'upserts'.");
		var deletes = payload.Deletes ?? throw BadRequest("sync.delta needs 'deletes'.");
		var need = await NeedAsync(repo, upserts, ct);
		foreach (var path in deletes)
		{
			store.Delete(repo, path);
			_expected.Remove(path);
		}

		return Reply(MessageTypes.SyncNeed, new SyncNeedPayload(repo, need), request);
	}

	/// <summary>Validates the entries and returns the paths whose mirror content differs; those are expected as chunks.</summary>
	private async Task<List<string>> NeedAsync(string repo, IReadOnlyList<ManifestEntry> entries, CancellationToken ct)
	{
		entries.ToList().ForEach(Validate);
		var need = new List<string>();
		foreach (var entry in entries)
		{
			if (await store.GetHashAsync(repo, entry.Path, ct) == entry.Sha256)
			{
				_expected.Remove(entry.Path);
				continue;
			}

			_expected[entry.Path] = entry;
			need.Add(entry.Path);
		}

		return need;
	}

	private async Task<Envelope?> ChunkAsync(SyncChunkPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		if (payload.Path is null || !_expected.TryGetValue(payload.Path, out var entry))
		{
			throw new EnvelopeException(ErrorCodes.NotFound, $"'{payload.Path}' was not requested with sync.need (or is already stored).");
		}

		byte[] data;
		try
		{
			data = Convert.FromBase64String(payload.Data ?? string.Empty);
		}
		catch (FormatException)
		{
			throw BadRequest($"Chunk of '{entry.Path}' is not base64.");
		}

		if (data.Length > SyncLimits.ChunkSize)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"Chunks carry at most {SyncLimits.ChunkSize} bytes.");
		}

		if (payload.Offset == 0)
		{
			DiscardUpload();
			_upload = new Upload(entry, store.CreateTemp(repo, entry.Path));
		}
		else if (_upload is null || _upload.Entry.Path != entry.Path || _upload.Received != payload.Offset)
		{
			throw BadRequest($"Chunk of '{entry.Path}' at offset {payload.Offset} is out of order.");
		}

		var upload = _upload!;
		if (upload.Received + data.Length > entry.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{entry.Path}' is longer than its manifest size {entry.Size}.");
		}

		await upload.Stream.WriteAsync(data, ct);
		upload.Hash.AppendData(data);
		upload.Received += data.Length;
		if (!payload.Last)
		{
			return null;
		}

		var hash = Convert.ToHexStringLower(upload.Hash.GetHashAndReset());
		await upload.Stream.DisposeAsync();
		if (upload.Received != entry.Size || hash != entry.Sha256 || (payload.Sha256 is not null && payload.Sha256 != hash))
		{
			throw BadRequest($"'{entry.Path}' arrived with a different size or hash than in its manifest; it is requested again on the next scan.");
		}

		store.Commit(repo, entry.Path);
		upload.Dispose();
		_upload = null;
		_expected.Remove(entry.Path);
		_storedFiles++;
		_storedBytes += entry.Size;
		if (_expected.Count == 0)
		{
			logger.LogInformation(
				"Sync {Repo}: stored {Files} files, {Bytes} bytes in {Duration}",
				repo,
				_storedFiles,
				_storedBytes,
				time.GetElapsedTime(_started));
			ResetStats();
		}

		return Reply(MessageTypes.SyncStored, new SyncStoredPayload(repo, entry.Path), request);
	}

	private string CheckRepo(string? repo)
	{
		if (_repo is null)
		{
			throw BadRequest("Send sync.open first.");
		}

		return repo == _repo ? _repo : throw BadRequest($"Repo '{repo}' is not the open one ('{_repo}').");
	}

	private void ResetStats()
	{
		_storedFiles = 0;
		_storedBytes = 0;
		_started = time.GetTimestamp();
	}

	private void DiscardUpload()
	{
		if (_upload is null)
		{
			return;
		}

		_upload.Dispose();
		store.DiscardTemp(_repo!, _upload.Entry.Path);
		_upload = null;
	}

	private sealed class Upload(ManifestEntry entry, Stream stream) : IDisposable
	{
		public ManifestEntry Entry => entry;

		public Stream Stream => stream;

		public IncrementalHash Hash { get; } = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

		public long Received { get; set; }

		public void Dispose()
		{
			stream.Dispose();
			Hash.Dispose();
		}
	}
}
```

Create `src/AiChromeProxy.Application/Sync/SyncSessions.cs`:

```csharp
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>Sync sessions by connection id; the hub closes a connection's session when it disconnects.</summary>
public sealed class SyncSessions(IMirrorStore store, ILogger<SyncSession> logger, TimeProvider time)
{
	private readonly ConcurrentDictionary<string, SyncSession> _sessions = new(StringComparer.Ordinal);

	public SyncSession Get(string connectionId) => _sessions.GetOrAdd(connectionId, _ => new SyncSession(store, logger, time));

	/// <summary>Drops the session and its unfinished upload (temporary file deleted).</summary>
	public void Close(string connectionId)
	{
		if (_sessions.TryRemove(connectionId, out var session))
		{
			session.Dispose();
		}
	}
}
```

Create `src/AiChromeProxy.Application/Sync/SyncHandler.cs`:

```csharp
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;

namespace AiChromeProxy.Application.Sync;

/// <summary>Routes one sync message type to the sender's <see cref="SyncSession"/>; registered once per type in <see cref="Types"/>.</summary>
public sealed class SyncHandler(string type, SyncSessions sessions) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncDelta, MessageTypes.SyncChunk];

	public string Type => type;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct) =>
		sessions.Get(context.ConnectionId).HandleAsync(request, ct);
}
```

- [ ] **Step 5: Wiring**

Replace `src/AiChromeProxy.Application/DependencyInjection.cs`:

```csharp
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Application;

public static class DependencyInjection
{
	/// <summary>Registers the envelope router and its handlers (singletons). Needs an <see cref="IMirrorStore"/> (Infrastructure) and logging (the host).</summary>
	public static IServiceCollection AddApplication(this IServiceCollection services)
	{
		services.TryAddSingleton(TimeProvider.System);
		services.AddSingleton<IEnvelopeHandler, PingHandler>();
		services.AddSingleton<SyncSessions>();
		foreach (var type in SyncHandler.Types)
		{
			services.AddSingleton<IEnvelopeHandler>(sp => new SyncHandler(type, sp.GetRequiredService<SyncSessions>()));
		}

		services.AddSingleton<EnvelopeRouter>();
		return services;
	}
}
```

Replace `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`:

```csharp
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Infrastructure.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AiChromeProxy.Infrastructure;

public static class DependencyInjection
{
	/// <summary>
	/// Registers the Cloudflare Access options (section <c>CloudflareAccess</c>), the JWKS HttpClient, the token validator and the
	/// file-system mirror (section <c>Mirror</c>; the host resolves an empty <c>Root</c> with <see cref="MirrorOptions.ResolveRoot"/>).
	/// </summary>
	public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
	{
		services.Configure<CloudflareAccessOptions>(configuration.GetSection(CloudflareAccessOptions.Section));
		services.TryAddSingleton(TimeProvider.System);
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient);
		services.AddSingleton<CloudflareAccessTokenValidator>();
		services.Configure<MirrorOptions>(configuration.GetSection(MirrorOptions.Section));
		services.AddSingleton<IMirrorStore, FileSystemMirrorStore>();
		return services;
	}
}
```

Replace `src/AiChromeProxy.Server/Transport/TransportHub.cs`:

```csharp
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.SignalR;

namespace AiChromeProxy.Server.Transport;

public sealed class TransportHub(EnvelopeRouter router, SyncSessions syncSessions, IHubContext<TransportHub> hub, ILogger<TransportHub> logger) : Hub
{
	public const string Path = "/hub";
	public const string ReceiveMethod = "Receive";

	public async Task Send(Envelope? envelope)
	{
		var request = envelope ?? new Envelope(string.Empty, default);
		var connectionId = Context.ConnectionId;
		var context = new EnvelopeContext(
			connectionId,
			Context.GetHttpContext()?.Items[CloudflareAccessMiddleware.EmailItem] as string,
			(e, ct) => hub.Clients.Client(connectionId).SendAsync(ReceiveMethod, e, ct));

		Envelope? reply;
		try
		{
			reply = await router.RouteAsync(request, context, Context.ConnectionAborted);
		}
		catch (Exception ex) when (!Context.ConnectionAborted.IsCancellationRequested)
		{
			// Details stay in the log; the client only learns that it failed.
			logger.LogError(ex, "Handler for {Type} failed", request.Type);
			reply = EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}

		if (reply is not null)
		{
			await Clients.Caller.SendAsync(ReceiveMethod, reply, Context.ConnectionAborted);
		}
	}

	public override Task OnDisconnectedAsync(Exception? exception)
	{
		syncSessions.Close(Context.ConnectionId);
		return base.OnDisconnectedAsync(exception);
	}
}
```

In `src/AiChromeProxy.Server/Program.cs`, add `using AiChromeProxy.Infrastructure.Sync;` (after `using AiChromeProxy.Infrastructure.Security;`) and, right after `builder.Services.AddInfrastructure(builder.Configuration);`:

```csharp
builder.Services.PostConfigure<MirrorOptions>(o => o.Root = MirrorOptions.ResolveRoot(o.Root, dataDir, builder.Environment.ContentRootPath));
```

In `src/AiChromeProxy.Server/appsettings.json`, add between the `Server` and `CloudflareAccess` sections:

```json
  "Mirror": {
    "Root": ""
  },
```

In `.gitignore`, under `# Server-side repo mirror (synced from RDP)` / `/data/`, add:

```
# Default dev mirror (Mirror:Root empty, no data directory): data/mirror under the Server content root
/src/AiChromeProxy.Server/data/
```

- [ ] **Step 6: Run the tests, then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Application.SyncSessionTests"`
Expected: `total: 30`, `failed: 0`.

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Server.SyncHubTests"`
Expected: `total: 3`, `failed: 0`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 652`, `failed: 0`, exit code 0.

- [ ] **Step 7: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Application src/AiChromeProxy.Infrastructure/DependencyInjection.cs src/AiChromeProxy.Server .gitignore tests/AiChromeProxy.Tests
git commit -m "feat: server-side sync sessions over the mirror"
```


### Task 8: `fsaccess.js` and the C# folder-access seam

**Files:**
- Create: `src/AiChromeProxy.Client/wwwroot/js/fsaccess.js`
- Create: `src/AiChromeProxy.Client/Sync/IFolderAccess.cs`, `FolderGrant.cs`, `FolderScan.cs`, `FileMeta.cs`, `JsFolderAccess.cs`
- Modify: `src/AiChromeProxy.Client/Program.cs`
- Test: `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs` (modify: the module is served, behind Access)

**Interfaces:**
- Consumes: `Microsoft.JSInterop` (`IJSRuntime`, `IJSObjectReference`, `DotNetObjectReference`, `JSInvokable`).
- Produces:
  - JS module exports: `pick()` → name | null; `restore()` → `{name, granted}` | null; `requestAccess()` → bool; `scan(skipDirectories, maxEntries)` → `{files: [{path, size, modified}], truncated}`; `hash(paths)` → (hex | null)[]; `readText(path)` → string | null; `readChunk(path, offset, length)` → Uint8Array; `watchVisibility(callback)` → calls `callback.Changed(visible)`.
  - `public interface IFolderAccess` — `Task<string?> PickAsync()`, `Task<FolderGrant?> RestoreAsync()`, `Task<bool> RequestAccessAsync()`, `Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries)`, `Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths)`, `Task<string?> ReadTextAsync(string path)`, `Task<byte[]> ReadChunkAsync(string path, long offset, int length)`, `Task WatchVisibilityAsync(Action<bool> changed)`.
  - Records `FolderGrant(string Name, bool Granted)`, `FolderScan(IReadOnlyList<FileMeta> Files, bool Truncated)`, `FileMeta(string Path, long Size, long Modified)`.
  - `[ExcludeFromCodeCoverage] public sealed class JsFolderAccess(IJSRuntime js) : IFolderAccess, IAsyncDisposable` (imports `./js/fsaccess.js` lazily).

- [ ] **Step 1: Write the failing test**

In `tests/AiChromeProxy.Tests/Server/TransportHubTests.cs`, add `[InlineData("GET", "/js/fsaccess.js")]` after `[InlineData("GET", "/css/app.css")]` on `NoToken_EveryEntryPoint_401`, and insert before `public void Production_WithoutAccessConfig_FailsToStart()` (its `[Fact]` line):

```csharp
	[Fact]
	public async Task FsAccessScript_WithToken_ServedAsJavaScriptModule()
	{
		using (var client = _factory.CreateClient())
		{
			var script = await Get(client, "/js/fsaccess.js", TestContext.Current.CancellationToken);

			Assert.Contains("export async function pick()", script, StringComparison.Ordinal);
			Assert.Contains("showDirectoryPicker({ id: 'aicp', mode: 'read' })", script, StringComparison.Ordinal);
		}
	}

```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Server.TransportHubTests"`
Expected: FAIL — `FsAccessScript_WithToken_ServedAsJavaScriptModule` gets 404 (`Assert.Equal() Failure … Expected: OK, Actual: NotFound`); the new `NoToken_EveryEntryPoint_401` case passes already (the Access check runs before the 404).

- [ ] **Step 3: The JS module**

Create `src/AiChromeProxy.Client/wwwroot/js/fsaccess.js`:

```javascript
// File System Access API glue for the sync engine: the only things C# cannot do in the browser.
// Read-only in sub-project 3a. Called through JsFolderAccess.cs (IFolderAccess); every other decision is made in C#.

const DB_NAME = 'aicp-fsaccess';
const HANDLES = 'handles';
const HASHES = 'hashes';
const ROOT_KEY = 'root';

let root = null;        // FileSystemDirectoryHandle of the picked folder
let files = new Map();  // path -> FileSystemFileHandle, from the last scan

function openDb() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open(DB_NAME, 1);
        request.onupgradeneeded = () => {
            request.result.createObjectStore(HANDLES);
            request.result.createObjectStore(HASHES);
        };
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(request.error);
    });
}

async function idb(store, mode, action) {
    const db = await openDb();
    try {
        return await new Promise((resolve, reject) => {
            const tx = db.transaction(store, mode);
            const request = action(tx.objectStore(store));
            tx.oncomplete = () => resolve(request.result);
            tx.onerror = () => reject(tx.error);
        });
    } finally {
        db.close();
    }
}

function toHex(buffer) {
    return Array.from(new Uint8Array(buffer), b => b.toString(16).padStart(2, '0')).join('');
}

/** Shows the folder picker; returns the folder name, or null when the user cancelled. The handle is kept in IndexedDB. */
export async function pick() {
    try {
        root = await window.showDirectoryPicker({ id: 'aicp', mode: 'read' });
    } catch (e) {
        if (e.name === 'AbortError') {
            return null;
        }
        throw e;
    }
    files = new Map();
    await idb(HANDLES, 'readwrite', s => s.put(root, ROOT_KEY));
    return root.name;
}

/** The folder picked on an earlier visit: { name, granted }, or null. After a reload Chrome usually answers "prompt". */
export async function restore() {
    const handle = await idb(HANDLES, 'readonly', s => s.get(ROOT_KEY));
    if (!handle) {
        return null;
    }
    root = handle;
    files = new Map();
    return { name: handle.name, granted: (await handle.queryPermission({ mode: 'read' })) === 'granted' };
}

/** Asks for read access again; must run from a click. */
export async function requestAccess() {
    return root !== null && (await root.requestPermission({ mode: 'read' })) === 'granted';
}

/** Walks the folder, not descending into the given directory names. Returns { files: [{ path, size, modified }], truncated }. */
export async function scan(skipDirectories, maxEntries) {
    const skip = new Set(skipDirectories.map(d => d.toLowerCase()));
    const found = new Map();
    const list = [];
    let truncated = false;

    async function walk(dir, prefix) {
        for await (const [name, handle] of dir.entries()) {
            if (list.length >= maxEntries) {
                truncated = true;
                return;
            }
            const path = prefix + name;
            if (handle.kind === 'directory') {
                if (!skip.has(name.toLowerCase())) {
                    await walk(handle, path + '/');
                }
            } else {
                const file = await handle.getFile();
                found.set(path, handle);
                list.push({ path, size: file.size, modified: file.lastModified });
            }
        }
    }

    await walk(root, '');
    files = found;
    return { files: list, truncated };
}

/**
 * SHA-256 (lower-case hex) of each path from the last scan, or null for a file that could not be read.
 * Cached in IndexedDB per folder by size and modification time, so a rescan only hashes changed files.
 */
export async function hash(paths) {
    const key = root.name;
    const cache = (await idb(HASHES, 'readonly', s => s.get(key))) ?? {};
    const result = [];
    for (const path of paths) {
        try {
            const file = await files.get(path).getFile();
            const hit = cache[path];
            if (hit && hit.size === file.size && hit.modified === file.lastModified) {
                result.push(hit.sha256);
                continue;
            }
            const sha256 = toHex(await crypto.subtle.digest('SHA-256', await file.arrayBuffer()));
            cache[path] = { size: file.size, modified: file.lastModified, sha256 };
            result.push(sha256);
        } catch {
            result.push(null);
        }
    }
    for (const path of Object.keys(cache)) {
        if (!files.has(path)) {
            delete cache[path];
        }
    }
    await idb(HASHES, 'readwrite', s => s.put(cache, key));
    return result;
}

/** Text of a file from the last scan (used for the root .gitignore), or null when it is not there. */
export async function readText(path) {
    const handle = files.get(path);
    return handle ? await (await handle.getFile()).text() : null;
}

/** Bytes [offset, offset + length) of a file from the last scan (marshalled to byte[]). */
export async function readChunk(path, offset, length) {
    const file = await files.get(path).getFile();
    return new Uint8Array(await file.slice(offset, offset + length).arrayBuffer());
}

/** Calls callback.Changed(visible) when the tab is shown or hidden and when the window gets focus. */
export function watchVisibility(callback) {
    const notify = () => callback.invokeMethodAsync('Changed', document.visibilityState === 'visible');
    document.addEventListener('visibilitychange', notify);
    window.addEventListener('focus', notify);
}
```

Optional syntax check when Node.js is installed: `node --input-type=module --check < src/AiChromeProxy.Client/wwwroot/js/fsaccess.js` → exit code 0, no output (plain `node --check file.js` does not report ES-module syntax errors).

- [ ] **Step 4: The C# seam and the thin wrapper**

Create `src/AiChromeProxy.Client/Sync/IFolderAccess.cs`:

```csharp
namespace AiChromeProxy.Client.Sync;

/// <summary>The picked folder in the browser (File System Access API, read-only); <see cref="JsFolderAccess"/> in the app, a fake in tests.</summary>
public interface IFolderAccess
{
	/// <summary>Shows the folder picker; the folder name, or null when the user cancelled.</summary>
	Task<string?> PickAsync();

	/// <summary>The folder picked on an earlier visit, or null.</summary>
	Task<FolderGrant?> RestoreAsync();

	/// <summary>Asks the browser for read access again (must run from a click).</summary>
	Task<bool> RequestAccessAsync();

	/// <summary>Walks the folder without descending into <paramref name="skipDirectories"/>; stops after <paramref name="maxEntries"/> files.</summary>
	Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries);

	/// <summary>SHA-256 (lower-case hex) per path of the last scan, in order; null for a file that could not be read.</summary>
	Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths);

	/// <summary>Text of a file of the last scan, or null when it is not there.</summary>
	Task<string?> ReadTextAsync(string path);

	Task<byte[]> ReadChunkAsync(string path, long offset, int length);

	/// <summary>Calls <paramref name="changed"/> with true when the tab becomes visible or gets focus, false when it is hidden.</summary>
	Task WatchVisibilityAsync(Action<bool> changed);
}
```

Create `src/AiChromeProxy.Client/Sync/FolderGrant.cs`:

```csharp
namespace AiChromeProxy.Client.Sync;

/// <summary>A remembered folder and whether the browser still grants read access to it.</summary>
public sealed record FolderGrant(string Name, bool Granted);
```

Create `src/AiChromeProxy.Client/Sync/FolderScan.cs`:

```csharp
namespace AiChromeProxy.Client.Sync;

/// <summary>Result of a folder walk: every file found (paths <c>/</c>-separated) and whether the walk stopped at the entry limit.</summary>
public sealed record FolderScan(IReadOnlyList<FileMeta> Files, bool Truncated);
```

Create `src/AiChromeProxy.Client/Sync/FileMeta.cs`:

```csharp
namespace AiChromeProxy.Client.Sync;

/// <summary>A file found by the walk; <see cref="Modified"/> is the browser's <c>lastModified</c> (ms since the epoch).</summary>
public sealed record FileMeta(string Path, long Size, long Modified);
```

Create `src/AiChromeProxy.Client/Sync/JsFolderAccess.cs`:

```csharp
using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

/// <summary>Thin interop wrapper over <c>wwwroot/js/fsaccess.js</c>; no logic of its own (checked manually, see docs/sync.md).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsFolderAccess(IJSRuntime js) : IFolderAccess, IAsyncDisposable
{
	private IJSObjectReference? _module;
	private DotNetObjectReference<VisibilityCallback>? _callback;

	public async Task<string?> PickAsync() => await (await ModuleAsync()).InvokeAsync<string?>("pick");

	public async Task<FolderGrant?> RestoreAsync() => await (await ModuleAsync()).InvokeAsync<FolderGrant?>("restore");

	public async Task<bool> RequestAccessAsync() => await (await ModuleAsync()).InvokeAsync<bool>("requestAccess");

	public async Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries) =>
		await (await ModuleAsync()).InvokeAsync<FolderScan>("scan", skipDirectories, maxEntries);

	public async Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths) =>
		await (await ModuleAsync()).InvokeAsync<string?[]>("hash", paths);

	public async Task<string?> ReadTextAsync(string path) => await (await ModuleAsync()).InvokeAsync<string?>("readText", path);

	public async Task<byte[]> ReadChunkAsync(string path, long offset, int length) =>
		await (await ModuleAsync()).InvokeAsync<byte[]>("readChunk", path, offset, length);

	public async Task WatchVisibilityAsync(Action<bool> changed)
	{
		_callback = DotNetObjectReference.Create(new VisibilityCallback(changed));
		await (await ModuleAsync()).InvokeVoidAsync("watchVisibility", _callback);
	}

	public async ValueTask DisposeAsync()
	{
		_callback?.Dispose();
		if (_module is not null)
		{
			await _module.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ModuleAsync() =>
		_module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/fsaccess.js");

	/// <summary>Target of <c>callback.invokeMethodAsync('Changed', visible)</c>.</summary>
	[ExcludeFromCodeCoverage]
	public sealed class VisibilityCallback(Action<bool> changed)
	{
		[JSInvokable]
		public void Changed(bool visible) => changed(visible);
	}
}
```

In `src/AiChromeProxy.Client/Program.cs`, add `using AiChromeProxy.Client.Sync;` (after `using AiChromeProxy.Client;`) and, after the `ITransport` registration:

```csharp
builder.Services.AddSingleton<IFolderAccess, JsFolderAccess>();
```

- [ ] **Step 5: Build and run the gate**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 654`, `failed: 0`, exit code 0.

- [ ] **Step 6: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Client/wwwroot/js/fsaccess.js src/AiChromeProxy.Client/Sync src/AiChromeProxy.Client/Program.cs tests/AiChromeProxy.Tests/Server/TransportHubTests.cs
git commit -m "feat: fsaccess.js and the IFolderAccess seam"
```


### Task 9: `SyncEngine` — browser-side orchestration

**Files:**
- Create: `src/AiChromeProxy.Client/Sync/SyncFile.cs`, `src/AiChromeProxy.Client/Sync/SyncEngine.cs`
- Test: `tests/AiChromeProxy.Tests/Client/FakeFolder.cs`, `LoopbackServer.cs`, `SyncEngineTests.cs` (new)

**Interfaces:**
- Consumes: `ITransport`, `RequestAsync`, `RequestFailedException` (Task 1); `IgnoreRules` (Task 4); `ManifestPlanner`, payload records, `SyncLimits` (Task 5); `SyncPath` (Task 3); `IFolderAccess`, `FolderGrant`, `FolderScan`, `FileMeta` (Task 8); in tests the real `SyncSessions`/`SyncHandler`/`EnvelopeRouter` (Task 7) over `FileSystemMirrorStore` (Task 6), `FakeTransport` (Task 1), `ListLogger<T>`.
- Produces:
  - `enum FileSyncState { Synced, Pending, TooLarge, Error }`; `public sealed record SyncFile(string Path, long Size, string? Sha256, FileSyncState State, string? Error = null)`.
  - `enum FolderStatus { None, NeedsPermission, Ready }`; `enum SyncPhase { Idle, Scanning, Uploading, Synced, Failed }`.
  - `public sealed class SyncEngine(ITransport transport, IFolderAccess folder, TimeProvider time)` — constants/statics `MaxScanEntries = 100_000`, `ScanInterval` (10 s), `RequestTimeout` (30 s), `ProgressInterval` (200 ms); `event Action? Changed`; properties `Folder`, `FolderName`, `Phase`, `Files` (`IReadOnlyList<SyncFile>`), `UploadDone`, `UploadTotal`, `LastSync`, `NextScan`, `Problem`, `Errors`; methods `Task InitializeAsync()`, `Task OpenFolderAsync()`, `Task RestoreAccessAsync()`, `void SetVisible(bool)`, `SyncFile? FileAt(string path)`, `Task RunAsync(CancellationToken)`, `Task SyncOnceAsync(CancellationToken)`.
  - Test helpers: `FakeFolder : IFolderAccess` (`Files`, `SizeOnly`, `PickResult`, `Remembered`, `Granted`, `ScanFailure`, `Truncated`, `ReadOverride`, `Scans`, `Hashed`, `Visibility`, `Write(path, text)`); `LoopbackServer` (`Transport`, `MirrorRoot`, `PathOf(repo, path)`, `Reconnect()`, `DropSession()`).

- [ ] **Step 1: Write the test helpers and the failing tests**

Create `tests/AiChromeProxy.Tests/Client/FakeFolder.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>In-memory <see cref="IFolderAccess"/>: a picked folder whose files are a dictionary.</summary>
public sealed class FakeFolder : IFolderAccess
{
	public string Name { get; set; } = "My Repo";

	public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);

	/// <summary>Files reported by the walk with this size but no content (e.g. larger than the sync limit).</summary>
	public Dictionary<string, long> SizeOnly { get; } = new(StringComparer.Ordinal);

	/// <summary>What <see cref="PickAsync"/> returns; null = the user cancelled.</summary>
	public string? PickResult { get; set; } = "My Repo";

	/// <summary>Whether <see cref="RestoreAsync"/> finds a remembered folder.</summary>
	public bool Remembered { get; set; }

	public bool Granted { get; set; } = true;

	public Exception? ScanFailure { get; set; }

	public bool Truncated { get; set; }

	/// <summary>Bytes <see cref="ReadChunkAsync"/> returns instead of the real ones (simulates a file changing during upload).</summary>
	public Dictionary<string, byte[]> ReadOverride { get; } = new(StringComparer.Ordinal);

	public int Scans { get; private set; }

	public List<string> Hashed { get; } = [];

	public Action<bool>? Visibility { get; private set; }

	public void Write(string path, string text) => Files[path] = Encoding.UTF8.GetBytes(text);

	public Task<string?> PickAsync() => Task.FromResult(PickResult);

	public Task<FolderGrant?> RestoreAsync() => Task.FromResult(Remembered ? new FolderGrant(Name, Granted) : null);

	public Task<bool> RequestAccessAsync()
	{
		Granted = true;
		return Task.FromResult(true);
	}

	public Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries)
	{
		Scans++;
		if (ScanFailure is not null)
		{
			return Task.FromException<FolderScan>(ScanFailure);
		}

		var skip = skipDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase);
		var files = Files.Select(f => new FileMeta(f.Key, f.Value.Length, 0))
			.Concat(SizeOnly.Select(f => new FileMeta(f.Key, f.Value, 0)))
			.Where(f => !f.Path.Split('/')[..^1].Any(skip.Contains))
			.ToList();
		return Task.FromResult(new FolderScan(files, Truncated));
	}

	public Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths)
	{
		Hashed.AddRange(paths);
		IReadOnlyList<string?> hashes = [.. paths.Select(p => Files.TryGetValue(p, out var b) ? Convert.ToHexStringLower(SHA256.HashData(b)) : null)];
		return Task.FromResult(hashes);
	}

	public Task<string?> ReadTextAsync(string path) =>
		Task.FromResult(Files.TryGetValue(path, out var b) ? Encoding.UTF8.GetString(b) : null);

	public Task<byte[]> ReadChunkAsync(string path, long offset, int length)
	{
		var content = ReadOverride.TryGetValue(path, out var o) ? o : Files[path];
		return Task.FromResult(content.Skip((int)offset).Take(length).ToArray());
	}

	public Task WatchVisibilityAsync(Action<bool> changed)
	{
		Visibility = changed;
		return Task.CompletedTask;
	}
}
```

Create `tests/AiChromeProxy.Tests/Client/LoopbackServer.cs`:

```csharp
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Client;

/// <summary>
/// The real server-side sync (router, sessions, file-system mirror in a temp folder) behind a <see cref="FakeTransport"/>:
/// the client engine is tested end to end without SignalR or a browser.
/// </summary>
public sealed class LoopbackServer : IDisposable
{
	private readonly SyncSessions _sessions;
	private readonly EnvelopeRouter _router;
	private int _connection = 1;

	public LoopbackServer()
	{
		var store = new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = MirrorRoot }));
		_sessions = new SyncSessions(store, new ListLogger<SyncSession>(), TimeProvider.System);
		_router = new EnvelopeRouter([.. SyncHandler.Types.Select(t => new SyncHandler(t, _sessions))]);
		Transport.Reply = ReplyAsync;
		Transport.SetState(TransportState.Connected);
	}

	public string MirrorRoot { get; } = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));

	public FakeTransport Transport { get; } = new();

	public string ConnectionId => $"conn-{_connection}";

	public string PathOf(string repo, string path) => Path.Combine(MirrorRoot, repo, path);

	/// <summary>Like SignalR: Reconnecting, a new connection id (the old session is gone on the server), Connected.</summary>
	public void Reconnect()
	{
		Transport.SetState(TransportState.Reconnecting);
		_sessions.Close(ConnectionId);
		_connection++;
		Transport.SetState(TransportState.Connected);
	}

	/// <summary>The server forgets the session without the client noticing (e.g. a server restart between scans).</summary>
	public void DropSession() => _sessions.Close(ConnectionId);

	public void Dispose()
	{
		_sessions.Close(ConnectionId);
		if (Directory.Exists(MirrorRoot))
		{
			Directory.Delete(MirrorRoot, recursive: true);
		}
	}

	private async Task<Envelope?> ReplyAsync(Envelope request)
	{
		try
		{
			return await _router.RouteAsync(request, new EnvelopeContext(ConnectionId, null, (_, _) => Task.CompletedTask), CancellationToken.None);
		}
		catch (Exception)
		{
			return EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}
	}
}
```

Create `tests/AiChromeProxy.Tests/Client/SyncEngineTests.cs`:

```csharp
using System.Text;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The browser-side engine against the real server-side sync (<see cref="LoopbackServer"/>) and an in-memory folder.</summary>
public sealed class SyncEngineTests : IDisposable
{
	private const string Repo = "My_Repo";

	private readonly LoopbackServer _server = new();
	private readonly FakeFolder _folder = new();
	private readonly SyncEngine _engine;

	public SyncEngineTests()
	{
		_engine = new SyncEngine(_server.Transport, _folder, TimeProvider.System);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task FirstSync_OpenManifestUpload_MirrorHasIncludedFilesOnly()
	{
		_folder.Write("README.md", "# hi");
		_folder.Write("src/app.cs", "class App { }");
		_folder.Write(".gitignore", "build/\n*.log\n");
		_folder.Write(".env", "SECRET=1");
		_folder.Write("certs/site.pem", "key");
		_folder.Write("node_modules/x/index.js", "x");
		_folder.Write("src/bin/app.dll", "dll");
		_folder.Write("build/out.js", "out");
		_folder.Write("debug.log", "log");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Null(_engine.Problem);
		Assert.Equal([".gitignore", "README.md", "src/app.cs"], _engine.Files.Select(f => f.Path));
		Assert.All(_engine.Files, f => Assert.Equal(FileSyncState.Synced, f.State));
		Assert.Equal([".gitignore", "README.md", "src/app.cs"], _folder.Hashed.Order(StringComparer.Ordinal));
		Assert.Equal("class App { }", File.ReadAllText(_server.PathOf(Repo, "src/app.cs")));
		Assert.False(File.Exists(_server.PathOf(Repo, ".env")));
		Assert.False(Directory.Exists(_server.PathOf(Repo, "node_modules")));
		Assert.False(Directory.Exists(_server.PathOf(Repo, "build")));
		Assert.Equal([MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncChunk, MessageTypes.SyncChunk, MessageTypes.SyncChunk], SentTypes());
		Assert.Equal(3, _engine.UploadTotal);
		Assert.Equal(3, _engine.UploadDone);
		Assert.NotNull(_engine.LastSync);
	}

	[Fact]
	public async Task LargeFile_SentInChunks()
	{
		var content = Enumerable.Range(0, (SyncLimits.ChunkSize * 2) + 5).Select(i => (byte)i).ToArray();
		_folder.Files["big.bin"] = content;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(content, File.ReadAllBytes(_server.PathOf(Repo, "big.bin")));
		Assert.Equal(3, SentTypes().Count(t => t == MessageTypes.SyncChunk));
	}

	[Fact]
	public async Task NoChanges_SecondScanSendsNothing()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var sent = _server.Transport.Sent.Count;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(sent, _server.Transport.Sent.Count);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task Files_SameListWhileNothingChanges_FileAtFindsEntries()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		var files = _engine.Files;

		await _engine.SyncOnceAsync(Ct);

		Assert.Same(files, _engine.Files);
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("a.txt")!.State);
		Assert.Null(_engine.FileAt("missing.txt"));

		_folder.Write("b.txt", "b");
		await _engine.SyncOnceAsync(Ct);

		Assert.NotSame(files, _engine.Files);
		Assert.Equal(FileSyncState.Synced, _engine.FileAt("b.txt")!.State);
	}

	[Fact]
	public async Task Changes_SentAsDelta_MirrorFollowsEditAddDeleteRename()
	{
		_folder.Write("edit.txt", "v1");
		_folder.Write("delete.txt", "d");
		_folder.Write("old-name.txt", "r");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();

		_folder.Write("edit.txt", "v2");
		_folder.Write("dir/added.txt", "new");
		_folder.Files.Remove("delete.txt");
		_folder.Files["new-name.txt"] = _folder.Files["old-name.txt"];
		_folder.Files.Remove("old-name.txt");
		await _engine.SyncOnceAsync(Ct);

		Assert.DoesNotContain(MessageTypes.SyncOpen, SentTypes());
		Assert.DoesNotContain(MessageTypes.SyncManifest, SentTypes());
		Assert.Contains(MessageTypes.SyncDelta, SentTypes());
		Assert.Equal("v2", File.ReadAllText(_server.PathOf(Repo, "edit.txt")));
		Assert.Equal("new", File.ReadAllText(_server.PathOf(Repo, "dir/added.txt")));
		Assert.Equal("r", File.ReadAllText(_server.PathOf(Repo, "new-name.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "delete.txt")));
		Assert.False(File.Exists(_server.PathOf(Repo, "old-name.txt")));
	}

	[Fact]
	public async Task FileOverLimit_ListedTooLarge_NotHashedNotSent()
	{
		_folder.Write("small.txt", "s");
		_folder.SizeOnly["video.mp4"] = SyncLimits.MaxFileSize + 1;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.TooLarge, _engine.Files.Single(f => f.Path == "video.mp4").State);
		Assert.DoesNotContain("video.mp4", _folder.Hashed);
		Assert.False(File.Exists(_server.PathOf(Repo, "video.mp4")));
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task NameNotAllowed_ListedAsError_OthersSynced()
	{
		_folder.Write("ok.txt", "ok");
		_folder.Write("trailing.", "x");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FileSyncState.Error, _engine.Files.Single(f => f.Path == "trailing.").State);
		Assert.Single(_engine.Errors);
		Assert.True(File.Exists(_server.PathOf(Repo, "ok.txt")));
	}

	[Fact]
	public async Task MoreThanMaxFiles_Refused_NothingSent()
	{
		for (var i = 0; i <= SyncLimits.MaxFiles; i++)
		{
			_folder.Files[$"f/{i}.txt"] = [];
		}

		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("more than 20,000 files to sync", _engine.Problem, StringComparison.Ordinal);
		Assert.Empty(_server.Transport.Sent);
		Assert.Empty(_folder.Hashed);
	}

	[Fact]
	public async Task WalkTruncated_Refused()
	{
		_folder.Truncated = true;
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("pick a smaller folder", _engine.Problem, StringComparison.Ordinal);
		Assert.Empty(_server.Transport.Sent);
	}

	[Fact]
	public async Task Reconnect_NextScanReopensAndSendsFullManifest()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.InitializeAsync();
		await _engine.SyncOnceAsync(Ct);
		_server.Transport.Sent.Clear();

		_server.Reconnect();
		_folder.Write("a.txt", "changed");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal([MessageTypes.SyncOpen, MessageTypes.SyncManifest, MessageTypes.SyncChunk], SentTypes());
		Assert.Equal("changed", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task ServerLostSession_CycleFails_NextCycleStartsOver()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.SyncOnceAsync(Ct);

		_server.DropSession();
		_folder.Write("b.txt", "b");
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Equal("Send sync.open first.", _engine.Problem);
		Assert.Equal(["Send sync.open first."], _engine.Errors);

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(SyncPhase.Synced, _engine.Phase);
		Assert.Null(_engine.Problem);
		Assert.Equal("b", File.ReadAllText(_server.PathOf(Repo, "b.txt")));
	}

	[Fact]
	public async Task FileChangedDuringUpload_MarkedError_OthersSynced_RetriedNextScan()
	{
		_folder.Write("a.txt", "aaaa");
		_folder.Write("b.txt", "bbbb");
		_folder.ReadOverride["a.txt"] = Encoding.UTF8.GetBytes("AAAA");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		var failed = _engine.Files.Single(f => f.Path == "a.txt");
		Assert.Equal(FileSyncState.Error, failed.State);
		Assert.Contains("different size or hash", failed.Error, StringComparison.Ordinal);
		Assert.Equal(FileSyncState.Synced, _engine.Files.Single(f => f.Path == "b.txt").State);
		Assert.False(File.Exists(_server.PathOf(Repo, "a.txt")));

		_folder.ReadOverride.Clear();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal("aaaa", File.ReadAllText(_server.PathOf(Repo, "a.txt")));
		Assert.All(_engine.Files, f => Assert.Equal(FileSyncState.Synced, f.State));
	}

	[Fact]
	public async Task FileShrankDuringUpload_MarkedError()
	{
		_folder.Write("a.txt", "aaaa");
		_folder.ReadOverride["a.txt"] = Encoding.UTF8.GetBytes("aa");
		await OpenAsync();

		await _engine.SyncOnceAsync(Ct);

		Assert.Contains("changed while it was uploaded", _engine.Files.Single().Error, StringComparison.Ordinal);
	}

	[Fact]
	public async Task AccessLost_NeedsPermission_RestoreAccessResumes()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		_folder.ScanFailure = new JSException("NotAllowedError");
		_folder.Remembered = true;
		_folder.Granted = false;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.NeedsPermission, _engine.Folder);
		Assert.Equal(SyncPhase.Failed, _engine.Phase);
		Assert.Contains("Restore access", _engine.Problem, StringComparison.Ordinal);

		_folder.ScanFailure = null;
		await _engine.RestoreAccessAsync();
		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.Ready, _engine.Folder);
		Assert.Equal(SyncPhase.Synced, _engine.Phase);
	}

	[Fact]
	public async Task ScanFailsWithAccessStillGranted_CycleFails()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		_folder.ScanFailure = new JSException("disk gone");
		_folder.Remembered = true;

		await _engine.SyncOnceAsync(Ct);

		Assert.Equal(FolderStatus.Ready, _engine.Folder);
		Assert.Equal("disk gone", _engine.Problem);
	}

	[Theory]
	[InlineData(false, false, FolderStatus.None)]
	[InlineData(true, false, FolderStatus.NeedsPermission)]
	[InlineData(true, true, FolderStatus.Ready)]
	public async Task Initialize_RestoresRememberedFolder(bool remembered, bool granted, FolderStatus expected)
	{
		_folder.Remembered = remembered;
		_folder.Granted = granted;

		await _engine.InitializeAsync();

		Assert.Equal(expected, _engine.Folder);
		Assert.Equal(remembered ? "My Repo" : null, _engine.FolderName);
		Assert.NotNull(_folder.Visibility);
	}

	[Fact]
	public async Task OpenFolder_Cancelled_NothingChanges()
	{
		_folder.PickResult = null;

		await _engine.OpenFolderAsync();

		Assert.Equal(FolderStatus.None, _engine.Folder);
		Assert.Null(_engine.FolderName);
	}

	[Fact]
	public async Task Run_SyncsAtOnce_WakesOnFocus_StopsOnCancel()
	{
		_folder.Write("a.txt", "a");
		await OpenAsync();
		await _engine.InitializeAsync();
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var run = Task.Run(() => _engine.RunAsync(cts.Token), Ct);
			await WaitUntilAsync(() => _folder.Scans == 1 && _engine.NextScan is not null);

			_folder.Visibility!(true);
			await WaitUntilAsync(() => _folder.Scans == 2);

			_folder.Visibility!(false);
			await cts.CancelAsync();
			await run;
		}

		Assert.True(File.Exists(_server.PathOf(Repo, "a.txt")));
	}

	[Fact]
	public async Task Run_Disconnected_DoesNotScan()
	{
		await OpenAsync();
		_server.Transport.SetState(TransportState.Reconnecting);
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var run = Task.Run(() => _engine.RunAsync(cts.Token), Ct);
			await WaitUntilAsync(() => _engine.NextScan is not null);
			await cts.CancelAsync();
			await run;
		}

		Assert.Equal(0, _folder.Scans);
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		for (var i = 0; i < 200 && !condition(); i++)
		{
			await Task.Delay(25, Ct);
		}

		Assert.True(condition());
	}

	private List<string> SentTypes() => [.. _server.Transport.Sent.Select(e => e.Type)];

	private async Task OpenAsync()
	{
		await _engine.OpenFolderAsync();
		Assert.Equal(FolderStatus.Ready, _engine.Folder);
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0246` for `SyncEngine`, `SyncFile`, `FileSyncState`, `FolderStatus`, `SyncPhase`.

- [ ] **Step 3: Implement**

Create `src/AiChromeProxy.Client/Sync/SyncFile.cs`:

```csharp
namespace AiChromeProxy.Client.Sync;

public enum FileSyncState
{
	/// <summary>The mirror has this content.</summary>
	Synced,

	/// <summary>New or changed; waiting for upload.</summary>
	Pending,

	/// <summary>Larger than <see cref="AiChromeProxy.Domain.Sync.SyncLimits.MaxFileSize"/>; not synced.</summary>
	TooLarge,

	/// <summary>Could not be synced (<see cref="SyncFile.Error"/> says why); retried on the next scan.</summary>
	Error,
}

/// <summary>A file of the picked folder as the explorer shows it (excluded files are never listed).</summary>
public sealed record SyncFile(string Path, long Size, string? Sha256, FileSyncState State, string? Error = null);
```

Create `src/AiChromeProxy.Client/Sync/SyncEngine.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

public enum FolderStatus
{
	/// <summary>No folder picked yet.</summary>
	None,

	/// <summary>A folder is remembered but the browser needs a click to grant access again.</summary>
	NeedsPermission,

	Ready,
}

public enum SyncPhase
{
	Idle,
	Scanning,
	Uploading,
	Synced,
	Failed,
}

/// <summary>
/// One-way sync of the picked folder to the server's mirror: scan → (first time or after a reconnect) open + full manifest,
/// otherwise a delta → upload what the server needs, one file at a time in chunks. Rescans every <see cref="ScanInterval"/>
/// while the tab is visible and immediately on focus (ponytail: polling; switch to FileSystemObserver once it is stable).
/// </summary>
public sealed class SyncEngine(ITransport transport, IFolderAccess folder, TimeProvider time)
{
	/// <summary>Files the walk may return before the folder is refused (excluded files included; protects the tab's memory).</summary>
	public const int MaxScanEntries = 100_000;

	public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(10);
	public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

	/// <summary>During uploads <see cref="Changed"/> fires at most this often (a 20k-file first sync must not re-render 20k times).</summary>
	public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

	private readonly SemaphoreSlim _cycle = new(1, 1);
	private Dictionary<string, ManifestEntry> _known = new(StringComparer.Ordinal);
	private SyncFile[] _files = [];
	private Dictionary<string, int> _fileIndex = new(StringComparer.Ordinal);
	private long _lastRaise;
	private string? _repo;
	private bool _visible = true;
	private CancellationTokenSource _wake = new();

	/// <summary>Raised after every visible state change; the UI re-renders.</summary>
	public event Action? Changed;

	public FolderStatus Folder { get; private set; }

	public string? FolderName { get; private set; }

	public SyncPhase Phase { get; private set; }

	/// <summary>
	/// Everything in the folder except excluded files, sorted by path. A new list only when a scan finds a different
	/// set of files or states (the explorer rebuilds its tree then); upload progress updates entries in place.
	/// </summary>
	public IReadOnlyList<SyncFile> Files => _files;

	public int UploadDone { get; private set; }

	public int UploadTotal { get; private set; }

	public DateTimeOffset? LastSync { get; private set; }

	public DateTimeOffset? NextScan { get; private set; }

	/// <summary>Why the last cycle failed as a whole (connection, too many files, lost access); null when it did not.</summary>
	public string? Problem { get; private set; }

	/// <summary>The folder-level problem followed by every file in error, as "path: reason".</summary>
	public IReadOnlyList<string> Errors =>
		[.. Problem is null ? [] : new[] { Problem }, .. Files.Where(f => f.State == FileSyncState.Error).Select(f => $"{f.Path}: {f.Error}")];

	/// <summary>Restores the remembered folder and starts listening to the connection and the tab's visibility.</summary>
	public async Task InitializeAsync()
	{
		transport.StateChanged += OnTransportStateChanged;
		await folder.WatchVisibilityAsync(SetVisible);
		if (await folder.RestoreAsync() is { } grant)
		{
			FolderName = grant.Name;
			Folder = grant.Granted ? FolderStatus.Ready : FolderStatus.NeedsPermission;
		}

		Raise();
	}

	/// <summary>Shows the picker; a new folder starts a fresh session (full manifest).</summary>
	public async Task OpenFolderAsync()
	{
		if (await folder.PickAsync() is not { } name)
		{
			return;
		}

		FolderName = name;
		Folder = FolderStatus.Ready;
		SetFiles([]);
		_repo = null;
		Wake();
		Raise();
	}

	/// <summary>Asks the browser for access to the remembered folder again (from the <b>Restore access</b> click).</summary>
	public async Task RestoreAccessAsync()
	{
		if (await folder.RequestAccessAsync())
		{
			Folder = FolderStatus.Ready;
			Problem = null;
			Wake();
		}

		Raise();
	}

	public void SetVisible(bool visible)
	{
		_visible = visible;
		if (visible)
		{
			Wake();
		}
	}

	/// <summary>The explorer entry for a path, or null when the path is not (or no longer) listed.</summary>
	public SyncFile? FileAt(string path) => _fileIndex.TryGetValue(path, out var i) ? _files[i] : null;

	/// <summary>Runs <see cref="SyncOnceAsync"/> now and then every <see cref="ScanInterval"/> (or when woken) until cancelled; returns when cancelled.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		// A wake-up from before the loop runs (e.g. the folder was just picked) is covered by its first cycle.
		_wake = new CancellationTokenSource();
		try
		{
			while (!ct.IsCancellationRequested)
			{
				if (Folder == FolderStatus.Ready && _visible && transport.State == TransportState.Connected)
				{
					await SyncOnceAsync(ct);
				}

				NextScan = time.GetUtcNow() + ScanInterval;
				Raise();
				await WaitForNextScanAsync(ct);
			}
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			// Stopped.
		}
	}

	/// <summary>One cycle: scan, tell the server (full manifest or delta), upload what it needs. Never throws except on cancellation.</summary>
	public async Task SyncOnceAsync(CancellationToken ct)
	{
		await _cycle.WaitAsync(ct);
		try
		{
			Phase = SyncPhase.Scanning;
			Raise();
			if (await ScanAsync() is not { } entries)
			{
				Phase = SyncPhase.Failed;
				return;
			}

			// A reconnect may reset _repo while this cycle runs: the cycle keeps using the repo it started with.
			var repo = _repo;
			List<string> need;
			if (repo is null)
			{
				repo = await OpenAsync(ct);
				need = await SendManifestAsync(repo, entries, ct);
			}
			else
			{
				need = await SendDeltaAsync(repo, entries, ct);
			}

			// The server now has every entry except the ones it asked for.
			var needed = need.ToHashSet(StringComparer.Ordinal);
			_known = entries.Where(e => !needed.Contains(e.Path)).ToDictionary(e => e.Path, StringComparer.Ordinal);
			foreach (var f in _files.Where(f => f.State == FileSyncState.Pending && !needed.Contains(f.Path)).ToList())
			{
				SetFileState(f.Path, FileSyncState.Synced, null);
			}

			await UploadAsync(repo, entries.Where(e => needed.Contains(e.Path)).ToList(), ct);

			Problem = null;
			Phase = SyncPhase.Synced;
			LastSync = time.GetUtcNow();
		}
		catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
		{
			// The server may have lost the session (reconnect, restart): start over with a full manifest.
			_repo = null;
			Problem = ex.Message;
			Phase = SyncPhase.Failed;
		}
		finally
		{
			_cycle.Release();
			Raise();
		}
	}

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	/// <returns>The entries to sync (sorted by path), or null when the folder cannot be synced (<see cref="Problem"/> says why).</returns>
	private async Task<List<ManifestEntry>?> ScanAsync()
	{
		FolderScan scan;
		try
		{
			scan = await folder.ScanAsync(IgnoreRules.BuiltInDirectories, MaxScanEntries);
		}
		catch (JSException)
		{
			if (await folder.RestoreAsync() is not { Granted: true })
			{
				Folder = FolderStatus.NeedsPermission;
				Problem = "Access to the folder was lost; click Restore access.";
				return null;
			}

			throw;
		}

		if (scan.Truncated)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {MaxScanEntries:N0} files; pick a smaller folder.");
			return null;
		}

		var rules = IgnoreRules.Create(await folder.ReadTextAsync(".gitignore"));
		var included = scan.Files.Where(f => !rules.IsIgnored(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
		var syncable = included.Where(f => f.Size <= SyncLimits.MaxFileSize && SyncPath.IsValid(f.Path)).ToList();
		if (syncable.Count > SyncLimits.MaxFiles)
		{
			Problem = string.Create(CultureInfo.InvariantCulture, $"The folder has more than {SyncLimits.MaxFiles:N0} files to sync; pick a smaller folder or extend its .gitignore.");
			return null;
		}

		var hashes = await folder.HashAsync(syncable.Select(f => f.Path).ToList());
		var hashByPath = syncable.Select((f, i) => (f.Path, Hash: hashes[i])).ToDictionary(x => x.Path, x => x.Hash, StringComparer.Ordinal);
		var entries = new List<ManifestEntry>();
		var files = new List<SyncFile>();
		foreach (var f in included)
		{
			if (f.Size > SyncLimits.MaxFileSize)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.TooLarge));
			}
			else if (SyncPath.GetError(f.Path) is { } error)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, error));
			}
			else if (hashByPath[f.Path] is not { } hash)
			{
				files.Add(new SyncFile(f.Path, f.Size, null, FileSyncState.Error, "Could not read the file."));
			}
			else
			{
				var entry = new ManifestEntry(f.Path, f.Size, hash);
				entries.Add(entry);
				var synced = _known.TryGetValue(f.Path, out var known) && known == entry;
				files.Add(new SyncFile(f.Path, f.Size, hash, synced ? FileSyncState.Synced : FileSyncState.Pending));
			}
		}

		if (!files.SequenceEqual(_files))
		{
			SetFiles(files);
		}

		Raise();
		return entries;
	}

	/// <returns>The repo name the server uses (sanitized folder name).</returns>
	private async Task<string> OpenAsync(CancellationToken ct)
	{
		var opened = await RequestAsync(MessageTypes.SyncOpen, new SyncOpenPayload(FolderName!), ct);
		_repo = Read<SyncOpenPayload>(opened).Repo;
		_known.Clear();
		return _repo;
	}

	private async Task<List<string>> SendManifestAsync(string repo, List<ManifestEntry> entries, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in ManifestPlanner.ManifestPages(repo, entries))
		{
			need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncManifest, page, ct)).Paths);
		}

		return need;
	}

	private async Task<List<string>> SendDeltaAsync(string repo, List<ManifestEntry> entries, CancellationToken ct)
	{
		var need = new List<string>();
		foreach (var page in ManifestPlanner.DeltaPages(repo, _known, entries))
		{
			need.AddRange(Read<SyncNeedPayload>(await RequestAsync(MessageTypes.SyncDelta, page, ct)).Paths);
		}

		return need;
	}

	/// <summary>Sequential uploads; a file that fails is marked and retried on the next scan, a lost connection ends the cycle.</summary>
	private async Task UploadAsync(string repo, List<ManifestEntry> entries, CancellationToken ct)
	{
		UploadDone = 0;
		UploadTotal = entries.Count;
		Phase = SyncPhase.Uploading;
		Raise();
		foreach (var entry in entries)
		{
			try
			{
				await UploadFileAsync(repo, entry, ct);
				_known[entry.Path] = entry;
				SetFileState(entry.Path, FileSyncState.Synced, null);
			}
			catch (Exception ex) when (ex is RequestFailedException or IOException or JSException)
			{
				SetFileState(entry.Path, FileSyncState.Error, ex.Message);
			}

			UploadDone++;
			if (time.GetElapsedTime(_lastRaise) >= ProgressInterval)
			{
				Raise();
			}
		}
	}

	private async Task UploadFileAsync(string repo, ManifestEntry entry, CancellationToken ct)
	{
		for (long offset = 0; ; offset += SyncLimits.ChunkSize)
		{
			var length = (int)Math.Min(SyncLimits.ChunkSize, entry.Size - offset);
			var data = length == 0 ? [] : await folder.ReadChunkAsync(entry.Path, offset, length);
			if (data.Length != length)
			{
				throw new IOException("The file changed while it was uploaded; it is sent again on the next scan.");
			}

			var last = offset + length == entry.Size;
			var chunk = new SyncChunkPayload(repo, entry.Path, offset, Convert.ToBase64String(data), last, last ? entry.Sha256 : null);
			if (last)
			{
				await RequestAsync(MessageTypes.SyncChunk, chunk, ct);
				return;
			}

			// Not awaited for a reply: the server answers only the last chunk (or an error, which the last chunk then gets too).
			await transport.SendAsync(Envelope.Create(MessageTypes.SyncChunk, chunk), ct);
		}
	}

	private Task<Envelope> RequestAsync<T>(string type, T payload, CancellationToken ct) =>
		transport.RequestAsync(Envelope.Create(type, payload), RequestTimeout, ct);

	private void SetFiles(IReadOnlyList<SyncFile> files)
	{
		_files = [.. files];
		_fileIndex = files.Select((f, i) => (f.Path, i)).ToDictionary(x => x.Path, x => x.i, StringComparer.Ordinal);
	}

	private void SetFileState(string path, FileSyncState state, string? error)
	{
		var i = _fileIndex[path];
		_files[i] = _files[i] with { State = state, Error = error };
	}

	/// <summary>Sessions live per connection: after a reconnect the next cycle opens again and sends the full manifest.</summary>
	private void OnTransportStateChanged(TransportState state)
	{
		if (state != TransportState.Connected)
		{
			_repo = null;
			return;
		}

		Wake();
	}

	private async Task WaitForNextScanAsync(CancellationToken ct)
	{
		using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct, _wake.Token))
		{
			try
			{
				await Task.Delay(ScanInterval, time, wait.Token);
			}
			catch (OperationCanceledException) when (!ct.IsCancellationRequested)
			{
				// Woken up early.
			}
		}

		if (_wake.IsCancellationRequested)
		{
			_wake.Dispose();
			_wake = new CancellationTokenSource();
		}
	}

	private void Wake() => _wake.Cancel();

	private void Raise()
	{
		_lastRaise = time.GetTimestamp();
		Changed?.Invoke();
	}
}
```

- [ ] **Step 4: Run the tests (repeat to catch timing flakes), then the gate**

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Client.SyncEngineTests"`
Expected: `total: 21`, `failed: 0`. Run it 5 times; all 5 must pass.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 675`, `failed: 0`, exit code 0.

- [ ] **Step 5: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Client/Sync/SyncFile.cs src/AiChromeProxy.Client/Sync/SyncEngine.cs tests/AiChromeProxy.Tests/Client
git commit -m "feat: SyncEngine - scan, manifest or delta, chunked uploads, resync after reconnect"
```


### Task 10: The app shell (components, design tokens, theme toggle)

**Files:**
- Create: `src/AiChromeProxy.Client/Shell/TreeNode.cs`, `TreeRow.cs`, `FileTree.cs`, `Format.cs`
- Create: `src/AiChromeProxy.Client/Shell/Icon.razor`, `TitleBar.razor`, `ActivityBar.razor`, `Explorer.razor`, `FileTreeView.razor`, `EditorArea.razor`, `StatusBar.razor`
- Modify: `src/AiChromeProxy.Client/Pages/Home.razor` (replaced), `src/AiChromeProxy.Client/_Imports.razor`, `src/AiChromeProxy.Client/Program.cs`, `src/AiChromeProxy.Client/wwwroot/css/app.css` (replaced), `src/AiChromeProxy.Client/wwwroot/index.html`
- Test: `tests/AiChromeProxy.Tests/Client/ShellTests.cs` (new)

**Interfaces:**
- Consumes: `SyncEngine`, `SyncFile`, `FileSyncState`, `FolderStatus`, `SyncPhase` (Task 9); `ITransport`, `TransportState`, `RequestAsync`, `ConnectForeverAsync` (Tasks 1–2); `IJSRuntime`.
- Produces:
  - `public sealed record TreeNode(string Name, string Path, bool IsFolder, IReadOnlyList<TreeNode> Children)`, `public sealed record TreeRow(TreeNode Node, int Depth)`.
  - `public static class FileTree` — `IReadOnlyList<TreeNode> Build(IEnumerable<string> paths)`, `IReadOnlyList<TreeRow> Rows(IReadOnlyList<TreeNode> roots, IReadOnlySet<string> expanded)`, `(string? Active, string? Open) OnKey(string key, IReadOnlyList<TreeRow> rows, string? active, ISet<string> expanded)`.
  - `public static class Format` — `Count(long)`, `Bytes(long)`, `Connection(TransportState)`, `Ago(DateTimeOffset?, DateTimeOffset)`, `SyncStatus(FolderStatus, SyncPhase, int synced, int uploadDone, int uploadTotal, TimeSpan? untilNextScan)`, `FileKind(string name)`.
  - `data-testid`s used by E2E: `shell`, `title-bar`, `app-name`, `folder-name`, `connection-state` (pill; `title` = "Ping N ms"), `theme-toggle` (`data-theme-choice` = system/light/dark), `activity-explorer` (`aria-pressed`), `activity-chat`, `activity-search` (`aria-disabled`, `title`), `explorer`, `open-folder`, `change-folder`, `restore-access`, `sync-summary`, `file-tree`, `sidebar-resizer`, `welcome`, `file-details`, `status-bar`, `status-connection`, `status-sync`, `status-errors`, `error-list`.

- [ ] **Step 1: Write the failing tests**

Create `tests/AiChromeProxy.Tests/Client/ShellTests.cs`:

```csharp
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;

namespace AiChromeProxy.Tests.Client;

public sealed class ShellTests
{
	private static readonly IReadOnlyList<TreeNode> Tree = FileTree.Build(["b.txt", "src/z.cs", "src/App/a.cs", "A.md", "docs/readme.md", "src/b.cs"]);

	[Fact]
	public void Build_FoldersFirstThenFiles_SortedIgnoringCase()
	{
		Assert.Equal(["docs", "src", "A.md", "b.txt"], Tree.Select(n => n.Name));
		var src = Tree[1];
		Assert.True(src.IsFolder);
		Assert.Equal("src", src.Path);
		Assert.Equal(["App", "b.cs", "z.cs"], src.Children.Select(n => n.Name));
		Assert.Equal("src/App/a.cs", src.Children[0].Children[0].Path);
		Assert.False(src.Children[0].Children[0].IsFolder);
	}

	[Fact]
	public void Rows_OnlyExpandedFoldersShowChildren()
	{
		var rows = FileTree.Rows(Tree, new HashSet<string> { "src" });

		Assert.Equal(["docs", "src", "src/App", "src/b.cs", "src/z.cs", "A.md", "b.txt"], rows.Select(r => r.Node.Path));
		Assert.Equal([0, 0, 1, 1, 1, 0, 0], rows.Select(r => r.Depth));
	}

	[Theory]
	[InlineData("ArrowDown", "src", "src/App", null)]
	[InlineData("ArrowDown", "b.txt", "b.txt", null)]
	[InlineData("ArrowUp", "src/App", "src", null)]
	[InlineData("ArrowUp", "docs", "docs", null)]
	[InlineData("Home", "b.txt", "docs", null)]
	[InlineData("End", "docs", "b.txt", null)]
	[InlineData("ArrowLeft", "src/b.cs", "src", null)]
	[InlineData("ArrowLeft", "A.md", "A.md", null)]
	[InlineData("Enter", "src/b.cs", "src/b.cs", "src/b.cs")]
	[InlineData(" ", "A.md", "A.md", "A.md")]
	[InlineData("x", "A.md", "A.md", null)]
	[InlineData("ArrowDown", null, "src", null)]
	public void OnKey_MovesAndOpens(string key, string? active, string expectedActive, string? expectedOpen)
	{
		var expanded = new HashSet<string> { "src" };

		var (newActive, open) = FileTree.OnKey(key, FileTree.Rows(Tree, expanded), active, expanded);

		Assert.Equal(expectedActive, newActive);
		Assert.Equal(expectedOpen, open);
	}

	[Fact]
	public void OnKey_RightExpandsThenEnters_LeftCollapses_EnterToggles()
	{
		var expanded = new HashSet<string>();

		Assert.Equal(("docs", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.Contains("docs", expanded);
		Assert.Equal(("docs/readme.md", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.Equal(("docs", null), FileTree.OnKey("ArrowLeft", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.DoesNotContain("docs", expanded);
		Assert.Equal(("src", null), FileTree.OnKey("Enter", FileTree.Rows(Tree, expanded), "src", expanded));
		Assert.Contains("src", expanded);
		Assert.Equal(("src", null), FileTree.OnKey(" ", FileTree.Rows(Tree, expanded), "src", expanded));
		Assert.DoesNotContain("src", expanded);
		Assert.Equal(("A.md", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "A.md", expanded));
	}

	[Fact]
	public void OnKey_EmptyTree_Nothing()
	{
		Assert.Equal((null, null), FileTree.OnKey("ArrowDown", [], null, new HashSet<string>()));
	}

	[Theory]
	[InlineData(0, "0")]
	[InlineData(999, "999")]
	[InlineData(1234, "1 234")]
	[InlineData(20000, "20 000")]
	public void Count_GroupedWithNoBreakSpace(long value, string expected)
	{
		Assert.Equal(expected, Format.Count(value));
	}

	[Theory]
	[InlineData(0, "0 B")]
	[InlineData(1023, "1023 B")]
	[InlineData(1536, "1.5 KB")]
	[InlineData(5 * 1024 * 1024, "5 MB")]
	[InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
	public void Bytes_HumanReadable(long bytes, string expected)
	{
		Assert.Equal(expected, Format.Bytes(bytes));
	}

	[Theory]
	[InlineData(TransportState.Connected, "Connected")]
	[InlineData(TransportState.Connecting, "Connecting…")]
	[InlineData(TransportState.Reconnecting, "Reconnecting…")]
	[InlineData(TransportState.Disconnected, "Offline")]
	public void Connection_Text(TransportState state, string expected)
	{
		Assert.Equal(expected, Format.Connection(state));
	}

	[Fact]
	public void Ago_Text()
	{
		var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

		Assert.Equal("never", Format.Ago(null, now));
		Assert.Equal("just now", Format.Ago(now.AddSeconds(-2), now));
		Assert.Equal("42 s ago", Format.Ago(now.AddSeconds(-42), now));
		Assert.Equal("5 min ago", Format.Ago(now.AddMinutes(-5), now));
		Assert.Equal(now.AddHours(-2).ToLocalTime().ToString("HH:mm"), Format.Ago(now.AddHours(-2), now));
	}

	[Theory]
	[InlineData(FolderStatus.None, SyncPhase.Idle, "No folder open")]
	[InlineData(FolderStatus.NeedsPermission, SyncPhase.Synced, "Folder access needed")]
	[InlineData(FolderStatus.Ready, SyncPhase.Idle, "Waiting for the server…")]
	[InlineData(FolderStatus.Ready, SyncPhase.Scanning, "Scanning…")]
	[InlineData(FolderStatus.Ready, SyncPhase.Uploading, "Uploading 12/80")]
	[InlineData(FolderStatus.Ready, SyncPhase.Failed, "Sync failed")]
	public void SyncStatus_ByState(FolderStatus folder, SyncPhase phase, string expected)
	{
		Assert.Equal(expected, Format.SyncStatus(folder, phase, 1234, 12, 80, null));
	}

	[Fact]
	public void SyncStatus_Synced_WithCountdown()
	{
		Assert.Equal("Synced 1 234 files", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 1234, 0, 0, null));
		Assert.Equal("Synced 1 234 files · Rescan in 7 s", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 1234, 0, 0, TimeSpan.FromSeconds(6.2)));
		Assert.Equal("Synced 3 files · Rescan in 0 s", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 3, 0, 0, TimeSpan.FromSeconds(-1)));
	}

	[Theory]
	[InlineData("Program.cs", "code")]
	[InlineData("app.TS", "code")]
	[InlineData("index.html", "web")]
	[InlineData("appsettings.json", "data")]
	[InlineData("README.md", "doc")]
	[InlineData("logo.png", "image")]
	[InlineData("LICENSE", "file")]
	public void FileKind_ByExtension(string name, string kind)
	{
		Assert.Equal(kind, Format.FileKind(name));
	}
}
```

- [ ] **Step 2: Run the build to verify it fails**

Run: `dotnet build -c Release`
Expected: FAIL — `CS0234`: namespace `AiChromeProxy.Client.Shell` does not exist.

- [ ] **Step 3: Tree and text helpers**

Create `src/AiChromeProxy.Client/Shell/TreeNode.cs`:

```csharp
namespace AiChromeProxy.Client.Shell;

/// <summary>A folder or file of the explorer tree (structure only: sync states are looked up by <see cref="Path"/>).</summary>
public sealed record TreeNode(string Name, string Path, bool IsFolder, IReadOnlyList<TreeNode> Children);
```

Create `src/AiChromeProxy.Client/Shell/TreeRow.cs`:

```csharp
namespace AiChromeProxy.Client.Shell;

/// <summary>A visible tree row: the node and its depth (0 at the root).</summary>
public sealed record TreeRow(TreeNode Node, int Depth);
```

Create `src/AiChromeProxy.Client/Shell/FileTree.cs`:

```csharp
namespace AiChromeProxy.Client.Shell;

/// <summary>Explorer tree logic: build from the flat file list, the rows currently visible, keyboard navigation.</summary>
public static class FileTree
{
	/// <summary>Folders first, then files; each group sorted by name ignoring case.</summary>
	/// <param name="paths">File paths, <c>/</c>-separated.</param>
	public static IReadOnlyList<TreeNode> Build(IEnumerable<string> paths) =>
		Build(paths.Select(p => (Tail: p, Path: p)).ToList(), string.Empty);

	/// <summary>Depth-first rows; children only for folders in <paramref name="expanded"/> (folder paths).</summary>
	public static IReadOnlyList<TreeRow> Rows(IReadOnlyList<TreeNode> roots, IReadOnlySet<string> expanded)
	{
		var rows = new List<TreeRow>();
		void Add(IReadOnlyList<TreeNode> nodes, int depth)
		{
			foreach (var node in nodes)
			{
				rows.Add(new TreeRow(node, depth));
				if (node.IsFolder && expanded.Contains(node.Path))
				{
					Add(node.Children, depth + 1);
				}
			}
		}

		Add(roots, 0);
		return rows;
	}

	/// <summary>
	/// Tree keyboard handling (WAI-ARIA tree pattern): Up/Down move, Home/End jump, Right expands or enters a folder,
	/// Left collapses or goes to the parent, Enter/Space toggles a folder or opens a file. Updates <paramref name="expanded"/>.
	/// </summary>
	/// <returns>The new active row's path, and the file to open (null when none).</returns>
	public static (string? Active, string? Open) OnKey(string key, IReadOnlyList<TreeRow> rows, string? active, ISet<string> expanded)
	{
		if (rows.Count == 0)
		{
			return (null, null);
		}

		var index = Math.Max(0, rows.ToList().FindIndex(r => r.Node.Path == active));
		var row = rows[index];
		switch (key)
		{
			case "ArrowDown":
				return (rows[Math.Min(index + 1, rows.Count - 1)].Node.Path, null);
			case "ArrowUp":
				return (rows[Math.Max(index - 1, 0)].Node.Path, null);
			case "Home":
				return (rows[0].Node.Path, null);
			case "End":
				return (rows[^1].Node.Path, null);
			case "ArrowRight" when row.Node.IsFolder:
				if (expanded.Add(row.Node.Path) || row.Node.Children.Count == 0)
				{
					return (row.Node.Path, null);
				}

				return (row.Node.Children[0].Path, null);
			case "ArrowLeft":
				if (row.Node.IsFolder && expanded.Remove(row.Node.Path))
				{
					return (row.Node.Path, null);
				}

				var parent = rows.Take(index).LastOrDefault(r => r.Depth == row.Depth - 1);
				return (parent?.Node.Path ?? row.Node.Path, null);
			case "Enter" or " ":
				if (!row.Node.IsFolder)
				{
					return (row.Node.Path, row.Node.Path);
				}

				if (!expanded.Remove(row.Node.Path))
				{
					expanded.Add(row.Node.Path);
				}

				return (row.Node.Path, null);
			default:
				return (row.Node.Path, null);
		}
	}

	private static List<TreeNode> Build(List<(string Tail, string Path)> items, string prefix)
	{
		var folders = items
			.Where(i => i.Tail.Contains('/'))
			.GroupBy(i => i.Tail[..i.Tail.IndexOf('/')], StringComparer.Ordinal)
			.Select(g => new TreeNode(
				g.Key,
				prefix + g.Key,
				true,
				Build(g.Select(i => (i.Tail[(i.Tail.IndexOf('/') + 1)..], i.Path)).ToList(), prefix + g.Key + "/")))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		var files = items
			.Where(i => !i.Tail.Contains('/'))
			.Select(i => new TreeNode(i.Tail, i.Path, false, []))
			.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
		return [.. folders, .. files];
	}
}
```

Create `src/AiChromeProxy.Client/Shell/Format.cs`:

```csharp
using System.Globalization;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;

namespace AiChromeProxy.Client.Shell;

/// <summary>Texts the shell shows; culture-independent so the UI reads the same everywhere.</summary>
public static class Format
{
	/// <summary>Thousands separated by a no-break space: "1 234".</summary>
	private static readonly NumberFormatInfo Grouped = new() { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };

	public static string Count(long value) => value.ToString("#,0", Grouped);

	public static string Bytes(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
		< 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
		_ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
	};

	public static string Connection(TransportState state) => state switch
	{
		TransportState.Connected => "Connected",
		TransportState.Connecting => "Connecting…",
		TransportState.Reconnecting => "Reconnecting…",
		_ => "Offline",
	};

	/// <summary>"just now", "42 s ago", "5 min ago", or the time of day for anything older than an hour.</summary>
	public static string Ago(DateTimeOffset? at, DateTimeOffset now)
	{
		if (at is not { } when)
		{
			return "never";
		}

		var age = now - when;
		return age.TotalSeconds switch
		{
			< 5 => "just now",
			< 60 => $"{(int)age.TotalSeconds} s ago",
			< 3600 => $"{(int)age.TotalMinutes} min ago",
			_ => when.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
		};
	}

	/// <summary>The status bar's sync text, e.g. "Synced 1 234 files · Rescan in 7 s" or "Uploading 12/80".</summary>
	public static string SyncStatus(FolderStatus folder, SyncPhase phase, int synced, int uploadDone, int uploadTotal, TimeSpan? untilNextScan)
	{
		if (folder == FolderStatus.None)
		{
			return "No folder open";
		}

		if (folder == FolderStatus.NeedsPermission)
		{
			return "Folder access needed";
		}

		return phase switch
		{
			SyncPhase.Scanning => "Scanning…",
			SyncPhase.Uploading => $"Uploading {uploadDone}/{uploadTotal}",
			SyncPhase.Failed => "Sync failed",
			SyncPhase.Synced when untilNextScan is { } wait => $"Synced {Count(synced)} files · Rescan in {Math.Max(0, (int)Math.Ceiling(wait.TotalSeconds))} s",
			SyncPhase.Synced => $"Synced {Count(synced)} files",
			_ => "Waiting for the server…",
		};
	}

	/// <summary>Icon kind for a file name (drives the icon colour): code, web, data, doc, image or file.</summary>
	public static string FileKind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
	{
		".cs" or ".razor" or ".fs" or ".vb" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".go" or ".rs" or ".java" or ".kt" or ".dart" or ".c" or ".cpp" or ".h" or ".ps1" or ".sh" or ".sql" => "code",
		".html" or ".htm" or ".css" or ".scss" or ".xaml" or ".axaml" or ".svg" => "web",
		".json" or ".xml" or ".yml" or ".yaml" or ".toml" or ".csproj" or ".props" or ".targets" or ".slnx" or ".sln" or ".config" or ".ini" => "data",
		".md" or ".txt" or ".rst" or ".pdf" => "doc",
		".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".webp" or ".bmp" => "image",
		_ => "file",
	};
}
```

Run: `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "AiChromeProxy.Tests.Client.ShellTests"`
Expected: `total: 44`, `failed: 0`.

- [ ] **Step 4: Components**

Add to the end of `src/AiChromeProxy.Client/_Imports.razor`:

```razor
@using AiChromeProxy.Client.Shell
```

Create `src/AiChromeProxy.Client/Shell/Icon.razor`:

```razor
<svg class="icon @Class" width="@Size" height="@Size" viewBox="0 0 16 16" aria-hidden="true" focusable="false">
    @switch (Name)
    {
        case "files":
            <path d="M4 1.5h5l3 3v9a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1v-11a1 1 0 0 1 1-1zM9 1.5v3h3" />
            break;
        case "chat":
            <path d="M2.5 3h11v7.5h-6l-3 2.5v-2.5h-2z" />
            break;
        case "search":
            <circle cx="7" cy="7" r="4.5" />
            <path d="M10.5 10.5l3.5 3.5" />
            break;
        case "folder":
            <path d="M1.5 3.5h4.5l1.5 1.5h7v8h-13z" />
            break;
        case "chevron-right":
            <path d="M6 4l4 4-4 4" />
            break;
        case "chevron-down":
            <path d="M4 6l4 4 4-4" />
            break;
        case "sun":
            <circle cx="8" cy="8" r="3" />
            <path d="M8 1v2M8 13v2M1 8h2M13 8h2M3 3l1.5 1.5M11.5 11.5L13 13M3 13l1.5-1.5M11.5 4.5L13 3" />
            break;
        case "moon":
            <path d="M13 9.5A5.5 5.5 0 1 1 6.5 3a4.5 4.5 0 0 0 6.5 6.5z" />
            break;
        case "monitor":
            <rect x="1.5" y="2.5" width="13" height="9" rx="1" />
            <path d="M5.5 14h5M8 11.5V14" />
            break;
        case "warning":
            <path d="M8 1.5l6.5 12h-13zM8 6v3.5M8 11.5v.5" />
            break;
        default:
            <path d="M4 1.5h5l3 3v10H4zM9 1.5v3h3" />
            break;
    }
</svg>

@code {
    /// <summary>files, chat, search, folder, chevron-right, chevron-down, sun, moon, monitor, warning; anything else draws a file.</summary>
    [Parameter, EditorRequired]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public int Size { get; set; } = 16;

    [Parameter]
    public string? Class { get; set; }
}
```

Create `src/AiChromeProxy.Client/Shell/TitleBar.razor`:

```razor
@using AiChromeProxy.Client.Transport

<header class="titlebar" data-testid="title-bar">
    <span class="app-name" data-testid="app-name">ai-chrome-proxy</span>
    @if (FolderName is not null)
    {
        <span class="title-sep" aria-hidden="true">—</span>
        <span class="title-folder" data-testid="folder-name">@FolderName</span>
    }
    <span class="spacer"></span>
    <span class="pill pill-@State.ToString().ToLowerInvariant()" data-testid="connection-state" title="@PingTitle" role="status">@Format.Connection(State)</span>
    <button type="button" class="icon-button" data-testid="theme-toggle" data-theme-choice="@(Theme ?? "system")" title="@ThemeTitle" aria-label="@ThemeTitle" @onclick="OnToggleTheme">
        <Icon Name="@(Theme switch { "light" => "sun", "dark" => "moon", _ => "monitor" })" />
    </button>
</header>

@code {
    [Parameter]
    public string? FolderName { get; set; }

    [Parameter]
    public TransportState State { get; set; }

    /// <summary>Round trip of the last ping; null before the first one or after a failed one.</summary>
    [Parameter]
    public long? PingMs { get; set; }

    /// <summary>"light", "dark" or null (follow the system).</summary>
    [Parameter]
    public string? Theme { get; set; }

    [Parameter]
    public EventCallback OnToggleTheme { get; set; }

    private string PingTitle => PingMs is { } ms ? $"Ping {ms} ms" : "Ping: no answer yet";

    private string ThemeTitle => $"Theme: {Theme ?? "system"} (click to change)";
}
```

Create `src/AiChromeProxy.Client/Shell/ActivityBar.razor`:

```razor
<nav class="activitybar" aria-label="Views">
    <button type="button" class="activity @(ExplorerOpen ? "active" : null)" data-testid="activity-explorer" title="Explorer" aria-label="Explorer"
            aria-pressed="@(ExplorerOpen ? "true" : "false")" @onclick="OnToggleExplorer">
        <Icon Name="files" Size="24" />
    </button>
    @* aria-disabled instead of disabled: the button stays focusable and its tooltip still shows. *@
    <button type="button" class="activity" data-testid="activity-chat" title="Chat — coming soon" aria-label="Chat — coming soon" aria-disabled="true">
        <Icon Name="chat" Size="24" />
    </button>
    <button type="button" class="activity" data-testid="activity-search" title="Search — coming soon" aria-label="Search — coming soon" aria-disabled="true">
        <Icon Name="search" Size="24" />
    </button>
</nav>

@code {
    [Parameter]
    public bool ExplorerOpen { get; set; }

    [Parameter]
    public EventCallback OnToggleExplorer { get; set; }
}
```

Create `src/AiChromeProxy.Client/Shell/Explorer.razor`:

```razor
@using AiChromeProxy.Client.Sync

<aside class="sidebar" data-testid="explorer" aria-label="Explorer">
    <header class="sidebar-title">Explorer</header>
    @switch (Engine.Folder)
    {
        case FolderStatus.None:
            <div class="sidebar-empty">
                <p>No folder is open. Pick the repository folder to mirror it to the home server.</p>
                <button type="button" class="primary" data-testid="open-folder" @onclick="Engine.OpenFolderAsync">Open folder</button>
            </div>
            break;
        case FolderStatus.NeedsPermission:
            <div class="folder-row">
                <Icon Name="folder" />
                <span class="folder-name">@Engine.FolderName</span>
                <button type="button" class="link" data-testid="change-folder" @onclick="Engine.OpenFolderAsync">Change</button>
            </div>
            <div class="sidebar-empty">
                <p>Chrome needs your permission again to read this folder.</p>
                <button type="button" class="primary" data-testid="restore-access" @onclick="Engine.RestoreAccessAsync">Restore access</button>
            </div>
            break;
        default:
            <div class="folder-row">
                <Icon Name="folder" />
                <span class="folder-name">@Engine.FolderName</span>
                <button type="button" class="link" data-testid="change-folder" @onclick="Engine.OpenFolderAsync">Change</button>
            </div>
            <div class="sync-summary" data-testid="sync-summary">
                <div>@Format.Count(Synced.Count) / @Format.Count(Syncable) files synced · @Format.Bytes(Synced.Sum(f => f.Size))</div>
                <div class="muted">Last sync: @Format.Ago(Engine.LastSync, DateTimeOffset.Now)</div>
                @if (Engine.Phase == SyncPhase.Uploading)
                {
                    <progress max="@Engine.UploadTotal" value="@Engine.UploadDone" aria-label="Upload progress"></progress>
                }
            </div>
            <FileTreeView Engine="Engine" Selected="@Selected" OnSelect="OnSelect" />
            break;
    }
    <div class="resizer" data-testid="sidebar-resizer" role="separator" aria-orientation="vertical" aria-label="Resize the sidebar" @onpointerdown="OnResizeStart"></div>
</aside>

@code {
    [Parameter, EditorRequired]
    public SyncEngine Engine { get; set; } = null!;

    [Parameter]
    public string? Selected { get; set; }

    [Parameter]
    public EventCallback<string> OnSelect { get; set; }

    [Parameter]
    public EventCallback OnResizeStart { get; set; }

    private List<SyncFile> Synced => Engine.Files.Where(f => f.State == FileSyncState.Synced).ToList();

    private int Syncable => Engine.Files.Count(f => f.State != FileSyncState.TooLarge);
}
```

Create `src/AiChromeProxy.Client/Shell/FileTreeView.razor`:

```razor
@using AiChromeProxy.Client.Sync

@* One focusable tree with aria-activedescendant: arrows move the active row without moving DOM focus. *@
<div class="tree" role="tree" tabindex="0" aria-label="Files" data-testid="file-tree" aria-activedescendant="@ActiveId" @onkeydown="OnKeyDownAsync">
    @for (var i = 0; i < _rows.Count; i++)
    {
        var node = _rows[i].Node;
        var depth = _rows[i].Depth;
        <div id="tree-row-@i" role="treeitem" aria-level="@(depth + 1)" aria-expanded="@ExpandedAttribute(node)"
             aria-selected="@(node.Path == Selected ? "true" : "false")"
             class="tree-row @(node.Path == _active ? "active" : null) @(node.Path == Selected ? "selected" : null)"
             style="padding-left: @(6 + (depth * 12))px" @onclick="() => ClickAsync(node)">
            @if (node.IsFolder)
            {
                <Icon Name="@(_expanded.Contains(node.Path) ? "chevron-down" : "chevron-right")" Class="chevron" />
                <Icon Name="folder" Class="kind-folder" />
                <span class="tree-name">@node.Name</span>
            }
            else
            {
                <span class="chevron"></span>
                <Icon Name="file" Class="@("kind-" + Format.FileKind(node.Name))" />
                <span class="tree-name">@node.Name</span>
                @if (Engine.FileAt(node.Path) is { } file)
                {
                    <span class="badge badge-@file.State.ToString().ToLowerInvariant()" title="@BadgeTitle(file)" aria-label="@BadgeTitle(file)">@BadgeText(file.State)</span>
                }
            }
        </div>
    }
</div>

@code {
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private IReadOnlyList<SyncFile>? _files;
    private IReadOnlyList<TreeNode> _roots = [];
    private IReadOnlyList<TreeRow> _rows = [];
    private string? _active;

    [Parameter, EditorRequired]
    public SyncEngine Engine { get; set; } = null!;

    [Parameter]
    public string? Selected { get; set; }

    [Parameter]
    public EventCallback<string> OnSelect { get; set; }

    private string? ActiveId => _rows.ToList().FindIndex(r => r.Node.Path == _active) is var i and >= 0 ? $"tree-row-{i}" : null;

    protected override void OnParametersSet()
    {
        // The engine hands out a new list only when the set of files changed: rebuild the tree then, not on every progress tick.
        if (!ReferenceEquals(Engine.Files, _files))
        {
            _files = Engine.Files;
            _roots = FileTree.Build(Engine.Files.Select(f => f.Path));
        }

        _rows = FileTree.Rows(_roots, _expanded);
    }

    private static string BadgeText(FileSyncState state) => state switch
    {
        FileSyncState.Synced => "✓",
        FileSyncState.Pending => "•",
        FileSyncState.TooLarge => "L",
        _ => "!",
    };

    private static string BadgeTitle(SyncFile file) => file.State switch
    {
        FileSyncState.Synced => "Synced",
        FileSyncState.Pending => "Pending upload",
        FileSyncState.TooLarge => "Too large to sync (over 20 MB)",
        _ => $"Error: {file.Error}",
    };

    private string? ExpandedAttribute(TreeNode node) => node.IsFolder ? (_expanded.Contains(node.Path) ? "true" : "false") : null;

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
        _active = active;
        _rows = FileTree.Rows(_roots, _expanded);
        if (open is not null)
        {
            await OnSelect.InvokeAsync(open);
        }
    }
}
```

Create `src/AiChromeProxy.Client/Shell/EditorArea.razor`:

```razor
@using AiChromeProxy.Client.Sync

<main class="editor" aria-label="Editor">
    @if (File is null)
    {
        <section class="welcome" data-testid="welcome">
            <h1>ai-chrome-proxy</h1>
            <p>Work on the repository in this browser with Claude Code running on your home server. The folder you open here is mirrored to the server and kept in sync.</p>
            <ol class="steps">
                <li><strong>Open folder</strong> — pick the repository folder in the Explorer. Secrets (<code>.env</code>, keys) and build output are never sent.</li>
                <li><strong>Wait for the sync</strong> — the status bar shows progress; later edits follow within about 10 seconds.</li>
                <li><strong>Chat</strong> — coming soon.</li>
            </ol>
        </section>
    }
    else
    {
        <section class="file-details" data-testid="file-details">
            <h1>@File.Path</h1>
            <dl>
                <dt>Size</dt>
                <dd>@Format.Bytes(File.Size) (@Format.Count(File.Size) bytes)</dd>
                <dt>SHA-256</dt>
                <dd><code>@(File.Sha256 ?? "—")</code></dd>
                <dt>Sync state</dt>
                <dd>@File.State@(File.Error is null ? null : $": {File.Error}")</dd>
            </dl>
            <p class="muted">The file viewer comes with the code navigator.</p>
        </section>
    }
</main>

@code {
    /// <summary>The selected file; null shows the welcome page.</summary>
    [Parameter]
    public SyncFile? File { get; set; }
}
```

Create `src/AiChromeProxy.Client/Shell/StatusBar.razor`:

```razor
@using AiChromeProxy.Client.Sync
@using AiChromeProxy.Client.Transport
@implements IDisposable

<footer class="statusbar" data-testid="status-bar">
    <span class="status-item" data-testid="status-connection">
        <span class="dot dot-@State.ToString().ToLowerInvariant()" aria-hidden="true"></span>@Format.Connection(State)
    </span>
    <span class="status-item" data-testid="status-sync" role="status">@SyncText</span>
    <span class="spacer"></span>
    <button type="button" class="status-item status-button" data-testid="status-errors" aria-expanded="@(_open ? "true" : "false")"
            aria-label="@($"{Engine.Errors.Count} errors")" @onclick="() => _open = !_open">
        <Icon Name="warning" Size="14" /> @Engine.Errors.Count
    </button>
    @if (_open)
    {
        <div class="error-list" data-testid="error-list" role="dialog" aria-label="Errors">
            @if (Engine.Errors.Count == 0)
            {
                <p>No errors.</p>
            }
            else
            {
                <ul>
                    @foreach (var error in Engine.Errors)
                    {
                        <li>@error</li>
                    }
                </ul>
            }
        </div>
    }
</footer>

@code {
    private readonly CancellationTokenSource _stop = new();
    private bool _open;

    [Parameter, EditorRequired]
    public SyncEngine Engine { get; set; } = null!;

    [Parameter]
    public TransportState State { get; set; }

    private string SyncText => Format.SyncStatus(
        Engine.Folder,
        Engine.Phase,
        Engine.Files.Count(f => f.State == FileSyncState.Synced),
        Engine.UploadDone,
        Engine.UploadTotal,
        Engine.NextScan - DateTimeOffset.UtcNow);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    protected override void OnInitialized() => _ = TickAsync();

    /// <summary>Re-renders only the status bar once a second for the "Rescan in N s" countdown.</summary>
    private async Task TickAsync()
    {
        using (var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)))
        {
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    StateHasChanged();
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
        }
    }
}
```

Replace `src/AiChromeProxy.Client/Pages/Home.razor` (the status page becomes the shell):

```razor
@page "/"
@using System.Diagnostics
@using AiChromeProxy.Client.Sync
@using AiChromeProxy.Client.Transport
@using AiChromeProxy.Domain
@inject ITransport Transport
@inject SyncEngine Engine
@inject IJSRuntime JS
@implements IDisposable

<PageTitle>@(Engine.FolderName is null ? "ai-chrome-proxy" : $"{Engine.FolderName} — ai-chrome-proxy")</PageTitle>

<div class="shell" data-testid="shell" data-theme="@_theme" style="--sidebar-width: @(_sidebarOpen ? _sidebarWidth : 0)px">
    <TitleBar FolderName="@Engine.FolderName" State="@Transport.State" PingMs="@_pingMs" Theme="@_theme" OnToggleTheme="ToggleThemeAsync" />
    <ActivityBar ExplorerOpen="@_sidebarOpen" OnToggleExplorer="() => _sidebarOpen = !_sidebarOpen" />
    @if (_sidebarOpen)
    {
        <Explorer Engine="Engine" Selected="@_selected" OnSelect="path => _selected = path" OnResizeStart="() => _resizing = true" />
    }
    <EditorArea File="@(_selected is null ? null : Engine.FileAt(_selected))" />
    <StatusBar Engine="Engine" State="@Transport.State" />
    @if (_resizing)
    {
        @* Catches the pointer anywhere while the sidebar is dragged; only rendered during the drag. *@
        <div class="resize-overlay" @onpointermove="OnResize" @onpointerup="() => _resizing = false" @onpointerleave="() => _resizing = false"></div>
    }
</div>

@code {
    private const string ThemeKey = "aicp-theme";
    private const int ActivityBarWidth = 48;

    private readonly CancellationTokenSource _stop = new();
    private string? _theme;
    private bool _sidebarOpen = true;
    private int _sidebarWidth = 280;
    private bool _resizing;
    private string? _selected;
    private long? _pingMs;

    public void Dispose()
    {
        Transport.StateChanged -= OnStateChanged;
        Engine.Changed -= OnEngineChanged;
        _stop.Cancel();
        _stop.Dispose();
    }

    protected override async Task OnInitializedAsync()
    {
        Transport.StateChanged += OnStateChanged;
        Engine.Changed += OnEngineChanged;
        _theme = await ReadThemeAsync();
        if (Transport.State == TransportState.Disconnected)
        {
            _ = Transport.ConnectForeverAsync(Task.Delay, _stop.Token);
        }

        await Engine.InitializeAsync();
        _ = Engine.RunAsync(_stop.Token);
        _ = PingEveryAsync(TimeSpan.FromSeconds(15));
    }

    private void OnResize(PointerEventArgs e) => _sidebarWidth = Math.Clamp((int)e.ClientX - ActivityBarWidth, 170, 600);

    private void OnEngineChanged() => InvokeAsync(StateHasChanged);

    private void OnStateChanged(TransportState state)
    {
        if (state == TransportState.Connected)
        {
            _ = PingAsync();
        }

        InvokeAsync(StateHasChanged);
    }

    private async Task PingEveryAsync(TimeSpan interval)
    {
        using (var timer = new PeriodicTimer(interval))
        {
            try
            {
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    await PingAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Disposed.
            }
        }
    }

    private async Task PingAsync()
    {
        if (Transport.State != TransportState.Connected)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            await Transport.RequestAsync(Envelope.Create(MessageTypes.Ping, new { }), TimeSpan.FromSeconds(5), _stop.Token);
            _pingMs = watch.ElapsedMilliseconds;
        }
        catch (Exception) when (!_stop.IsCancellationRequested)
        {
            _pingMs = null;
        }

        await InvokeAsync(StateHasChanged);
    }

    /// <summary>System → light → dark → system; the choice is kept in localStorage (no attribute = follow the system).</summary>
    private async Task ToggleThemeAsync()
    {
        _theme = _theme switch
        {
            null => "light",
            "light" => "dark",
            _ => null,
        };

        try
        {
            if (_theme is null)
            {
                await JS.InvokeVoidAsync("localStorage.removeItem", ThemeKey);
            }
            else
            {
                await JS.InvokeVoidAsync("localStorage.setItem", ThemeKey, _theme);
            }
        }
        catch (JSException)
        {
            // Storage blocked: the choice lasts for this page only.
        }
    }

    private async Task<string?> ReadThemeAsync()
    {
        try
        {
            var stored = await JS.InvokeAsync<string?>("localStorage.getItem", ThemeKey);
            return stored is "light" or "dark" ? stored : null;
        }
        catch (JSException)
        {
            return null;
        }
    }
}
```

In `src/AiChromeProxy.Client/Program.cs`, after the `IFolderAccess` registration:

```csharp
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<SyncEngine>();
```

- [ ] **Step 5: Design tokens and host page**

Replace `src/AiChromeProxy.Client/wwwroot/css/app.css` (dark default, light via `prefers-color-scheme` or `data-theme`; keeps the Blazor error/loading styles that `index.html` uses):

```css
/* Design tokens: dark by default, light from the system preference or the theme toggle (data-theme on .shell).
   Text/background pairs meet WCAG AA (>= 4.5:1) in both themes. */
:root,
.shell,
.shell[data-theme="dark"] {
    color-scheme: dark;
    --bg: #1e1e1e;
    --bg-sidebar: #252526;
    --bg-activity: #333333;
    --bg-title: #3c3c3c;
    --bg-hover: #2a2d2e;
    --bg-selected: #04395e;
    --bg-input: #3c3c3c;
    --fg: #d4d4d4;
    --fg-muted: #a6a6a6;
    --fg-activity: #858585;
    --fg-activity-active: #ffffff;
    --border: #454545;
    --accent: #3794ff;
    --focus: #007fd4;
    --button-bg: #0e639c;
    --button-fg: #ffffff;
    --status-bg: #007acc;
    --status-fg: #ffffff;
    --ok: #89d185;
    --warn: #cca700;
    --error: #f48771;
    --kind-code: #569cd6;
    --kind-web: #e37933;
    --kind-data: #cbcb41;
    --kind-doc: #9cdcfe;
    --kind-image: #c586c0;
    --kind-folder: #dcb67a;
}

@media (prefers-color-scheme: light) {
    :root,
    .shell:not([data-theme]) {
        color-scheme: light;
        --bg: #ffffff;
        --bg-sidebar: #f3f3f3;
        --bg-activity: #e8e8e8;
        --bg-title: #dddddd;
        --bg-hover: #e8e8e8;
        --bg-selected: #cce4f7;
        --bg-input: #ffffff;
        --fg: #1f1f1f;
        --fg-muted: #5f5f5f;
        --fg-activity: #616161;
        --fg-activity-active: #1f1f1f;
        --border: #c8c8c8;
        --accent: #005fb8;
        --focus: #0090f1;
        --button-bg: #005fb8;
        --button-fg: #ffffff;
        --status-bg: #005fb8;
        --status-fg: #ffffff;
        --ok: #2e7d32;
        --warn: #8a6100;
        --error: #c72e0f;
        --kind-code: #0451a5;
        --kind-web: #a6460c;
        --kind-data: #7a6a00;
        --kind-doc: #1a5f8a;
        --kind-image: #8a2f8a;
        --kind-folder: #8a6100;
    }
}

.shell[data-theme="light"] {
    color-scheme: light;
    --bg: #ffffff;
    --bg-sidebar: #f3f3f3;
    --bg-activity: #e8e8e8;
    --bg-title: #dddddd;
    --bg-hover: #e8e8e8;
    --bg-selected: #cce4f7;
    --bg-input: #ffffff;
    --fg: #1f1f1f;
    --fg-muted: #5f5f5f;
    --fg-activity: #616161;
    --fg-activity-active: #1f1f1f;
    --border: #c8c8c8;
    --accent: #005fb8;
    --focus: #0090f1;
    --button-bg: #005fb8;
    --button-fg: #ffffff;
    --status-bg: #005fb8;
    --status-fg: #ffffff;
    --ok: #2e7d32;
    --warn: #8a6100;
    --error: #c72e0f;
    --kind-code: #0451a5;
    --kind-web: #a6460c;
    --kind-data: #7a6a00;
    --kind-doc: #1a5f8a;
    --kind-image: #8a2f8a;
    --kind-folder: #8a6100;
}

html,
body {
    margin: 0;
    height: 100%;
    background: var(--bg);
    color: var(--fg);
    font: 13px/1.4 system-ui, -apple-system, "Segoe UI", Roboto, sans-serif;
}

/* Layout: title bar, activity bar | sidebar | editor, status bar. Works from 1024 px wide. */
.shell {
    display: grid;
    grid-template-rows: 35px minmax(0, 1fr) 22px;
    grid-template-columns: 48px var(--sidebar-width, 280px) minmax(0, 1fr);
    height: 100vh;
    min-width: 1024px;
    background: var(--bg);
    color: var(--fg);
    overflow: hidden;
}

.titlebar {
    grid-row: 1;
    grid-column: 1 / -1;
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 0 8px 0 14px;
    background: var(--bg-title);
}

.app-name {
    font-weight: 600;
}

.title-sep,
.muted {
    color: var(--fg-muted);
}

.spacer {
    flex: 1;
}

.pill {
    padding: 1px 10px;
    border-radius: 10px;
    border: 1px solid var(--border);
    font-size: 12px;
}

.pill-connected {
    color: var(--ok);
}

.pill-connecting,
.pill-reconnecting {
    color: var(--warn);
}

.pill-disconnected {
    color: var(--error);
}

.activitybar {
    grid-row: 2;
    grid-column: 1;
    display: flex;
    flex-direction: column;
    background: var(--bg-activity);
}

.activity {
    width: 48px;
    height: 48px;
    display: grid;
    place-items: center;
    border: 0;
    border-left: 2px solid transparent;
    background: none;
    color: var(--fg-activity);
    cursor: pointer;
}

.activity.active {
    color: var(--fg-activity-active);
    border-left-color: var(--accent);
}

.activity[aria-disabled="true"] {
    opacity: 0.45;
    cursor: not-allowed;
}

.sidebar {
    grid-row: 2;
    grid-column: 2;
    position: relative;
    display: flex;
    flex-direction: column;
    min-width: 0;
    background: var(--bg-sidebar);
    border-right: 1px solid var(--border);
}

.sidebar-title {
    padding: 8px 14px;
    font-size: 11px;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--fg-muted);
}

.sidebar-empty {
    padding: 0 14px;
}

.folder-row {
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 2px 8px 2px 14px;
    font-weight: 600;
}

.folder-name {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.sync-summary {
    padding: 4px 14px 8px;
    font-size: 12px;
}

.sync-summary progress {
    width: 100%;
    height: 4px;
    accent-color: var(--accent);
}

.resizer {
    position: absolute;
    top: 0;
    right: -3px;
    width: 6px;
    height: 100%;
    cursor: col-resize;
    z-index: 2;
}

.resizer:hover {
    background: var(--focus);
}

.resize-overlay {
    position: fixed;
    inset: 0;
    cursor: col-resize;
    z-index: 10;
}

.tree {
    flex: 1;
    overflow: auto;
    padding-bottom: 8px;
    outline: none;
}

.tree-row {
    display: flex;
    align-items: center;
    gap: 4px;
    height: 22px;
    padding-right: 8px;
    white-space: nowrap;
    cursor: pointer;
}

.tree-row:hover {
    background: var(--bg-hover);
}

.tree-row.selected {
    background: var(--bg-selected);
}

.tree:focus-visible .tree-row.active {
    outline: 1px solid var(--focus);
    outline-offset: -1px;
}

.tree-name {
    flex: 1;
    overflow: hidden;
    text-overflow: ellipsis;
}

.chevron {
    width: 16px;
    flex: none;
}

.kind-folder { color: var(--kind-folder); }
.kind-code { color: var(--kind-code); }
.kind-web { color: var(--kind-web); }
.kind-data { color: var(--kind-data); }
.kind-doc { color: var(--kind-doc); }
.kind-image { color: var(--kind-image); }
.kind-file { color: var(--fg-muted); }

.badge {
    font-size: 11px;
    min-width: 12px;
    text-align: center;
}

.badge-synced { color: var(--ok); }
.badge-pending { color: var(--warn); }
.badge-toolarge { color: var(--fg-muted); }
.badge-error { color: var(--error); font-weight: 700; }

.editor {
    grid-row: 2;
    grid-column: 3;
    overflow: auto;
    padding: 32px 48px;
}

.welcome,
.file-details {
    max-width: 720px;
}

.welcome h1,
.file-details h1 {
    font-size: 24px;
    font-weight: 400;
    margin: 0 0 12px;
    word-break: break-all;
}

.steps li {
    margin: 6px 0;
}

.file-details dl {
    display: grid;
    grid-template-columns: max-content 1fr;
    gap: 6px 16px;
}

.file-details dt {
    color: var(--fg-muted);
}

.file-details dd {
    margin: 0;
    word-break: break-all;
}

.statusbar {
    grid-row: 3;
    grid-column: 1 / -1;
    position: relative;
    display: flex;
    align-items: center;
    gap: 14px;
    padding: 0 10px;
    background: var(--status-bg);
    color: var(--status-fg);
    font-size: 12px;
}

.status-item {
    display: inline-flex;
    align-items: center;
    gap: 5px;
    white-space: nowrap;
}

.status-button {
    border: 0;
    background: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
    padding: 0 4px;
}

.status-button:hover {
    background: rgba(255, 255, 255, 0.15);
}

.dot {
    width: 8px;
    height: 8px;
    border-radius: 50%;
    background: currentColor;
}

.error-list {
    position: absolute;
    right: 8px;
    bottom: 26px;
    width: 480px;
    max-height: 50vh;
    overflow: auto;
    padding: 8px 12px;
    background: var(--bg-sidebar);
    color: var(--fg);
    border: 1px solid var(--border);
    box-shadow: 0 4px 16px rgba(0, 0, 0, 0.35);
    z-index: 5;
}

.error-list ul {
    margin: 0;
    padding-left: 18px;
}

.error-list li {
    margin: 4px 0;
    word-break: break-all;
}

/* Controls */
button.primary {
    padding: 6px 14px;
    border: 0;
    border-radius: 2px;
    background: var(--button-bg);
    color: var(--button-fg);
    font: inherit;
    cursor: pointer;
}

button.link {
    border: 0;
    background: none;
    padding: 0 4px;
    color: var(--accent);
    font: inherit;
    font-weight: 400;
    cursor: pointer;
}

.icon-button {
    display: grid;
    place-items: center;
    width: 28px;
    height: 28px;
    border: 0;
    border-radius: 4px;
    background: none;
    color: var(--fg);
    cursor: pointer;
}

.icon-button:hover {
    background: var(--bg-hover);
}

.icon {
    flex: none;
    fill: none;
    stroke: currentColor;
    stroke-width: 1.3;
    stroke-linecap: round;
    stroke-linejoin: round;
}

:focus-visible {
    outline: 2px solid var(--focus);
    outline-offset: 1px;
}

/* FocusOnNavigate focuses the page heading for screen readers; it is not an interactive element. */
h1:focus,
h1:focus-visible {
    outline: none;
}

code {
    font-family: Consolas, "Cascadia Code", monospace;
    color: inherit;
}

/* Blazor host page */
#blazor-error-ui {
    color-scheme: light only;
    background: lightyellow;
    color: #1f1f1f;
    bottom: 0;
    box-shadow: 0 -1px 2px rgba(0, 0, 0, 0.2);
    box-sizing: border-box;
    display: none;
    left: 0;
    padding: 0.6rem 1.25rem 0.7rem 1.25rem;
    position: fixed;
    width: 100%;
    z-index: 1000;
}

#blazor-error-ui .dismiss {
    cursor: pointer;
    position: absolute;
    right: 0.75rem;
    top: 0.5rem;
}

.blazor-error-boundary {
    background: #b32121;
    padding: 1rem;
    color: white;
}

.blazor-error-boundary::after {
    content: "An error has occurred.";
}

.loading-progress {
    position: absolute;
    display: block;
    width: 8rem;
    height: 8rem;
    inset: 20vh 0 auto 0;
    margin: 0 auto 0 auto;
}

.loading-progress circle {
    fill: none;
    stroke: var(--border);
    stroke-width: 0.6rem;
    transform-origin: 50% 50%;
    transform: rotate(-90deg);
}

.loading-progress circle:last-child {
    stroke: var(--accent);
    stroke-dasharray: calc(3.141 * var(--blazor-load-percentage, 0%) * 0.8), 500%;
    transition: stroke-dasharray 0.05s ease-in-out;
}

.loading-progress-text {
    position: absolute;
    text-align: center;
    font-weight: bold;
    inset: calc(20vh + 3.25rem) 0 auto 0.2rem;
}

.loading-progress-text:after {
    content: var(--blazor-load-percentage-text, "Loading");
}
```

In `src/AiChromeProxy.Client/wwwroot/index.html` (keep it free of build placeholders and keep `_framework/blazor.webassembly.js`): replace `<title>AiChromeProxy.Client</title>` with

```html
<meta name="color-scheme" content="dark light" />
    <title>ai-chrome-proxy</title>
```

and delete the commented-out scoped-CSS block (`<!-- If you add any scoped CSS files … -->`, two lines).

- [ ] **Step 6: Build and run the gate**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 719`, `failed: 0`, exit code 0.

(`TransportHubTests.ClientIndexHtml_HasNoBuildPlaceholders` and `Page_WithToken_LoadsBlazorScriptThatExists` guard `index.html`.)

- [ ] **Step 7: Look at it (optional, never on port 5180)**

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"; $env:CloudflareAccess__Enabled = "false"; $env:Server__Port = "5197"
dotnet run --project src/AiChromeProxy.Server --no-launch-profile
# open http://127.0.0.1:5197/ — title bar, activity bar, explorer with "Open folder", welcome page, status bar "No folder open"; stop with Ctrl+C
Remove-Item Env:ASPNETCORE_ENVIRONMENT, Env:CloudflareAccess__Enabled, Env:Server__Port
```

- [ ] **Step 8: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add src/AiChromeProxy.Client tests/AiChromeProxy.Tests/Client/ShellTests.cs
git commit -m "feat: VS Code-like app shell with dark/light theme"
```


### Task 11: E2E for the shell

**Files:**
- Modify: `tests/AiChromeProxy.E2E/Features/Connection.feature`, `tests/AiChromeProxy.E2E/StepDefinitions/ConnectionSteps.cs`
- Create: `tests/AiChromeProxy.E2E/Features/Shell.feature`, `tests/AiChromeProxy.E2E/StepDefinitions/ShellSteps.cs`

**Interfaces:**
- Consumes: the `data-testid`s listed in Task 10; existing hooks (`AppServer` on a random port, `BrowserHooks`).
- Produces: step bindings `the server is running`, `I open the app`, `the connection state is {string}`, `the app is connected`, `the connection pill's tooltip shows the ping in milliseconds` (ConnectionSteps); the Shell steps in `ShellSteps.cs`.

- [ ] **Step 1: Replace the features and steps**

Replace `tests/AiChromeProxy.E2E/Features/Connection.feature`:

```gherkin
Feature: Connection
	The app shell connects to the server hub over SignalR and shows the connection in the title bar.

Scenario: App connects
	Given the server is running
	When I open the app
	Then the connection state is "Connected"

Scenario: Connection pill shows the ping latency
	Given the app is connected
	Then the connection pill's tooltip shows the ping in milliseconds
```

Replace `tests/AiChromeProxy.E2E/StepDefinitions/ConnectionSteps.cs` (the Ping button is gone; the latency is in the pill's tooltip):

```csharp
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

[Binding]
public sealed partial class ConnectionSteps(IPage page)
{
	[Given("the server is running")]
	public async Task GivenTheServerIsRunningAsync()
	{
		await Expect(await page.APIRequest.GetAsync("/")).ToBeOKAsync();
	}

	[When("I open the app")]
	public async Task WhenIOpenTheAppAsync()
	{
		await page.GotoAsync("/");
	}

	[Then("the connection state is {string}")]
	public async Task ThenTheConnectionStateIsAsync(string state)
	{
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync(state);
	}

	[Given("the app is connected")]
	public async Task GivenTheAppIsConnectedAsync()
	{
		await WhenIOpenTheAppAsync();
		await ThenTheConnectionStateIsAsync("Connected");
	}

	[Then("the connection pill's tooltip shows the ping in milliseconds")]
	public async Task ThenThePillShowsThePingAsync()
	{
		await Expect(page.GetByTestId("connection-state")).ToHaveAttributeAsync("title", PingTitle());
	}

	/// <summary>TitleBar.razor: "Ping {ms} ms" once a ping was answered (the first one is sent when the connection comes up).</summary>
	[GeneratedRegex(@"^Ping \d+ ms$")]
	private static partial Regex PingTitle();
}
```

Create `tests/AiChromeProxy.E2E/Features/Shell.feature`:

```gherkin
Feature: Shell
	The app looks like a small VS Code: title bar, activity bar, explorer, editor area and status bar.
	The folder picker cannot be automated: sync is covered by the hub tests and the manual checklist (docs/sync.md).

Scenario: The shell renders with no folder open
	Given the app is connected
	Then I see the title bar, the explorer, the editor and the status bar
	And the explorer offers "Open folder"
	And the editor shows the welcome page
	And the status bar says "No folder open"

Scenario: Chat and Search are announced as coming soon
	Given the app is connected
	Then the "chat" view is disabled with the tooltip "Chat — coming soon"
	And the "search" view is disabled with the tooltip "Search — coming soon"

Scenario: The theme toggle cycles system, light and dark and is remembered
	Given the app is connected
	Then the theme is "system"
	When I click the theme toggle
	Then the theme is "light"
	When I click the theme toggle
	Then the theme is "dark"
	When I reload the app
	Then the theme is "dark"
	When I click the theme toggle
	Then the theme is "system"

Scenario: The explorer collapses and opens again
	Given the app is connected
	When I click the Explorer view
	Then the explorer is hidden
	When I click the Explorer view
	Then the explorer is visible

Scenario: The error list opens from the status bar
	Given the app is connected
	When I click the error count in the status bar
	Then the error list says "No errors."
```

Create `tests/AiChromeProxy.E2E/StepDefinitions/ShellSteps.cs`:

```csharp
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Pages/Home.razor and Shell/*.razor.</summary>
[Binding]
public sealed class ShellSteps(IPage page)
{
	[Then("I see the title bar, the explorer, the editor and the status bar")]
	public async Task ThenISeeTheShellAsync()
	{
		await Expect(page.GetByTestId("title-bar")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("app-name")).ToHaveTextAsync("ai-chrome-proxy");
		await Expect(page.GetByTestId("explorer")).ToBeVisibleAsync();
		await Expect(page.GetByRole(AriaRole.Main)).ToBeVisibleAsync();
		await Expect(page.GetByTestId("status-bar")).ToBeVisibleAsync();
	}

	[Then("the explorer offers {string}")]
	public async Task ThenTheExplorerOffersAsync(string button)
	{
		await Expect(page.GetByTestId("open-folder")).ToHaveTextAsync(button);
	}

	[Then("the editor shows the welcome page")]
	public async Task ThenTheWelcomePageIsShownAsync()
	{
		await Expect(page.GetByTestId("welcome")).ToContainTextAsync("Open folder");
	}

	[Then("the status bar says {string}")]
	public async Task ThenTheStatusBarSaysAsync(string text)
	{
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(text);
	}

	[Then("the {string} view is disabled with the tooltip {string}")]
	public async Task ThenTheViewIsDisabledAsync(string view, string tooltip)
	{
		var button = page.GetByTestId($"activity-{view}");
		await Expect(button).ToHaveAttributeAsync("aria-disabled", "true");
		await Expect(button).ToHaveAttributeAsync("title", tooltip);
	}

	[Then("the theme is {string}")]
	public async Task ThenTheThemeIsAsync(string theme)
	{
		await Expect(page.GetByTestId("theme-toggle")).ToHaveAttributeAsync("data-theme-choice", theme);
		var shell = page.GetByTestId("shell");
		if (theme == "system")
		{
			await Expect(shell).Not.ToHaveAttributeAsync("data-theme", new Regex(".*"));
		}
		else
		{
			await Expect(shell).ToHaveAttributeAsync("data-theme", theme);
		}
	}

	[When("I click the theme toggle")]
	public async Task WhenIClickTheThemeToggleAsync()
	{
		await page.GetByTestId("theme-toggle").ClickAsync();
	}

	[When("I reload the app")]
	public async Task WhenIReloadTheAppAsync()
	{
		await page.ReloadAsync();
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
	}

	[When("I click the Explorer view")]
	public async Task WhenIClickTheExplorerViewAsync()
	{
		await page.GetByTestId("activity-explorer").ClickAsync();
	}

	[Then("the explorer is hidden")]
	public async Task ThenTheExplorerIsHiddenAsync()
	{
		await Expect(page.GetByTestId("explorer")).ToHaveCountAsync(0);
		await Expect(page.GetByTestId("activity-explorer")).ToHaveAttributeAsync("aria-pressed", "false");
	}

	[Then("the explorer is visible")]
	public async Task ThenTheExplorerIsVisibleAsync()
	{
		await Expect(page.GetByTestId("explorer")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("activity-explorer")).ToHaveAttributeAsync("aria-pressed", "true");
	}

	[When("I click the error count in the status bar")]
	public async Task WhenIClickTheErrorCountAsync()
	{
		await page.GetByTestId("status-errors").ClickAsync();
	}

	[Then("the error list says {string}")]
	public async Task ThenTheErrorListSaysAsync(string text)
	{
		await Expect(page.GetByTestId("error-list")).ToHaveTextAsync(text);
	}
}
```

- [ ] **Step 2: Run the E2E suite**

Run: `dotnet build tests/AiChromeProxy.E2E`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.E2E`
Expected: `total: 7`, `failed: 0`, `succeeded: 7` (Chromium must be installed once, see docs/testing.md).

- [ ] **Step 3: The xunit gate is unchanged**

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 719`, `failed: 0`, exit code 0.

- [ ] **Step 4: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add tests/AiChromeProxy.E2E/Features tests/AiChromeProxy.E2E/StepDefinitions
git commit -m "test: E2E for the app shell"
```


### Task 12: Documentation and the manual checklist

**Files:**
- Create: `docs/sync.md`
- Modify: `README.md`, `CLAUDE.md`, `docs/testing.md`

**Interfaces:**
- Consumes: everything above (names, defaults, messages).
- Produces: `docs/sync.md` (how it works, protocol table, excludes, mirror location, manual checklist from the spec), links from README and CLAUDE.md.

- [ ] **Step 1: Write `docs/sync.md`**

Create `docs/sync.md`:

````markdown
# Sync (one way: browser → home server)

The folder you open in Chrome is mirrored to the home server and kept in sync. Nothing is written back to the folder yet (that is sub-project 3b). Design: [spec](superpowers/specs/2026-10-03-sync-and-shell-design.md).

## How it works

1. **Open folder** in the Explorer calls `showDirectoryPicker({mode: "read"})` (`src/AiChromeProxy.Client/wwwroot/js/fsaccess.js`). The folder handle is kept in IndexedDB; after a reload Chrome asks again with one click (**Restore access**).
2. Every 10 s while the tab is visible, and at once when it gets focus, the browser walks the folder, drops excluded files, hashes new or changed files (SHA-256 via WebCrypto, cached in IndexedDB by size and modification time) and tells the server what changed.
3. The server answers with the paths it is missing; the browser uploads them in 16 KB chunks, one file at a time. Each file is written to `<path>.aicp-tmp` and moved into place only after its SHA-256 matched.

| Message (`Envelope.type`) | Direction | Payload (camelCase) |
|---|---|---|
| `sync.open` → `sync.opened` | client → server → client | `{repo}` — the folder name; the reply carries the sanitized name |
| `sync.manifest` → `sync.need` | client → server → client | `{repo, entries[{path, size, sha256}], final}` (pages of ≤ 500 entries and ≤ 24 000 bytes) → `{repo, paths[]}` |
| `sync.delta` → `sync.need` | client → server → client | `{repo, upserts[{path, size, sha256}], deletes[]}` → `{repo, paths[]}` |
| `sync.chunk` → `sync.stored` | client → server → client | `{repo, path, offset, data (base64), last, sha256?}` → `{repo, path}` (only the last chunk is answered) |
| `error` | server → client | `{code, message?}` with the request's `correlationId`; `code` is `bad_request`, `not_found`, `too_large`, `unknown_type` or `internal` |

After the last manifest page the server deletes mirror files that are not in the manifest. After a reconnect the browser opens a new session and sends the full manifest again.

## What is never sent

- Built in (a `.gitignore` cannot re-include them): `.git/`, `node_modules/`, `bin/`, `obj/`, `.vs/`, `.idea/`, `.env`, `.env.*`, `*.pfx`, `*.key`, `*.pem`, `id_rsa*`.
- Everything the root `.gitignore` excludes (common subset: `#` comments, blank lines, `*`, `**`, `?`, trailing `/`, leading `/`, `!`). Matching ignores case.
- Files larger than 20 MB (listed as "too large"). A folder with more than 20 000 files to sync is refused with a message.
- Files whose names Windows cannot store (e.g. `CON`, `a:b`, a trailing dot) are listed as errors.

## Where the mirror lives

`Mirror:Root`, one sub-folder per synced folder (`<root>\<repo>`, the folder name reduced to `[A-Za-z0-9._-]`, at most 64 characters):

| Server runs as | Default `Mirror:Root` |
|---|---|
| Windows service (or with `AICP_DATA_DIR`) | `<DataDir>\mirror`, e.g. `%ProgramData%\AiChromeProxy\mirror` |
| `dotnet run` without a data directory | `src\AiChromeProxy.Server\data\mirror` (gitignored) |

Set `Mirror:Root` (or the environment variable `Mirror__Root`) to put it elsewhere. Every protocol path is validated (relative, `/`-separated, no `..`, no `\` or `:`, no reserved Windows names) and must resolve inside `<root>\<repo>`; the server never follows a link or junction inside the mirror.

## Manual checklist (Chrome on the locked-down machine)

Run it before a release that touches sync. Use a test repository, not production code, the first time.

- [ ] Open `https://<your host>` (e.g. `https://code.example.com`), sign in through Cloudflare Access, click **Open folder**, pick a real repository. Note the first-sync time and the file count in the Explorer; the status bar ends at "Synced N files".
- [ ] On the home server the mirror folder has as many files as the Explorer counts as synced (`(Get-ChildItem <mirror>\<repo> -Recurse -File).Count`), and a few spot-checked files are identical (`Get-FileHash`).
- [ ] Edit a file → the mirror has the new content within ~10 s.
- [ ] Add a file, delete a file, rename a file → the mirror follows within ~10 s; the deleted/renamed file is gone and empty folders are removed.
- [ ] `.env` and `node_modules` exist in the folder but not in the mirror.
- [ ] Reload the page → the Explorer shows **Restore access**; one click resumes the sync without picking the folder again.
- [ ] Disconnect the network for 1 minute → the pill shows "Reconnecting…"; after reconnecting it shows "Connected" and the status bar returns to "Synced"; an edit made while offline reaches the mirror.
- [ ] A file larger than 20 MB is listed with the "too large" badge and is not in the mirror.
- [ ] The error count in the status bar opens the error list.
- [ ] Dark and light theme: the theme toggle cycles system → light → dark; text is readable in both; the tree works with the keyboard (arrows, Enter).
- [ ] The Server log has one "Sync <repo>: manifest of …" line per session and "Sync <repo>: stored …" after uploads, and no file contents.
````

- [ ] **Step 2: Link it**

In `README.md`, replace the **Status** line with:

````markdown
**Status:** transport + Cloudflare Access, Windows host (service, tray, installer), remote access wizard (Cloudflare Tunnel + Access in one step), one-way folder sync and the app shell. See [architecture](docs/superpowers/specs/2026-10-02-architecture-design.md), [Windows host](docs/windows-host.md) and [sync](docs/sync.md).
````

and in "Run locally" replace the line `# open http://127.0.0.1:5180/` with:

````powershell
# open http://127.0.0.1:5180/ — a folder you open there is mirrored to src/AiChromeProxy.Server/data/mirror (see docs/sync.md)
````

In `CLAUDE.md` (Stack section), replace the bullet that starts with "JS only where C# can't" with:

````markdown
- JS only where C# can't: `fsaccess.js` (File System Access API), Monaco, mermaid. JS interop sits behind a C# seam (`IFolderAccess`) with a thin `[ExcludeFromCodeCoverage]` wrapper (`JsFolderAccess`).
- Sync (one way, browser → mirror): [docs/sync.md](docs/sync.md) — protocol, excludes, `Mirror:Root`, manual checklist.
````

In `docs/testing.md`:

1. Replace the E2E row of the layers table with:

````markdown
| E2E BDD | `tests/AiChromeProxy.E2E` — Reqnroll + Playwright | The real app in a real browser: the shell renders and connects (ping latency in the pill), theme toggle, explorer collapse, error list. The folder picker cannot be automated: sync is covered by the xunit hub/engine tests and the manual checklist in [sync.md](sync.md) | local |
````

2. Append to the "Excluded from coverage" paragraph (after `… manual checklist in [windows-host.md](windows-host.md).`):

````markdown
 `JsFolderAccess` (the interop wrapper over `fsaccess.js`) is excluded the same way and covered by the checklist in [sync.md](sync.md); the sync engine itself is tested against the real server-side sync through `LoopbackServer` and an in-memory `FakeFolder`.
````

3. In the E2E section replace `` `Features/Connection.feature` is bound by `StepDefinitions/ConnectionSteps.cs`. `` with `` `Features/Connection.feature` and `Features/Shell.feature` are bound by `StepDefinitions/ConnectionSteps.cs` and `StepDefinitions/ShellSteps.cs`. `` and `` Selectors are `data-testid` attributes in `Home.razor`. `` with `` Selectors are `data-testid` attributes in `Pages/Home.razor` and `Shell/*.razor`. ``

- [ ] **Step 3: Check the links and the gate**

Run: `rg -n "sync.md" README.md CLAUDE.md docs/testing.md`
Expected: one hit in README.md, one in CLAUDE.md, two in docs/testing.md.

Run: `dotnet build -c Release`
Expected: `0 Warning(s)`, `0 Error(s)`.

Run: `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total`
Expected: `total: 719`, `failed: 0`, exit code 0.

- [ ] **Step 4: Commit**

Run `git status --short` and check that every new file of this task is listed (a `.gitignore`d folder name would drop files silently), then:

```bash
git add docs/sync.md README.md CLAUDE.md docs/testing.md
git commit -m "docs: sync guide and manual checklist"
```


## Final check (after Task 12)

- [ ] `dotnet build -c Release` → `0 Warning(s)`, `0 Error(s)`.
- [ ] Gate → `total: 719`, `failed: 0`, exit 0 (line coverage ≈ 98%).
- [ ] `dotnet test --project tests/AiChromeProxy.E2E` → 7 passed.
- [ ] `git status --short` is clean; no file under `src/AiChromeProxy.Server/data/` is tracked.
- [ ] Push the branch, open the PR, and wait for `gh pr checks <pr-number>` to be green before calling it done.
- [ ] Walk the manual checklist in `docs/sync.md` against the real home server (Chrome on the locked-down machine).

## Self-review (done while writing this plan)

- **Spec coverage.** Split/one-way (all tasks; nothing writes to the client) · folder access, IndexedDB handle, `requestPermission` (8, 9 `RestoreAccessAsync`) · walk skipping built-in dirs, WebCrypto hash with size/mtime cache (8) · excludes + `.gitignore` subset in C# (4) · 10 s rescan while visible, focus rescan (8 `watchVisibility`, 9 `RunAsync`) · 20 MB / 20 000 limits (5 `SyncLimits`, 7 server, 9 client) · protocol open/manifest pages/need/delete/chunk/stored/delta (5, 7, 9) · mirror location, sanitized repo, temp + hash + move (3, 6, 7) · path rules on both ends (3, 6, 9) · handler context + error contract + `RequestAsync` (1) · reconnect forever + full manifest again (2, 9) · one session per connection, sequential uploads (7, 9) · UI stack, tokens, theme toggle in localStorage, inline SVG (10) · every UI bullet (10; E2E 11) · server logging summary, no contents (7) · tests listed in the spec: validator table (3), `.gitignore` table (4), diff + paging (5), chunk assembly + temp cleanup (7), deletion never leaving the repo / junction not followed (6), handler/error contract (1), `RequestAsync` timeout (1), reconnect re-sends the manifest with a fake transport (9), hub end to end with `WebApplicationFactory` (7), E2E shell (11), manual checklist (12).
- **Placeholders.** None: every code step has the full file or the exact lines; every command has its expected output.
- **Type consistency.** Checked across tasks: `EnvelopeContext(connectionId, email, send)`, `RequestAsync(Envelope, TimeSpan, CancellationToken)`, `ConnectForeverAsync(Func<TimeSpan, CancellationToken, Task>, CancellationToken)`, `IMirrorStore` members, `SyncSessions(IMirrorStore, ILogger<SyncSession>, TimeProvider)`, `SyncHandler(string, SyncSessions)`, `IFolderAccess` members and records, `SyncEngine(ITransport, IFolderAccess, TimeProvider)`, `FileTree.Build(IEnumerable<string>)`, `Format.SyncStatus(...)` — the code blocks were compiled together in that form.
