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
