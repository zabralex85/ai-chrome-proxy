using System.Globalization;
using System.Text.Json;
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Packages;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Packages;

/// <summary>
/// The Packages tab's logic: after each sync pass it reads <c>obj/project.assets.json</c> next to every <c>*.csproj</c> of the repo, classifies each package
/// by its <c>.nupkg.metadata</c> in the NuGet cache folder of this browser, asks the server which private ones it lacks (<c>packages.nuget.have</c>) and
/// uploads those one at a time (<c>packages.nuget.begin</c>, then 16 KB chunks; only the last one waits for its reply). Nothing is ever written anywhere
/// on this computer. UI-free: the view reads the properties on <see cref="Changed"/>.
/// </summary>
public sealed class PackagesEngine(ITransport transport, SyncEngine sync, IFolderAccess repo, IPackageFolder cache)
{
	public const string NoRestore = "No restore yet";
	public const string AssetsUnreadable = "Could not read obj/project.assets.json";

	/// <summary>Progress is shown every this many chunks (256 KB), not on every chunk.</summary>
	private const int ProgressEvery = 16;

	private readonly Lock _gate = new();
	private readonly SemaphoreSlim _pass = new(1, 1);

	/// <summary>Parsed assets files by path, with the modified time they were read at (only touched inside a pass).</summary>
	private readonly Dictionary<string, (long Modified, IReadOnlyList<PackageRef> Refs)> _assets = new(StringComparer.Ordinal);
	private string? _assetsRepo;
	private IReadOnlyList<ProjectRow> _projects = [];
	private Dictionary<PackageRef, PackageRow> _rows = [];
	private DateTimeOffset? _lastSync;

	/// <summary>Changes with every source change: a pass started before it no longer writes rows.</summary>
	private int _sourceEpoch;
	private int _passEpoch;

	/// <summary>Raised after every visible change.</summary>
	public event Action? Changed;

	public SourceState Source { get; private set; }

	/// <summary>Gets the name of the picked cache folder, or null.</summary>
	public string? SourceName { get; private set; }

	/// <summary>Gets the repo's projects in path order, as of the last pass.</summary>
	public IReadOnlyList<ProjectRow> Projects
	{
		get
		{
			lock (_gate)
			{
				return _projects;
			}
		}
	}

	/// <summary>Gets the packages the repo's projects restored, sorted by id and version.</summary>
	public IReadOnlyList<PackageRow> Packages
	{
		get
		{
			lock (_gate)
			{
				return [.. _rows.Values.OrderBy(r => r.Id, StringComparer.Ordinal).ThenBy(r => r.Version, StringComparer.Ordinal)];
			}
		}
	}

	/// <summary>Gets "{n} private packages ready on the server · {m} missing" (missing: neither on the server nor public), or "" without such packages or a ready source.</summary>
	public string Summary
	{
		get
		{
			if (Source != SourceState.Ready)
			{
				return string.Empty;
			}

			var rows = Packages;
			var ready = rows.Count(r => r.State == PackageState.OnServer);
			var missing = rows.Count(r => r.State is not (PackageState.OnServer or PackageState.Public));
			return ready + missing == 0 ? string.Empty : $"{ready} private packages ready on the server · {missing} missing";
		}
	}

	/// <summary>Restores the remembered source and refreshes after each finished sync pass from now on.</summary>
	/// <returns>A task that completes when the source is restored.</returns>
	public async Task InitializeAsync()
	{
		var grant = await cache.RestoreAsync();
		SetSource(grant is null ? SourceState.None : grant.Granted ? SourceState.Ready : SourceState.NeedsPermission, grant?.Name);
		sync.Changed -= OnSyncChanged;
		sync.Changed += OnSyncChanged;
		Changed?.Invoke();
	}

	/// <summary>Shows the folder picker; a picked folder replaces the source and the packages are refreshed.</summary>
	/// <returns>A task that completes after the refresh.</returns>
	public async Task AddSourceAsync()
	{
		if (await cache.PickAsync() is not { } name)
		{
			return;
		}

		SetSource(SourceState.Ready, name);
		Changed?.Invoke();
		await RefreshAsync();
	}

	/// <summary>Asks the browser for access to the remembered source (from a click); refreshes when granted.</summary>
	/// <returns>A task that completes after the refresh.</returns>
	public async Task AllowAsync()
	{
		if (!await cache.RequestAccessAsync())
		{
			return;
		}

		SetSource(SourceState.Ready, SourceName);
		Changed?.Invoke();
		await RefreshAsync();
	}

	/// <summary>Forgets the source; the rows lose their cache state.</summary>
	/// <returns>A task that completes when the browser forgot it.</returns>
	public async Task RemoveSourceAsync()
	{
		await cache.ForgetAsync();
		SetSource(SourceState.None, null);
		lock (_gate)
		{
			_rows = NotOnComputer(_rows.Keys);
		}

		Changed?.Invoke();
	}

	/// <summary>One pass over the repo's packages; returns at once when a pass is already running.</summary>
	/// <param name="ct">Stops the pass.</param>
	/// <returns>A task that completes when the pass is done.</returns>
	public async Task RefreshAsync(CancellationToken ct = default)
	{
		if (!await _pass.WaitAsync(0, ct))
		{
			return;
		}

		try
		{
			await PassAsync(ct);
		}
		finally
		{
			_pass.Release();
		}
	}

	private static Dictionary<PackageRef, PackageRow> NotOnComputer(IEnumerable<PackageRef> refs) =>
		refs.ToDictionary(r => r, r => Row(r, PackageState.NotOnComputer, "Not on this computer"));

	private static PackageRow Row(PackageRef package, PackageState state, string text) => new(package.Id, package.Version, state, text);

	private static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(JsonSerializerOptions.Web)!;

	private async Task PassAsync(CancellationToken ct)
	{
		// 1. Nothing to do without an open repo or a connection.
		if (sync.Repo is not { } repoName || transport.State != TransportState.Connected)
		{
			return;
		}

		lock (_gate)
		{
			_passEpoch = _sourceEpoch;
		}

		// 2.-4. The repo's packages, classified by the cache.
		var refs = await ReadProjectsAsync(repoName);
		// A source not found is tried again: the folder may be back.
		var candidates = Source is SourceState.Ready or SourceState.NotFound ? await ClassifyAsync(refs) : [];
		if (Source != SourceState.Ready)
		{
			SetRows(NotOnComputer(refs));
		}

		Changed?.Invoke();

		// 5.-6. Which private ones the server lacks; those are uploaded one at a time.
		foreach (var (package, hash) in await MissingAsync(candidates, ct))
		{
			if (!IsCurrentPass())
			{
				break;
			}

			await UploadAsync(package, hash, ct);
			Changed?.Invoke();
		}

		// 7.
		Changed?.Invoke();
	}

	/// <summary>Step 2: the projects (<see cref="Projects"/>) and the union of their packages.</summary>
	private async Task<IReadOnlyList<PackageRef>> ReadProjectsAsync(string repoName)
	{
		if (_assetsRepo != repoName)
		{
			_assets.Clear();
			_assetsRepo = repoName;
		}

		var projects = new List<ProjectRow>();
		var refs = new HashSet<PackageRef>();
		foreach (var project in sync.Files.Select(f => f.Path).Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)))
		{
			var slash = project.LastIndexOf('/');
			var (found, problem) = await ReadAssetsAsync(project[..(slash + 1)] + "obj/project.assets.json");
			projects.Add(new ProjectRow(project, problem));
			refs.UnionWith(found);
		}

		lock (_gate)
		{
			_projects = projects;
		}

		return [.. refs];
	}

	/// <summary>An assets file's packages, re-read only when its modified time changed; or why there are none.</summary>
	private async Task<(IReadOnlyList<PackageRef> Refs, string? Problem)> ReadAssetsAsync(string path)
	{
		try
		{
			if (await repo.ModifiedAsync(path) is not { } modified)
			{
				return ([], NoRestore);
			}

			if (_assets.TryGetValue(path, out var cached) && cached.Modified == modified)
			{
				return (cached.Refs, null);
			}

			if (await repo.ReadTextAsync(path) is not { } text)
			{
				return ([], AssetsUnreadable);
			}

			var refs = ProjectAssets.Parse(text);
			_assets[path] = (modified, refs);
			return (refs, null);
		}
		catch (Exception)
		{
			// Invalid JSON, or the browser could not read it.
			return ([], AssetsUnreadable);
		}
	}

	/// <summary>Steps 3-4: every package's row from its <c>.nupkg.metadata</c>; the private ones (with their hash) are returned for the server.</summary>
	private async Task<List<(PackageRef Package, string Hash)>> ClassifyAsync(IReadOnlyList<PackageRef> refs)
	{
		var candidates = new List<(PackageRef, string)>();
		var rows = new Dictionary<PackageRef, PackageRow>();
		Dictionary<PackageRef, PackageRow> previous;
		lock (_gate)
		{
			previous = _rows;
		}

		foreach (var package in refs)
		{
			string? text;
			try
			{
				text = await cache.ReadTextAsync($"{package.Id}/{package.Version}/.nupkg.metadata");
			}
			catch (Exception)
			{
				await SourceLostAsync();
				SetRows(NotOnComputer(refs));
				return [];
			}

			if (Source == SourceState.NotFound)
			{
				SetSource(SourceState.Ready, SourceName, newEpoch: false);
			}

			if (text is null)
			{
				rows[package] = Row(package, PackageState.NotOnComputer, "Not on this computer");
			}
			else if (NuGetMetadata.Parse(text) is not { } metadata)
			{
				rows[package] = Row(package, PackageState.Failed, "Failed: unreadable .nupkg.metadata");
			}
			else if (NuGetNames.IsPublicSource(metadata.Source))
			{
				rows[package] = Row(package, PackageState.Public, "Public");
			}
			else
			{
				// Shown as before until the server answers.
				rows[package] = previous.GetValueOrDefault(package) ?? Row(package, PackageState.Uploading, "Uploading 0%");
				candidates.Add((package, metadata.ContentHash));
			}
		}

		SetRows(rows);
		return candidates;
	}

	/// <summary>The cache could not be read: is the permission gone (Needs permission) or the folder (Not found)?</summary>
	private async Task SourceLostAsync()
	{
		var grant = await cache.RestoreAsync();
		SetSource(grant is null ? SourceState.None : grant.Granted ? SourceState.NotFound : SourceState.NeedsPermission, grant?.Name, newEpoch: false);
	}

	/// <summary>Step 5: <c>packages.nuget.have</c> in batches; the rest are on the server.</summary>
	private async Task<List<(PackageRef Package, string Hash)>> MissingAsync(List<(PackageRef Package, string Hash)> candidates, CancellationToken ct)
	{
		var missing = new List<(PackageRef, string)>();
		foreach (var batch in candidates.Chunk(PackageLimits.MaxHavePackages))
		{
			try
			{
				var have = new NuGetHavePayload([.. batch.Select(c => new NuGetPackageInfo(c.Package.Id, c.Package.Version, c.Hash))]);
				var reply = Read<NuGetMissingPayload>(await transport.RequestAsync(Envelope.Create(MessageTypes.PackagesNuGetHave, have), ChatEngine.RequestTimeout, ct));
				var lacking = reply.Missing.Select(k => new PackageRef(k.Id, k.Version)).ToHashSet();
				foreach (var candidate in batch)
				{
					var absent = lacking.Contains(candidate.Package);
					SetRow(Row(candidate.Package, absent ? PackageState.Uploading : PackageState.OnServer, absent ? "Uploading 0%" : "On the server"));
					if (absent)
					{
						missing.Add(candidate);
					}
				}
			}
			catch (Exception ex) when (!ct.IsCancellationRequested)
			{
				foreach (var candidate in batch)
				{
					SetRow(Row(candidate.Package, PackageState.Failed, $"Failed: {Describe(ex)}"));
				}
			}
		}

		Changed?.Invoke();
		return missing;
	}

	/// <summary>Step 6: one package, <c>.nupkg</c> first, then its other files by path; a failure is tried again on the next pass.</summary>
	private async Task UploadAsync(PackageRef package, string hash, CancellationToken ct)
	{
		var folder = $"{package.Id}/{package.Version}";
		var nupkg = $"{package.Id}.{package.Version}.nupkg";
		try
		{
			var listed = await cache.ListAsync(folder);
			if (!IsCurrentPass())
			{
				return;
			}

			if (listed is null || !listed.Any(f => f.Path == nupkg))
			{
				SetRow(Row(package, PackageState.NotOnComputer, "Not on this computer"));
				return;
			}

			var size = listed.Sum(f => f.Size);
			if (listed.Count > PackageLimits.MaxPackageFiles)
			{
				SetRow(Row(package, PackageState.TooLarge, $"Too large to upload ({Format.Count(listed.Count)} files > {Format.Count(PackageLimits.MaxPackageFiles)})"));
				return;
			}

			if (size > PackageLimits.MaxPackageBytes)
			{
				// Rounded up, so it never reads "250 MB > 250 MB".
				var megabytes = Math.Ceiling(size * 10.0 / (1024 * 1024)) / 10;
				SetRow(Row(package, PackageState.TooLarge, string.Create(CultureInfo.InvariantCulture, $"Too large to upload ({megabytes:0.#} MB > 250 MB)")));
				return;
			}

			List<NuGetFileInfo> files = [.. listed.OrderBy(f => f.Path == nupkg ? 0 : 1).ThenBy(f => f.Path, StringComparer.Ordinal).Select(f => new NuGetFileInfo(f.Path, f.Size))];
			var begin = Envelope.Create(MessageTypes.PackagesNuGetBegin, new NuGetBeginPayload(package.Id, package.Version, hash, files));
			var uploadId = Read<NuGetBegunPayload>(await transport.RequestAsync(begin, ChatEngine.RequestTimeout, ct)).UploadId;
			long sent = 0;
			var chunks = 0;
			for (var i = 0; i < files.Count; i++)
			{
				var file = files[i];
				for (long offset = 0; ; offset += PackageLimits.ChunkSize)
				{
					if (!IsCurrentPass())
					{
						return; // the server drops the unfinished upload on the next begin or when the connection closes
					}

					var length = (int)Math.Min(PackageLimits.ChunkSize, file.Size - offset);
					var data = length == 0 ? [] : await cache.ReadChunkAsync($"{folder}/{file.Path}", offset, length);
					if (data.Length != length)
					{
						throw new IOException("The package changed while it was uploaded.");
					}

					var fileDone = offset + length == file.Size;
					var last = fileDone && i == files.Count - 1;
					var chunk = Envelope.Create(MessageTypes.PackagesNuGetChunk, new NuGetChunkPayload(uploadId, file.Path, offset, SyncData.Encode(data), last));
					if (last)
					{
						await transport.RequestAsync(chunk, ChatEngine.RequestTimeout, ct);
						SetRow(Row(package, PackageState.OnServer, "On the server"));
						return;
					}

					// Not awaited for a reply: the server answers only the last chunk (with the upload's first error, if any).
					await transport.SendAsync(chunk, ct);
					sent += length;
					if (++chunks % ProgressEvery == 0)
					{
						SetRow(Row(package, PackageState.Uploading, $"Uploading {sent * 100 / Math.Max(size, 1)}%"));
						Changed?.Invoke();
					}

					if (fileDone)
					{
						break;
					}
				}
			}
		}
		catch (Exception ex) when (!ct.IsCancellationRequested)
		{
			SetRow(Row(package, PackageState.Failed, $"Failed: {Describe(ex)}"));
		}
	}

	private string Describe(Exception ex) => transport.State == TransportState.Connected ? ex.Message : SyncEngine.ConnectionLost;

	/// <summary>Refreshes once per finished sync pass.</summary>
	private void OnSyncChanged()
	{
		var last = sync.LastSync;
		lock (_gate)
		{
			if (last == _lastSync)
			{
				return;
			}

			_lastSync = last;
		}

		if (last is not null)
		{
			_ = RefreshQuietlyAsync();
		}
	}

	private async Task RefreshQuietlyAsync()
	{
		try
		{
			await RefreshAsync();
		}
		catch (Exception)
		{
			// The next sync pass tries again.
		}
	}

	private void SetSource(SourceState state, string? name, bool newEpoch = true)
	{
		lock (_gate)
		{
			Source = state;
			SourceName = name;
			if (newEpoch)
			{
				_sourceEpoch++;
			}
		}
	}

	private bool IsCurrentPass()
	{
		lock (_gate)
		{
			return _passEpoch == _sourceEpoch;
		}
	}

	/// <summary>Replaces every row, unless the source changed since the pass started.</summary>
	private void SetRows(Dictionary<PackageRef, PackageRow> rows)
	{
		lock (_gate)
		{
			if (_passEpoch == _sourceEpoch)
			{
				_rows = rows;
			}
		}
	}

	private void SetRow(PackageRow row)
	{
		lock (_gate)
		{
			if (_passEpoch == _sourceEpoch)
			{
				_rows[new PackageRef(row.Id, row.Version)] = row;
			}
		}
	}
}
