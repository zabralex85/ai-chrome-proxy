# Private NuGet packages Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A **Packages** tab next to Explorer: the user adds their NuGet cache folder as a source; private packages the open repo restores are uploaded to the home server, and every Claude run restores them from there through environment variables only — nothing in the project's own configuration changes.

**Architecture:** The browser keeps a second picked folder (`Scripts/pkgaccess.ts`, behind `IPackageFolder`). A client `PackagesEngine` reads `obj/project.assets.json` next to each `*.csproj` of the repo (via the repo's `IFolderAccess`), classifies each package by its `.nupkg.metadata` in the cache, asks the server which private ones are missing (`packages.nuget.have`) and uploads them file by file in 16 KB chunks (`packages.nuget.begin` / `packages.nuget.chunk`). The server's `PackageUploads` (Application) validates and writes them through `IPackageStore` (file system, `<Packages:Root>\nuget\<id>\<version>\`, temp folder → sha512 check → rename). `ClaudeRunner` adds `NUGET_FALLBACK_PACKAGES`, `RestoreIgnoreFailedSources`, `NuGetAudit`, `WarningsNotAsErrors` to each run and a system-prompt line.

**Tech Stack:** .NET 10, C#, SignalR, Blazor WebAssembly, TypeScript (MSBuild), xunit v3, Reqnroll + Playwright.

**Spec:** [2026-10-19-private-nuget-packages-design.md](../specs/2026-10-19-private-nuget-packages-design.md) (its "Verified" paragraph is step 0, already done).

## Global Constraints

- Everything in English; no personal paths, domains or secrets in committed files.
- UTF-8 without BOM, tabs, CRLF, no `this.`, private fields `_camelCase`; StyleCop errors fail the build.
- Disposables in block form `using (...) { }` — never `using var`.
- Interfaces prefixed `I`; async methods end in `Async` and return `Task`/`Task<T>`.
- Browser code only as strict TypeScript in `src/AiChromeProxy.Client/Scripts` (no `any`, no hand-written `.js`); JS interop behind a seam with a thin `[ExcludeFromCodeCoverage]` wrapper.
- The `Envelope` JSON (camelCase) is a public contract shared with the Flutter app: only additions.
- Every path from the protocol is normalized and checked against its root.
- **Nothing in the repo, the user's folder, `nuget.config`, `*.csproj`, `Directory.*.props` or Claude Code's own files is ever written by this feature.**
- Limits: 250 MB (`250L * 1024 * 1024`) and 2 000 files per package; at most 2 000 packages per `packages.nuget.have`; chunks ≤ 16 KB raw (`16 * 1024`); id ≤ 100 chars `[A-Za-z0-9._-]`; version ≤ 64 chars `[A-Za-z0-9.+-]`; both stored lower case.
- Agent environment (only when `<Packages:Root>\nuget` exists): `NUGET_FALLBACK_PACKAGES` = configured/inherited value + `;` + that folder; `RestoreIgnoreFailedSources=true`, `NuGetAudit=false`, `WarningsNotAsErrors=NU1801`, each unless `Agent:Env` sets it.
- System prompt line (exact): "Private NuGet packages are provided through a fallback packages folder. Do not edit nuget.config, project files or Directory.*.props to fix a restore; if a private package is missing, tell the user to restore the project on their computer (the Packages tab lists it)."
- Row texts (exact): "On the server", "Uploading {n}%", "Public", "Not on this computer", "Too large to upload ({size} > 250 MB)" (size in MB rounded up to one decimal) or "Too large to upload ({n} files > 2 000)", "Failed: {reason}"; project texts "No restore yet", "Could not read obj/project.assets.json"; summary "{n} private packages ready on the server · {m} missing"; server hash mismatch "Changed while uploading; retried on the next pass."
- Gate: `dotnet build -c Release` clean; `dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --coverlet --coverlet-threshold 85 --coverlet-threshold-type line --coverlet-threshold-stat Total` green.

## File structure

| File | Responsibility |
|---|---|
| `src/AiChromeProxy.Domain/Packages/*.cs` (new) | payloads, `PackageLimits`, `NuGetNames` (id/version checks) |
| `src/AiChromeProxy.Domain/MessageTypes.cs` | `packages.nuget.*` types |
| `src/AiChromeProxy.Application/Packages/IPackageStore.cs`, `PackageUploads.cs`, `PackagesHandler.cs` (new) | port, upload rules, routing |
| `src/AiChromeProxy.Infrastructure/Packages/PackagesOptions.cs`, `FileSystemPackageStore.cs` (new) | root, file-system store |
| `src/AiChromeProxy.Infrastructure/Chat/PackageEnvironment.cs` (new), `ClaudeRunner.cs`, `ClaudeArguments.cs` | run environment, prompt line |
| `src/AiChromeProxy.Server/Program.cs`, `Transport/TransportHub.cs` | root resolution, close uploads on disconnect |
| `src/AiChromeProxy.Client/Packages/ProjectAssets.cs`, `NuGetMetadata.cs`, `IPackageFolder.cs`, `PackageRow.cs`, `PackagesEngine.cs`, `JsPackageFolder.cs` (new) | client logic and seam |
| `src/AiChromeProxy.Client/Sync/IFolderAccess.cs`, `JsFolderAccess.cs`, `Scripts/fsaccess.ts` | `ModifiedAsync` |
| `src/AiChromeProxy.Client/Scripts/pkgaccess.ts` (new) | the cache folder handle |
| `src/AiChromeProxy.Client/Shell/Explorer.razor`, `Shell/PackagesView.razor` (new), `Pages/Home.razor`, `Program.cs`, `wwwroot/css/app.css` | tab and view |
| `tests/...` | unit, loopback, E2E |
| `docs/packages.md` (new), `CLAUDE.md` | docs |

---

### Task 1: Wire contract and names

**Files:**
- Create: `src/AiChromeProxy.Domain/Packages/NuGetPayloads.cs`, `src/AiChromeProxy.Domain/Packages/PackageLimits.cs`, `src/AiChromeProxy.Domain/Packages/NuGetNames.cs`
- Modify: `src/AiChromeProxy.Domain/MessageTypes.cs`
- Test: `tests/AiChromeProxy.Tests/Domain/PackagesContractTests.cs`

**Interfaces — Produces:** the records and constants below; `NuGetNames.IsValidId(string?)`, `NuGetNames.IsValidVersion(string?)`, `NuGetNames.IsValidContentHash(string?)`, `NuGetNames.IsPublicSource(string?)`.

- [ ] **Step 1: Failing tests** — `tests/AiChromeProxy.Tests/Domain/PackagesContractTests.cs`:

```csharp
using System.Text.Json;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;

namespace AiChromeProxy.Tests.Domain;

public sealed class PackagesContractTests
{
	[Fact]
	public void Payloads_AreCamelCase()
	{
		Assert.Equal("""{"packages":[{"id":"corp.a","version":"1.2.3","contentHash":"h"}]}""", Json(new NuGetHavePayload([new NuGetPackageInfo("corp.a", "1.2.3", "h")])));
		Assert.Equal("""{"missing":[{"id":"corp.a","version":"1.2.3"}]}""", Json(new NuGetMissingPayload([new NuGetPackageKey("corp.a", "1.2.3")])));
		Assert.Equal("""{"id":"corp.a","version":"1.2.3","contentHash":"h","fileCount":2,"totalSize":3}""", Json(new NuGetBeginPayload("corp.a", "1.2.3", "h", 2, 3)));
		Assert.Equal("""{"uploadId":"u"}""", Json(new NuGetBegunPayload("u")));
		Assert.Equal("""{"uploadId":"u","path":"lib/a.dll","offset":0,"data":"AAEC","last":true,"size":3}""", Json(new NuGetChunkPayload("u", "lib/a.dll", 0, "AAEC", true, 3)));
		Assert.Equal("""{"uploadId":"u","path":"lib/a.dll","offset":3,"data":"AAEC","last":false}""", Json(new NuGetChunkPayload("u", "lib/a.dll", 3, "AAEC", false)));
		Assert.Equal("""{"id":"corp.a","version":"1.2.3"}""", Json(new NuGetStoredPayload("corp.a", "1.2.3")));
	}

	[Fact]
	public void Limits_And_Types()
	{
		Assert.Equal(250L * 1024 * 1024, PackageLimits.MaxPackageBytes);
		Assert.Equal(2_000, PackageLimits.MaxPackageFiles);
		Assert.Equal(2_000, PackageLimits.MaxHavePackages);
		Assert.Equal(16 * 1024, PackageLimits.ChunkSize);
		Assert.Equal("packages.nuget.have", MessageTypes.PackagesNuGetHave);
		Assert.Equal("packages.nuget.missing", MessageTypes.PackagesNuGetMissing);
		Assert.Equal("packages.nuget.begin", MessageTypes.PackagesNuGetBegin);
		Assert.Equal("packages.nuget.begun", MessageTypes.PackagesNuGetBegun);
		Assert.Equal("packages.nuget.chunk", MessageTypes.PackagesNuGetChunk);
		Assert.Equal("packages.nuget.stored", MessageTypes.PackagesNuGetStored);
	}

	[Theory]
	[InlineData("Corp.Private", true)]
	[InlineData("corp_x-1", true)]
	[InlineData("", false)]
	[InlineData(null, false)]
	[InlineData("..", false)]
	[InlineData(".hidden", false)]
	[InlineData("a/b", false)]
	[InlineData("a b", false)]
	public void Ids(string? id, bool valid) => Assert.Equal(valid, NuGetNames.IsValidId(id));

	[Fact]
	public void Ids_LongerThan100_AreInvalid() => Assert.False(NuGetNames.IsValidId(new string('a', 101)));

	[Theory]
	[InlineData("1.2.3", true)]
	[InlineData("1.0.0-beta.2+build.7", true)]
	[InlineData("", false)]
	[InlineData("1..2", false)]
	[InlineData("../1", false)]
	[InlineData("1.0 ", false)]
	public void Versions(string version, bool valid) => Assert.Equal(valid, NuGetNames.IsValidVersion(version));

	[Fact]
	public void ContentHash_IsBase64OfSixtyFourBytes()
	{
		Assert.True(NuGetNames.IsValidContentHash(Convert.ToBase64String(new byte[64])));
		Assert.False(NuGetNames.IsValidContentHash(Convert.ToBase64String(new byte[32])));
		Assert.False(NuGetNames.IsValidContentHash("not base64!"));
		Assert.False(NuGetNames.IsValidContentHash(null));
	}

	[Theory]
	[InlineData("https://api.nuget.org/v3/index.json", true)]
	[InlineData("https://API.NUGET.ORG/v3/index.json", true)]
	[InlineData("https://artifactory.corp.example/api/nuget/v3/corp/index.json", false)]
	[InlineData("C:\\feeds\\local", false)]
	[InlineData(null, false)]
	public void PublicSource(string? source, bool expected) => Assert.Equal(expected, NuGetNames.IsPublicSource(source));

	private static string Json(object value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
```

- [ ] **Step 2: Run to see it fail** — `dotnet build -c Release` → CS0246 for the new types.

- [ ] **Step 3: Implement**

`NuGetPayloads.cs`:

```csharp
namespace AiChromeProxy.Domain.Packages;

/// <summary>A package the browser found in its NuGet cache: lower-case id and version, and the base64 sha512 of its <c>.nupkg</c> (<c>.nupkg.metadata</c> <c>contentHash</c>).</summary>
public sealed record NuGetPackageInfo(string Id, string Version, string ContentHash);

/// <summary>A package by lower-case id and version.</summary>
public sealed record NuGetPackageKey(string Id, string Version);

/// <summary><c>packages.nuget.have</c>: the private packages the open repo uses (at most <see cref="PackageLimits.MaxHavePackages"/>); reply <c>packages.nuget.missing</c>.</summary>
public sealed record NuGetHavePayload(IReadOnlyList<NuGetPackageInfo> Packages);

/// <summary><c>packages.nuget.missing</c>: those of <c>have</c> the server does not hold.</summary>
public sealed record NuGetMissingPayload(IReadOnlyList<NuGetPackageKey> Missing);

/// <summary>
/// <c>packages.nuget.begin</c>: starts uploading one package of <paramref name="FileCount"/> files, <paramref name="TotalSize"/> bytes in all (each file's path and
/// size come with its first chunk, so this message stays small); reply <c>packages.nuget.begun</c>.
/// </summary>
public sealed record NuGetBeginPayload(string Id, string Version, string ContentHash, int FileCount, long TotalSize);

/// <summary><c>packages.nuget.begun</c>: the upload's id for its chunks.</summary>
public sealed record NuGetBegunPayload(string UploadId);

/// <summary>
/// <c>packages.nuget.chunk</c>: part of a file of the upload, files one after another (a file of size 0 is one empty chunk). A file's first chunk
/// (<paramref name="Offset"/> 0) carries its <paramref name="Size"/>, later chunks none (left out of the JSON).
/// <paramref name="Data"/> base64url without padding, ≤ <see cref="PackageLimits.ChunkSize"/> bytes raw. Only the package's <paramref name="Last"/>
/// chunk is answered: <c>packages.nuget.stored</c>, or the upload's first error.
/// </summary>
public sealed record NuGetChunkPayload(
	string UploadId,
	string Path,
	long Offset,
	string Data,
	bool Last,
	[property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Size = null);

/// <summary><c>packages.nuget.stored</c>: the package is on the server.</summary>
public sealed record NuGetStoredPayload(string Id, string Version);
```

`PackageLimits.cs`:

```csharp
namespace AiChromeProxy.Domain.Packages;

/// <summary>
/// Limits of the <c>packages.nuget.*</c> messages. Every message, inside its envelope, must stay under SignalR's 32 KB receive limit (the server drops the
/// connection otherwise): <c>have</c> is paged by serialized size like <c>sync.manifest</c> (<see cref="Sync.SyncPages"/>), <c>begin</c> carries counts only
/// and each file's path and size travel with its first chunk.
/// </summary>
public static class PackageLimits
{
	/// <summary>Largest package (all its files), in bytes.</summary>
	public const long MaxPackageBytes = 250L * 1024 * 1024;

	/// <summary>Most files in one package folder.</summary>
	public const int MaxPackageFiles = 2_000;

	/// <summary>Most packages in one <c>packages.nuget.have</c>.</summary>
	public const int MaxHavePackages = 2_000;

	/// <summary>Largest chunk before encoding, in bytes.</summary>
	public const int ChunkSize = 16 * 1024;
}
```

`NuGetNames.cs`:

```csharp
namespace AiChromeProxy.Domain.Packages;

/// <summary>Checks on NuGet ids, versions and sources from the protocol.</summary>
public static class NuGetNames
{
	public const int MaxIdLength = 100;

	public const int MaxVersionLength = 64;

	/// <summary><c>[A-Za-z0-9._-]</c>, at most <see cref="MaxIdLength"/>, starting with a letter, digit or <c>_</c>, without <c>..</c>.</summary>
	public static bool IsValidId(string? id) =>
		id is { Length: > 0 and <= MaxIdLength }
		&& (char.IsAsciiLetterOrDigit(id[0]) || id[0] == '_')
		&& !id.Contains("..", StringComparison.Ordinal)
		&& id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

	/// <summary><c>[A-Za-z0-9.+-]</c>, at most <see cref="MaxVersionLength"/>, starting with a letter or digit, without <c>..</c>.</summary>
	public static bool IsValidVersion(string? version) =>
		version is { Length: > 0 and <= MaxVersionLength }
		&& char.IsAsciiLetterOrDigit(version[0])
		&& !version.Contains("..", StringComparison.Ordinal)
		&& version.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '+' or '-');

	/// <summary>Base64 of exactly 64 bytes (sha512), as in <c>.nupkg.metadata</c>.</summary>
	public static bool IsValidContentHash(string? hash)
	{
		if (hash is not { Length: 88 })
		{
			return false;
		}

		var bytes = new byte[66];
		return Convert.TryFromBase64String(hash, bytes, out var written) && written == 64;
	}

	/// <summary>A source on nuget.org (<c>api.nuget.org</c>): the server downloads such packages itself.</summary>
	public static bool IsPublicSource(string? source) =>
		Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.Equals(uri.Host, "api.nuget.org", StringComparison.OrdinalIgnoreCase);
}
```

`MessageTypes.cs` — add (with the usual one-line summaries naming the payload):

```csharp
	public const string PackagesNuGetHave = "packages.nuget.have";
	public const string PackagesNuGetMissing = "packages.nuget.missing";
	public const string PackagesNuGetBegin = "packages.nuget.begin";
	public const string PackagesNuGetBegun = "packages.nuget.begun";
	public const string PackagesNuGetChunk = "packages.nuget.chunk";
	public const string PackagesNuGetStored = "packages.nuget.stored";
```

- [ ] **Step 4: Run** — `dotnet build -c Release; dotnet test --project tests/AiChromeProxy.Tests -c Release --no-build --filter-class "*PackagesContractTests"` → PASS.
- [ ] **Step 5: Commit** — `git commit -m "Packages: NuGet wire contract and name checks"`.

---

### Task 2: File-system package store

**Files:**
- Create: `src/AiChromeProxy.Application/Packages/IPackageStore.cs`, `src/AiChromeProxy.Infrastructure/Packages/PackagesOptions.cs`, `src/AiChromeProxy.Infrastructure/Packages/FileSystemPackageStore.cs`
- Modify: `src/AiChromeProxy.Infrastructure/Hosting/DataDirectory.cs` (`Packages => Path.Combine(Root, "packages")`), `src/AiChromeProxy.Infrastructure/DependencyInjection.cs`, `src/AiChromeProxy.Server/Program.cs` (PostConfigure next to `AttachmentsOptions`), `tests/AiChromeProxy.Tests/Infrastructure/DependencyInjectionTests.cs` (`Config()` sets `Packages:Root` like `Attachments:Root`)
- Test: `tests/AiChromeProxy.Tests/Infrastructure/FileSystemPackageStoreTests.cs`

**Interfaces — Produces:**

```csharp
namespace AiChromeProxy.Application.Packages;

/// <summary>
/// The server's NuGet packages: <c>&lt;root&gt;\nuget\&lt;id&gt;\&lt;version&gt;\</c> (lower case, checked by the caller), filled through a temp folder
/// <c>&lt;version&gt;.&lt;tag&gt;.tmp</c> that is renamed into place only when complete. Paths leaving their folder throw <see cref="ArgumentException"/>.
/// </summary>
public interface IPackageStore
{
	/// <summary>Absolute <c>&lt;root&gt;\nuget</c> (it may not exist yet).</summary>
	string NuGetFolder { get; }

	bool HasNuGet(string id, string version);

	/// <summary>Creates an empty temp folder for the package; returns its handle for the calls below.</summary>
	string Begin(string id, string version, string tag);

	/// <summary>Creates the file (and its folders) when <paramref name="data"/> starts it, else appends; <paramref name="path"/> is relative with forward slashes.</summary>
	void Append(string upload, string path, byte[] data, bool create);

	/// <summary>Base64 sha512 of a file of the upload.</summary>
	string Sha512(string upload, string path);

	/// <summary>Renames the temp folder into place; false (and the temp folder deleted) when the package is there already.</summary>
	bool Commit(string upload, string id, string version);

	/// <summary>Deletes the temp folder; no error when it is gone.</summary>
	void Discard(string upload);

	/// <summary>Deletes every temp folder (a restart lost their uploads); returns how many.</summary>
	int DeleteTemps();
}
```

`PackagesOptions` (Section `Packages`, `Root`, `ResolveRoot(configured, dataDir, contentRoot)` → configured, else `dataDir.Packages`, else `<contentRoot>\data\packages`) — copy `AttachmentsOptions` exactly with the names changed.

- [ ] **Step 1: Failing tests** — `FileSystemPackageStoreTests` (temp root under `TempRootCleanup.Root`, store over `Options.Create(new PackagesOptions { Root = _root })`):

```csharp
	[Fact]
	public void BeginAppendCommit_PutsThePackageInPlace()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		_store.Append(upload, "corp.a.1.2.3.nupkg", [1, 2], create: true);
		_store.Append(upload, "corp.a.1.2.3.nupkg", [3], create: false);
		_store.Append(upload, "lib/net10.0/a.dll", [9], create: true);
		Assert.False(_store.HasNuGet("corp.a", "1.2.3"));

		Assert.Equal(Convert.ToBase64String(SHA512.HashData(new byte[] { 1, 2, 3 })), _store.Sha512(upload, "corp.a.1.2.3.nupkg"));
		Assert.True(_store.Commit(upload, "corp.a", "1.2.3"));

		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Equal([9], File.ReadAllBytes(Path.Combine(_root, "nuget", "corp.a", "1.2.3", "lib", "net10.0", "a.dll")));
		Assert.Equal(Path.Combine(_root, "nuget"), _store.NuGetFolder);
	}

	[Fact]
	public void Commit_WhenThePackageExists_DiscardsTheUpload()
	{
		Commit("corp.a", "1.2.3", "t1");
		var second = _store.Begin("corp.a", "1.2.3", "t2");

		Assert.False(_store.Commit(second, "corp.a", "1.2.3"));
		Assert.False(Directory.Exists(second));
	}

	[Fact]
	public void PathsLeavingTheirFolder_Throw()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		Assert.Throws<ArgumentException>(() => _store.Append(upload, "../x.dll", [1], create: true));
		Assert.Throws<ArgumentException>(() => _store.Append(upload, "C:/x.dll", [1], create: true));
		Assert.Throws<ArgumentException>(() => _store.Begin("..", "1.0.0", "t"));
		Assert.Throws<ArgumentException>(() => _store.Begin("corp.a", "..", "t"));
		Assert.Throws<ArgumentException>(() => _store.Discard(Path.Combine(_root, "elsewhere")));
	}

	[Fact]
	public void DeleteTemps_RemovesUnfinishedUploadsOnly()
	{
		Commit("corp.a", "1.2.3", "t1");
		_store.Begin("corp.b", "2.0.0", "t2");

		Assert.Equal(1, _store.DeleteTemps());
		Assert.True(_store.HasNuGet("corp.a", "1.2.3"));
		Assert.Empty(Directory.GetDirectories(Path.Combine(_root, "nuget", "corp.b")));
	}

	[Fact]
	public void DeleteTemps_WithoutARoot_IsZero() => Assert.Equal(0, _store.DeleteTemps());

	[Fact]
	public void Discard_IgnoresAMissingFolder()
	{
		var upload = _store.Begin("corp.a", "1.2.3", "t1");
		_store.Discard(upload);
		_store.Discard(upload);
		Assert.False(Directory.Exists(upload));
	}

	[Fact]
	public void ResolveRoot_ConfiguredDataDirOrContentRoot() { /* same four asserts as FileSystemAttachmentStoreTests.ResolveRoot_..., with "packages" */ }

	private void Commit(string id, string version, string tag)
	{
		var upload = _store.Begin(id, version, tag);
		_store.Append(upload, "x.nupkg", [1], create: true);
		_store.Commit(upload, id, version);
	}
```

(Write `ResolveRoot_ConfiguredDataDirOrContentRoot` out in full, mirroring the attachments test.)

- [ ] **Step 2: Run to see it fail** — build fails on the missing types.

- [ ] **Step 3: Implement** `FileSystemPackageStore`:

```csharp
using System.Security.Cryptography;
using AiChromeProxy.Application.Packages;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Packages;

/// <summary><see cref="IPackageStore"/> on the file system under <see cref="PackagesOptions.Root"/>.</summary>
public sealed class FileSystemPackageStore(IOptions<PackagesOptions> options) : IPackageStore
{
	private const string TempSuffix = ".tmp";

	private readonly string _nuget = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Value.Root)), "nuget");

	public string NuGetFolder => _nuget;

	public bool HasNuGet(string id, string version) => Directory.Exists(Package(id, version));

	public string Begin(string id, string version, string tag)
	{
		var temp = Child(Child(_nuget, id), version + "." + tag + TempSuffix);
		if (Directory.Exists(temp))
		{
			Directory.Delete(temp, recursive: true);
		}

		Directory.CreateDirectory(temp);
		return temp;
	}

	public void Append(string upload, string path, byte[] data, bool create)
	{
		var file = Inside(Temp(upload), path);
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);
		using (var stream = new FileStream(file, create ? FileMode.Create : FileMode.Append, FileAccess.Write, FileShare.None))
		{
			stream.Write(data);
		}
	}

	public string Sha512(string upload, string path)
	{
		using (var stream = File.OpenRead(Inside(Temp(upload), path)))
		{
			return Convert.ToBase64String(SHA512.HashData(stream));
		}
	}

	public bool Commit(string upload, string id, string version)
	{
		var temp = Temp(upload);
		var target = Package(id, version);
		if (Directory.Exists(target))
		{
			Directory.Delete(temp, recursive: true);
			return false;
		}

		Directory.Move(temp, target);
		return true;
	}

	public void Discard(string upload)
	{
		var temp = Temp(upload);
		if (Directory.Exists(temp))
		{
			Directory.Delete(temp, recursive: true);
		}
	}

	public int DeleteTemps()
	{
		if (!Directory.Exists(_nuget))
		{
			return 0;
		}

		var temps = Directory.EnumerateDirectories(_nuget).SelectMany(Directory.EnumerateDirectories).Where(d => d.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase)).ToList();
		temps.ForEach(d => Directory.Delete(d, recursive: true));
		return temps.Count;
	}

	private static string Child(string parent, string segment)
	{
		var full = Path.GetFullPath(Path.Combine(parent, segment));
		if (!string.Equals(Path.GetDirectoryName(full), parent, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path leaves its folder.", nameof(segment));
		}

		return full;
	}

	private static string Inside(string folder, string relative)
	{
		var full = Path.GetFullPath(Path.Combine(folder, relative.Replace('/', Path.DirectorySeparatorChar)));
		if (Path.IsPathRooted(relative) || !full.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("The path leaves the package folder.", nameof(relative));
		}

		return full;
	}

	private string Package(string id, string version) => Child(Child(_nuget, id), version);

	/// <summary>A handle from <see cref="Begin"/>: a temp folder two levels under the NuGet folder.</summary>
	private string Temp(string upload)
	{
		var full = Path.GetFullPath(upload);
		if (!full.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase) || !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(full)), _nuget, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException("Not an upload of this store.", nameof(upload));
		}

		return full;
	}
}
```

DI (`Infrastructure/DependencyInjection.cs`): `services.Configure<PackagesOptions>(configuration.GetSection(PackagesOptions.Section)); services.AddSingleton<IPackageStore, FileSystemPackageStore>();`. Program.cs: `builder.Services.PostConfigure<PackagesOptions>(o => o.Root = PackagesOptions.ResolveRoot(o.Root, dataDir, builder.Environment.ContentRootPath));`. `appsettings.json`: `"Packages": { "Root": "" }` next to `Attachments`.

- [ ] **Step 4: Run** — focused `*FileSystemPackageStoreTests`, `*DependencyInjectionTests`, `*LayerDependencyTests` → PASS.
- [ ] **Step 5: Commit** — "Packages: file-system NuGet store under Packages:Root".

---

### Task 3: Server uploads and handler

**Files:**
- Create: `src/AiChromeProxy.Application/Packages/PackageUploads.cs`, `src/AiChromeProxy.Application/Packages/PackagesHandler.cs`
- Modify: `src/AiChromeProxy.Application/DependencyInjection.cs`, `src/AiChromeProxy.Server/Transport/TransportHub.cs` (ctor gets `PackageUploads packages`; `OnDisconnectedAsync` calls `packages.Close(Context.ConnectionId)`)
- Test: `tests/AiChromeProxy.Tests/Application/PackageUploadsTests.cs`

**Interfaces — Consumes:** Task 1 types, `IPackageStore`, `SyncPath.GetError`, `SyncData.Decode`, `EnvelopeException`, `ErrorCodes`. **Produces:** `PackageUploads(IPackageStore store, ILogger<PackageUploads> logger)` with `NuGetMissingPayload Have(NuGetHavePayload)`, `NuGetBegunPayload Begin(string connectionId, NuGetBeginPayload)`, `NuGetStoredPayload? Chunk(string connectionId, NuGetChunkPayload)`, `void Close(string connectionId)`, const `ChangedMessage = "Changed while uploading; retried on the next pass."`; `PackagesHandler(string type, PackageUploads uploads)` with `Types` = have/begin/chunk.

- [ ] **Step 1: Failing tests** — `PackageUploadsTests` with a real `FileSystemPackageStore` in a temp root and `ListLogger<PackageUploads>`; helper `Begin(byte[] nupkg, (string Path, byte[] Data)[] files)` that returns the begin payload (id `corp.a`, version `1.2.3`, contentHash = base64 sha512 of `nupkg`, `fileCount` and `totalSize` of the files) and a `SendAll(conn, begun, files)` that sends each file as 16 KB chunks (the file's size on its first chunk; zero-size → one empty chunk carrying size 0) with `Last` on the final chunk of the final file and returns the replies. Tests:
  - `Have_ListsOnlyPackagesNotOnTheServer` (one committed, one not → missing has the second).
  - `Have_InvalidIdOrVersionOrHash_IsBadRequest`; `Have_MoreThan2000_IsTooLarge`.
  - `Upload_StoresThePackage_AndAnswersTheLastChunkOnly` (multi-chunk file > 16 KB, a zero-size file, a nested `lib/net10.0/a.dll`; non-last chunks return null; last returns `NuGetStoredPayload("corp.a","1.2.3")`; `store.HasNuGet` true).
  - `Upload_HashMismatch_FailsOnTheLastChunk_WithTheChangedMessage_AndLeavesNothing` (contentHash of other bytes; last chunk throws `bad_request` with `PackageUploads.ChangedMessage`; no package, no temp).
  - `Begin_Limits` (`fileCount` 2 001 → too_large; `totalSize` > 250 MB → too_large; `fileCount` 0 or `totalSize` < 0 → bad_request; invalid id → bad_request); `Begin_AtTheLimits_IsAccepted`.
  - `FirstChunk_OfAFile_IsChecked` (a path failing `SyncPath.GetError`, no size, offset ≠ 0, size < 0 → bad_request; size beyond `totalSize` → too_large); `FirstChunk_RepeatedPathIgnoringCase_IsBadRequest`; `FirstChunk_OfMoreFilesThanAnnounced_IsBadRequest`; `LaterChunk_WithASize_IsOutOfOrder`.
  - `LastChunk_NeedsEveryAnnouncedFile_TheTotalSize_AndTheLowerCaseNupkg` (fewer files than `fileCount`, sizes short of `totalSize`, no `<id>.<version>.nupkg` or not lower case → bad_request, nothing stored).
  - `Chunk_OutOfOrder_DropsTheRest_AndTheLastGetsTheFirstError` (wrong offset → later non-last null, last throws that first error).
  - `Chunk_UnknownUpload_LastIsNotFound`.
  - `Begin_Again_DiscardsTheConnectionsPreviousUpload` (only one temp folder remains).
  - `Close_DiscardsTheUpload` (temp folder gone; a later chunk's last → not_found).
  - `Upload_OfAPackageAlreadyThere_StillAnswersStored` (commit returns false → still `NuGetStoredPayload`).
  - `Constructor_DeletesLeftoverTemps` (a temp created through the store before constructing → gone; logged count).
  - `Handler_RoutesTypes_AndRepliesWithCorrelationId` (have → missing; begin → begun; non-last chunk → no reply; last → stored).

- [ ] **Step 2: Run to see them fail.**

- [ ] **Step 3: Implement** `PackageUploads`:

```csharp
using System.Collections.Concurrent;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Packages;

/// <summary>
/// <c>packages.nuget.*</c>: which private packages the server lacks, and one upload per connection at a time, written to a temp folder and put in place
/// only when the files <c>begin</c> announced (count and total size) all arrived and the <c>.nupkg</c> matches its <c>contentHash</c>. Each file is checked
/// at its first chunk, which carries its path and size. A chunk that fails ends the upload: later chunks are dropped and the
/// last one gets that first error. Leftover temp folders (a restart) are deleted when the service is created.
/// </summary>
public sealed class PackageUploads
{
	public const string ChangedMessage = "Changed while uploading; retried on the next pass.";

	private const string TooLargeMessage = "A package may have at most 2 000 files and 250 MB.";

	private readonly IPackageStore _store;
	private readonly ILogger<PackageUploads> _logger;
	private readonly ConcurrentDictionary<string, Upload> _uploads = new(StringComparer.Ordinal);

	public PackageUploads(IPackageStore store, ILogger<PackageUploads> logger)
	{
		_store = store;
		_logger = logger;
		try
		{
			var deleted = _store.DeleteTemps();
			if (deleted > 0)
			{
				_logger.LogInformation("Deleted {Count} unfinished package uploads", deleted);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not delete unfinished package uploads");
		}
	}

	public NuGetMissingPayload Have(NuGetHavePayload payload)
	{
		var packages = payload.Packages ?? [];
		if (packages.Count > PackageLimits.MaxHavePackages)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"At most {PackageLimits.MaxHavePackages} packages at once.");
		}

		if (packages.Any(p => p is null))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.have has an empty entry.");
		}

		return new NuGetMissingPayload([.. packages.Select(p => Key(p.Id, p.Version, p.ContentHash)).Distinct().Where(k => !_store.HasNuGet(k.Id, k.Version))]);
	}

	public NuGetBegunPayload Begin(string connectionId, NuGetBeginPayload payload)
	{
		var key = Key(payload.Id, payload.Version, payload.ContentHash);
		if (payload.FileCount > PackageLimits.MaxPackageFiles || payload.TotalSize > PackageLimits.MaxPackageBytes)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, TooLargeMessage);
		}

		if (payload.FileCount < 1 || payload.TotalSize < 0)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "packages.nuget.begin needs at least one file and a size of 0 or more.");
		}

		Close(connectionId);
		var id = Guid.NewGuid().ToString("N");
		var upload = new Upload(id, key, payload.ContentHash, payload.FileCount, payload.TotalSize, _store.Begin(key.Id, key.Version, id[..8]));
		_uploads[connectionId] = upload;
		return new NuGetBegunPayload(id);
	}

	public NuGetStoredPayload? Chunk(string connectionId, NuGetChunkPayload payload)
	{
		if (!_uploads.TryGetValue(connectionId, out var upload) || upload.Id != payload.UploadId)
		{
			return Unknown(payload);
		}

		lock (upload)
		{
			if (upload.Closed)
			{
				return Unknown(payload);
			}

			if (upload.Error is { } error)
			{
				return payload.Last ? throw Finish(connectionId, upload, error) : null;
			}

			try
			{
				return Append(connectionId, upload, payload);
			}
			catch (Exception ex) when (ex is EnvelopeException or IOException or UnauthorizedAccessException or ArgumentException)
			{
				var failure = ex as EnvelopeException ?? new EnvelopeException(ErrorCodes.Internal, "Could not store the package.");
				if (ex is not EnvelopeException)
				{
					_logger.LogWarning(ex, "Storing package {Id} {Version} failed", upload.Key.Id, upload.Key.Version);
				}

				upload.Error = failure;
				return payload.Last ? throw Finish(connectionId, upload, failure) : null;
			}
		}
	}

	public void Close(string connectionId)
	{
		if (_uploads.TryRemove(connectionId, out var upload))
		{
			lock (upload)
			{
				upload.Closed = true;
				Discard(upload);
			}
		}
	}

	private static NuGetStoredPayload? Unknown(NuGetChunkPayload payload) =>
		payload.Last ? throw new EnvelopeException(ErrorCodes.NotFound, "Unknown package upload; it starts again on the next pass.") : null;

	private static NuGetPackageKey Key(string? id, string? version, string? hash)
	{
		if (!NuGetNames.IsValidId(id) || !NuGetNames.IsValidVersion(version) || !NuGetNames.IsValidContentHash(hash))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "Invalid package id, version or contentHash.");
		}

		return new NuGetPackageKey(id!.ToLowerInvariant(), version!.ToLowerInvariant());
	}

	/// <summary>A file's first chunk: its path and size are checked against the rules and what <c>begin</c> announced.</summary>
	private static void Start(Upload upload, NuGetChunkPayload payload)
	{
		if (payload.Offset != 0 || payload.Size is not { } size)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Chunk of '{payload.Path}' at {payload.Offset} is out of order (a file starts at 0 with its size).");
		}

		if (upload.Paths.Count == upload.FileCount)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"More files than the {upload.FileCount} of packages.nuget.begin.");
		}

		if (size < 0 || SyncPath.GetError(payload.Path) is not null || !upload.Paths.Add(payload.Path))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid or repeated file '{payload.Path}'.");
		}

		if (size > upload.TotalSize - upload.Started)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"The files are larger than the {upload.TotalSize} bytes of packages.nuget.begin.");
		}

		upload.HasNupkg |= string.Equals(payload.Path, upload.Nupkg, StringComparison.Ordinal);
		upload.Current = payload.Path;
		upload.Size = size;
		upload.Started += size;
		upload.Received = 0;
	}

	private NuGetStoredPayload? Append(string connectionId, Upload upload, NuGetChunkPayload payload)
	{
		if (upload.Current is null)
		{
			Start(upload, payload);
		}
		else if (!string.Equals(payload.Path, upload.Current, StringComparison.Ordinal) || payload.Offset != upload.Received || payload.Size is not null)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Chunk of '{payload.Path}' at {payload.Offset} is out of order.");
		}

		var path = upload.Current!;
		byte[] data;
		try
		{
			data = SyncData.Decode(payload.Data ?? string.Empty);
		}
		catch (FormatException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The chunk is not base64url.");
		}

		if (data.Length > PackageLimits.ChunkSize || upload.Received + data.Length > upload.Size)
		{
			throw new EnvelopeException(ErrorCodes.TooLarge, $"'{path}' is longer than its size {upload.Size}.");
		}

		_store.Append(upload.Temp, path, data, create: upload.Received == 0);
		upload.Received += data.Length;
		if (upload.Received == upload.Size)
		{
			upload.Complete++;
			upload.Current = null;
		}

		if (!payload.Last)
		{
			return null;
		}

		if (upload.Current is not null || upload.Complete != upload.FileCount || upload.Started != upload.TotalSize)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "The package arrived incomplete; it starts again on the next pass.");
		}

		if (!upload.HasNupkg)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"The package has no {upload.Nupkg}.");
		}

		if (_store.Sha512(upload.Temp, upload.Nupkg) != upload.ContentHash)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, ChangedMessage);
		}

		_store.Commit(upload.Temp, upload.Key.Id, upload.Key.Version);
		_uploads.TryRemove(new KeyValuePair<string, Upload>(connectionId, upload));
		return new NuGetStoredPayload(upload.Key.Id, upload.Key.Version);
	}

	/// <summary>A folder that cannot be deleted now is left for <see cref="IPackageStore.DeleteTemps"/> at the next start.</summary>
	private void Discard(Upload upload)
	{
		try
		{
			_store.Discard(upload.Temp);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			_logger.LogWarning(ex, "Could not delete the unfinished upload of {Id} {Version}", upload.Key.Id, upload.Key.Version);
		}
	}

	private EnvelopeException Finish(string connectionId, Upload upload, EnvelopeException error)
	{
		_uploads.TryRemove(new KeyValuePair<string, Upload>(connectionId, upload));
		Discard(upload);
		return error;
	}

	private sealed class Upload(string id, NuGetPackageKey key, string contentHash, int fileCount, long totalSize, string temp)
	{
		public string Id { get; } = id;

		public NuGetPackageKey Key { get; } = key;

		public string ContentHash { get; } = contentHash;

		/// <summary>Gets the lower-case <c>&lt;id&gt;.&lt;version&gt;.nupkg</c> that must be among the files.</summary>
		public string Nupkg { get; } = $"{key.Id}.{key.Version}.nupkg";

		public int FileCount { get; } = fileCount;

		public long TotalSize { get; } = totalSize;

		public string Temp { get; } = temp;

		/// <summary>Gets the paths of the files started so far (no two equal ignoring case: the store is on Windows).</summary>
		public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);

		/// <summary>Gets or sets the file being received, or null between files.</summary>
		public string? Current { get; set; }

		/// <summary>Gets or sets the size of <see cref="Current"/>.</summary>
		public long Size { get; set; }

		/// <summary>Gets or sets the bytes of <see cref="Current"/> received so far.</summary>
		public long Received { get; set; }

		/// <summary>Gets or sets the sum of the sizes of the files started so far.</summary>
		public long Started { get; set; }

		/// <summary>Gets or sets a value indicating whether <see cref="Nupkg"/> (exactly, lower case) is among the files.</summary>
		public bool HasNupkg { get; set; }

		/// <summary>Gets or sets how many files arrived whole.</summary>
		public int Complete { get; set; }

		public EnvelopeException? Error { get; set; }

		public bool Closed { get; set; }
	}
}
```

Note for the implementer: a file of size 0 is one empty chunk carrying size 0: its first chunk starts it, the empty append creates the file and `Received == Size` completes it — the upload test covers it. `begin` carries no file list (2 000 paths would exceed SignalR's 32 KB receive limit); each file is checked at its first chunk.

`PackagesHandler` (pattern of `ChatHandler`, types have/begin/chunk; deserialization errors → `bad_request` "Invalid {type} payload."; replies `packages.nuget.missing` / `packages.nuget.begun` / `packages.nuget.stored` with the request's correlation id; a non-last chunk returns null). DI (`AddApplication`): `services.AddSingleton<PackageUploads>(); foreach (var type in PackagesHandler.Types) { services.AddSingleton<IEnvelopeHandler>(sp => new PackagesHandler(type, sp.GetRequiredService<PackageUploads>())); }`. TransportHub: ctor param + `packages.Close(Context.ConnectionId);` in `OnDisconnectedAsync`.

- [ ] **Step 4: Run** — focused `*PackageUploadsTests`, `*ChatHubTests`, `*TransportHubTests`, `*DependencyInjectionTests` → PASS; full suite once.
- [ ] **Step 5: Commit** — "Packages: server uploads checked, hashed and put in place whole".

---

### Task 4: Agent environment and prompt line

**Files:**
- Create: `src/AiChromeProxy.Infrastructure/Chat/PackageEnvironment.cs`
- Modify: `src/AiChromeProxy.Infrastructure/Chat/ClaudeRunner.cs` (ctor gets `IOptions<PackagesOptions> packages`; in `StartAsync`: `var nuget = Path.Combine(_packagesRoot, "nuget"); var has = Directory.Exists(nuget);` then `Start(file, ClaudeArguments.Build(run, _options, has), run.RepoFolder, PackageEnvironment.With(_options.Env, Environment.GetEnvironmentVariable(PackageEnvironment.FallbackVariable), has ? nuget : null), ...)`), `src/AiChromeProxy.Infrastructure/Chat/ClaudeArguments.cs` (`Build(AgentRun run, AgentOptions options, bool privatePackages = false)`, `public const string PackagesHint = "<exact text from Global Constraints>";`, `--append-system-prompt` value = `privatePackages ? Convention + " " + PackagesHint : Convention`), `tests/AiChromeProxy.Tests/Infrastructure/ClaudeRunnerTests.cs` (the factory at line ~183 passes `Options.Create(new PackagesOptions { Root = <temp> })`)
- Test: `tests/AiChromeProxy.Tests/Infrastructure/PackageEnvironmentTests.cs`, `ClaudeArgumentsTests.cs`

```csharp
namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>The environment that lets a run restore private NuGet packages from the server's packages folder without touching the project's files.</summary>
public static class PackageEnvironment
{
	public const string FallbackVariable = "NUGET_FALLBACK_PACKAGES";

	/// <summary>
	/// <paramref name="configured"/> (<c>Agent:Env</c>) plus, when <paramref name="nugetFolder"/> is set: the folder appended to <see cref="FallbackVariable"/>
	/// (configured value, else <paramref name="inheritedFallback"/>), <c>RestoreIgnoreFailedSources=true</c>, <c>NuGetAudit=false</c> and
	/// <c>WarningsNotAsErrors=NU1801</c>, each unless configured. Names compare ignoring case, like Windows does.
	/// </summary>
	public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> configured, string? inheritedFallback, string? nugetFolder)
	{
		if (nugetFolder is null)
		{
			return configured;
		}

		var env = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
		var current = env.TryGetValue(FallbackVariable, out var set) ? set : inheritedFallback;
		env[FallbackVariable] = string.IsNullOrWhiteSpace(current) ? nugetFolder : current.TrimEnd(';') + ";" + nugetFolder;
		env.TryAdd("RestoreIgnoreFailedSources", "true");
		env.TryAdd("NuGetAudit", "false");
		env.TryAdd("WarningsNotAsErrors", "NU1801");
		return env;
	}
}
```

- [ ] **Step 1: Failing tests** — `PackageEnvironmentTests`: no folder → same dictionary instance; folder only → four variables with the exact values; configured `NUGET_FALLBACK_PACKAGES=C:\a;` → `C:\a;<folder>`; inherited `C:\b` used when not configured; configured `restoreignorefailedsources=false` (other case) kept and not duplicated; configured `WarningsNotAsErrors=CS1234` kept. `ClaudeArgumentsTests`: `Build(run, options, privatePackages: true)` ends with `["--append-system-prompt", Convention + " " + PackagesHint]` (then `options.Args`); default → `Convention` alone; `PackagesHint` equals the exact text.
- [ ] **Step 2: Run to see them fail.** **Step 3: Implement** (code above plus the edits listed). **Step 4: Run** focused `*PackageEnvironmentTests`, `*ClaudeArgumentsTests`, `*ClaudeRunnerTests` → PASS. **Step 5: Commit** — "Packages: runs restore private packages from the fallback folder".

---

### Task 5: Client logic

**Files:**
- Create: `src/AiChromeProxy.Client/Packages/ProjectAssets.cs`, `NuGetMetadata.cs`, `IPackageFolder.cs`, `PackageRow.cs`, `PackagesEngine.cs`
- Modify: `src/AiChromeProxy.Client/Sync/IFolderAccess.cs` (+ `Task<long?> ModifiedAsync(string path);` — last modified, ms since epoch, null when missing), `Sync/JsFolderAccess.cs` (wrapper → `modified`), `Scripts/fsaccess.ts` (`export async function modified(path: string): Promise<number | null>` walking like `readFile`, returning `(await handle.getFile()).lastModified`, null on `NotFoundError`/`TypeMismatchError`), `tests/AiChromeProxy.Tests/Client/FakeFolder.cs` (`Dictionary<string, long> Modified`; `ModifiedAsync` returns `Modified[path]` when set, else `Files.ContainsKey(path) ? 1 : null`), `tests/AiChromeProxy.Tests/Client/LoopbackServer.cs` (a `PackagesRoot`, `PackageUploads Packages`, its handlers in the router, `Close` in `Reconnect`, root deleted in `Dispose`)
- Test: `tests/AiChromeProxy.Tests/Client/ProjectAssetsTests.cs`, `tests/AiChromeProxy.Tests/Client/PackagesEngineTests.cs` (with `FakePackageFolder` in the same folder)

**Interfaces — Produces:**

```csharp
/// <summary>A package a project restored (lower case).</summary>
public sealed record PackageRef(string Id, string Version);

public static class ProjectAssets
{
	/// <summary>The <c>type: "package"</c> entries of <c>libraries</c> (key <c>Id/Version</c>), lower-cased and distinct; project references are ignored.</summary>
	/// <exception cref="FormatException">Not a <c>project.assets.json</c>.</exception>
	public static IReadOnlyList<PackageRef> Parse(string json);
}

public static class NuGetMetadata
{
	/// <summary><c>contentHash</c> and <c>source</c> of a <c>.nupkg.metadata</c>; null when it cannot be read as one.</summary>
	public static (string ContentHash, string? Source)? Parse(string json);
}

/// <summary>The NuGet cache folder picked in this browser (<c>Scripts/pkgaccess.ts</c>).</summary>
public interface IPackageFolder
{
	Task<string?> PickAsync();                       // folder name; null when the picker was cancelled
	Task<FolderGrant?> RestoreAsync();               // remembered folder, or null
	Task<bool> RequestAccessAsync();                 // needs a click
	Task ForgetAsync();
	Task<string?> ReadTextAsync(string path);        // null when missing
	Task<IReadOnlyList<FileMeta>?> ListAsync(string folder); // every file under it, paths relative to the folder (forward slashes); null when missing
	Task<byte[]> ReadChunkAsync(string path, long offset, int length);
}

public enum SourceState { None, Ready, NeedsPermission, NotFound }

public enum PackageState { OnServer, Uploading, Public, NotOnComputer, TooLarge, Failed }

/// <summary>One row of the Packages tab; <paramref name="Text"/> is what the row says (exact texts of the spec).</summary>
public sealed record PackageRow(string Id, string Version, PackageState State, string Text);

/// <summary>A project of the repo and why it contributes no packages, if so ("No restore yet", "Could not read obj/project.assets.json").</summary>
public sealed record ProjectRow(string Path, string? Problem);

public sealed class PackagesEngine(ITransport transport, SyncEngine sync, IFolderAccess repo, IPackageFolder cache, TimeProvider time)
{
	public event Action? Changed;
	public SourceState Source { get; }
	public string? SourceName { get; }
	public IReadOnlyList<ProjectRow> Projects { get; }
	public IReadOnlyList<PackageRow> Packages { get; }   // sorted by id, version
	public string Summary { get; }                        // "{n} private packages ready on the server · {m} missing" ("" without private packages)
	public Task InitializeAsync();                        // restores the source; refreshes after each finished sync pass (LastSync changed)
	public Task AddSourceAsync();                         // picker; replaces the source; refreshes
	public Task AllowAsync();                             // RequestAccessAsync; refreshes when granted
	public Task RemoveSourceAsync();                      // ForgetAsync; Source = None; rows lose their cache state
	public Task RefreshAsync(CancellationToken ct = default); // one pass (a pass already running → returns at once)
}
```

**Refresh pass (spell out in code, one method each):**
1. Nothing to do without `sync.Repo` or a connected transport.
2. Projects: every `sync.Files` path ending in `.csproj` (ignore case); assets path = `<dir>/obj/project.assets.json` (`obj/project.assets.json` at the root). `repo.ModifiedAsync` null → `ProjectRow(path, "No restore yet")`; same modified as cached → cached refs; else `ReadTextAsync` + `ProjectAssets.Parse` (`FormatException`/null → `"Could not read obj/project.assets.json"`).
3. Union of refs. Without a ready source every ref → `NotOnComputer` row with text "Not on this computer" (the view also says to add a source).
4. Per ref: `cache.ReadTextAsync($"{id}/{version}/.nupkg.metadata")` → null → `NotOnComputer`; `NuGetMetadata.Parse` null → `Failed: unreadable .nupkg.metadata`; public source → `Public`; else private candidate with its hash.
5. Candidates (first 2 000) → `packages.nuget.have` → missing set; the others → `OnServer`.
6. Each missing package in turn: `cache.ListAsync($"{id}/{version}")` → over 2 000 files or 250 MB → `TooLarge` ("Too large to upload ({size} > 250 MB)", size in MB rounded up to one decimal, e.g. "300.1 MB"; over 2 000 files: "Too large to upload ({n} files > 2 000)"); `.nupkg` first then the rest by path; `packages.nuget.begin` → `uploadId`; for each file, chunks of `PackageLimits.ChunkSize` read with `cache.ReadChunkAsync($"{id}/{version}/{path}", ...)` (a size-0 file → one empty chunk; a short read → `IOException("The package changed while it was uploaded.")`), non-last with `transport.SendAsync`, the very last with `transport.RequestAsync(..., ChatEngine.RequestTimeout, ct)`; row `Uploading {n}%` (raise `Changed` at most every 16 chunks); stored → `OnServer`; `RequestFailedException` → `Failed: {message}`; other exceptions → `Failed: {message}` (connection lost uses `SyncEngine.ConnectionLost`). Failed packages are tried again on the next pass.
7. Raise `Changed` at the end.

Hook: in `InitializeAsync`, subscribe `sync.Changed` and start `RefreshAsync` (fire and forget) when `sync.LastSync` differs from the last value seen; also after a source change. `RefreshAsync` uses a `SemaphoreSlim(1,1)` with `WaitAsync(0)` to skip overlapping passes.

- [ ] **Step 1: Failing tests.**
  - `ProjectAssetsTests`: packages from a realistic snippet (two packages, one project reference) → two lower-cased refs; duplicate keys across casing → one; missing `libraries` → empty; invalid JSON → `FormatException`; `NuGetMetadata.Parse` on the real format (`{"version":2,"contentHash":"...","source":"..."}`) and on garbage → null.
  - `PackagesEngineTests` (against `LoopbackServer`, a `FakeFolder` repo synced through a real `SyncEngine` like `ChatEngineTests` does, and a `FakePackageFolder` with `Dictionary<string, byte[]> Files`, `PickResult`, `Granted`, `Remembered`):
    - private package (cache has `.nupkg.metadata` with a corp source and the real sha512 of the `.nupkg` bytes, `.nupkg`, `lib/net10.0/a.dll` > 16 KB, an empty file) → after `RefreshAsync` row `OnServer`, files exist under `LoopbackServer.PackagesRoot\nuget\corp.a\1.2.3\` with the same bytes;
    - public package (`source` api.nuget.org) → `Public`, nothing sent for it;
    - package not in the cache → `NotOnComputer`; no assets file → project row "No restore yet"; bad assets → "Could not read obj/project.assets.json";
    - second refresh with the package on the server → no `begin` sent;
    - `.nupkg` bytes not matching the hash → `Failed: Changed while uploading; retried on the next pass.`;
    - package over 2 000 files → `TooLarge` and nothing sent;
    - no source → rows "Not on this computer", `Source == None`; `AddSourceAsync` with a picked folder → `Ready` and uploads; `RemoveSourceAsync` → `None`; restored-but-not-granted → `NeedsPermission`, `AllowAsync` → `Ready`;
    - summary text for 1 on the server and 1 missing.
- [ ] **Step 2: Run to see them fail.** **Step 3: Implement.** **Step 4: Run** focused classes + full suite. **Step 5: Commit** — "Packages: client finds the repo's packages and uploads the private ones".

---

### Task 6: Browser folder, Packages tab, wiring

**Files:**
- Create: `src/AiChromeProxy.Client/Scripts/pkgaccess.ts`, `src/AiChromeProxy.Client/Packages/JsPackageFolder.cs` (`[ExcludeFromCodeCoverage]`, imports `./js/pkgaccess.js` lazily like `JsChatView`), `src/AiChromeProxy.Client/Shell/PackagesView.razor`
- Modify: `src/AiChromeProxy.Client/Shell/Explorer.razor` (header becomes a tablist "Explorer" / "Packages"; body shows today's content or `<PackagesView Engine="Packages" />`; new parameter `PackagesEngine Packages`), `src/AiChromeProxy.Client/Pages/Home.razor` (inject `PackagesEngine`, `await Packages.InitializeAsync()` after `Chat.InitializeAsync()`, pass it to `Explorer`), `src/AiChromeProxy.Client/Program.cs` (`AddSingleton<JsPackageFolder>()`, `AddSingleton<IPackageFolder>(s => s.GetRequiredService<JsPackageFolder>())`, `AddSingleton<PackagesEngine>()`), `wwwroot/css/app.css`

`pkgaccess.ts` (strict, no `any`): its own IndexedDB database `aicp-packages`, store `handles`, key `nuget`; `pick()` → `showDirectoryPicker({ id: 'aicp-pkg', mode: 'read' })` (null on `AbortError`), stores the handle, returns its name; `restore()` → `{ name, granted }` or null; `requestAccess()`; `forget()`; `readText(path)`; `list(folder)` → `{ path, size, modified }[]` of every file under `folder` (recursive, paths relative to `folder` with `/`) or null when missing; `readChunk(path, offset, length)` → `Uint8Array` (walk the handles from the root each time). Path segments `''`, `.`, `..` or containing `\` throw.

`PackagesView.razor` (test ids: `packages-view`, `packages-add-source`, `packages-source`, `packages-allow`, `packages-remove-source`, `packages-summary`, `packages-row`, `packages-project-problem`):
- **Sources**: without a source a short text ("Add your NuGet cache folder, usually `%USERPROFILE%\.nuget\packages`, so that private packages reach the home server.") and **Add source** (`packages-add-source`; the only type for now is **NuGet cache folder**, shown as the button's label "Add NuGet cache folder"); with one: a row (`packages-source`) "NuGet cache · {name} · {Ready|Needs permission|Not found}" with **Allow** (needs permission) and **Remove**.
- **Packages this folder needs**: the summary (`packages-summary`), project problems (`packages-project-problem`: "{path}: {problem}"), then rows (`packages-row`, class by state): `{id}` `{version}` `{text}`.
- Note line: "Nothing in your project changes: the home server's Claude runs find these packages through environment variables."

Explorer tabs: `role="tablist"` in the header with two `role="tab"` buttons (`data-testid="left-tab-explorer"`, `left-tab-packages`), `aria-selected`, arrow keys switch (copy the MainArea tab strip markup and classes where they fit).

- [ ] **Steps:** implement; `dotnet build -c Release` (TypeScript compiles); full unit suite (`*BrowserScriptTests`, `*ClientMarkupTests` included); commit "Packages: Packages tab with the NuGet cache source".

---

### Task 7: E2E, docs, gate

**Files:**
- Modify: `tests/AiChromeProxy.E2E/Hooks/AppServer.cs` (`Packages:Root` → `<root>\packages`, property `PackagesRoot`), `Hooks/BrowserHooks.cs` (`PackagesRoot`), `tests/AiChromeProxy.E2E/Features/Packages.feature` (new), `StepDefinitions/PackagesSteps.cs` (new), `docs/packages.md` (new), `CLAUDE.md` (one bullet under Stack linking `docs/packages.md`), `docs/chat.md` (a line under Security: private packages on the server), the spec (no change expected)

Scenario:

```gherkin
Feature: Packages
	Private NuGet packages from the NuGet cache folder reach the home server; nothing in the project changes.

Scenario: A private package is uploaded and a public one is left to nuget.org
	Given the app is connected with a synced folder that restored a private and a public package
	When I open the Packages tab
	And I add the NuGet cache folder
	Then the package "corp.private" "1.2.3" says "On the server"
	And the package "humanizer.core" "2.14.1" says "Public"
	And the server holds "corp.private" "1.2.3"
	And I save package screenshots named "packages"
```

Steps: the `showDirectoryPicker` stub branches on `options?.id === 'aicp-pkg'` and returns an OPFS folder `e2e-nuget-<8 hex>` holding `corp.private/1.2.3/{corp.private.1.2.3.nupkg, corp.private.nuspec, .nupkg.metadata, lib/net10.0/corp.private.dll}` (the metadata's `contentHash` computed in the page with `crypto.subtle.digest('SHA-512', ...)` over the nupkg bytes, `source` `https://artifactory.corp.invalid/api/nuget/v3/corp/index.json`) and `humanizer.core/2.14.1/.nupkg.metadata` with source `https://api.nuget.org/v3/index.json`; the repo folder gets `app/app.csproj` and `app/obj/project.assets.json` whose `libraries` list `Corp.Private/1.2.3` and `Humanizer.Core/2.14.1` (type package). "the server holds" checks `<PackagesRoot>\nuget\corp.private\1.2.3\corp.private.1.2.3.nupkg` exists. Screenshots via `E2E_SCREENSHOT_DIR` like the chat steps; look at them (dark and light).

`docs/packages.md`: what the tab does; Add source (the folder to pick); what is uploaded (only private packages the repo restored, exact versions; nuget.org ones are downloaded by the server); **nothing in the project changes** — the four environment variables and the system prompt line; limits; where packages live (`Packages:Root`, never synced back; remove by hand); limitations (`packages.config`, `BaseIntermediateOutputPath`, a project's own `WarningsNotAsErrors`, npm later); **policy note**: private packages of your client are copied to your home server — check that your company allows it; manual checklist (pick the real `.nuget\packages`; a repo with a private package restores and builds in a Claude run; `git status` on the mirror and in your folder shows no changed config).

- [ ] **Steps:** E2E per `docs/testing.md` (all scenarios green); full gate (build + unit + coverlet 85 %); commit "Packages: E2E, docs". Do not push (the controller opens the PR).
