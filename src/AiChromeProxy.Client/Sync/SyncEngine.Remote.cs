using System.Security.Cryptography;
using System.Text;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

/// <summary>
/// The back channel: server changes (<c>sync.remote</c>) are queued as they arrive and applied at the start of a cycle under the hash-guard
/// (the file as it is now must still be the version both sides agreed on), fetched with <c>sync.fetch</c> and acknowledged with <c>sync.ack</c>.
/// A file changed on both sides becomes a conflict (<b>Keep mine</b> / <b>Take server's</b>). Every write runs inside the cycle guard.
/// </summary>
public sealed partial class SyncEngine
{
	/// <summary>Largest server file <see cref="ServerTextAsync"/> previews.</summary>
	private const int MaxPreviewSize = 256 * 1024;

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>Server changes not applied yet, by path.</summary>
	private readonly Dictionary<string, RemoteItem> _remote = new(StringComparer.Ordinal);

	private IReadOnlyList<RemoteItem> _remoteList = [];

	/// <summary>The rules of the last scan (null before the first): a server change of a path they exclude is never written.</summary>
	private IgnoreRules? _rules;

	/// <summary>Server changes not applied yet (waiting or in conflict), sorted by path; a new list whenever it changes.</summary>
	public IReadOnlyList<RemoteItem> Remote => _remoteList;

	/// <summary>Items of <see cref="Remote"/> in <see cref="RemoteStatus.Conflict"/>.</summary>
	public int ConflictCount { get; private set; }

	/// <summary>The browser's last answer to "may the folder be written".</summary>
	public bool CanWrite { get; private set; }

	/// <summary>The project's settings, from <c>sync.opened</c> or <see cref="SaveSettingsAsync"/>.</summary>
	public ProjectSettings Settings { get; private set; } = ProjectSettings.Default;

	/// <summary>Asks for write access (call it straight from the <b>Allow writing</b> click); the next cycle, started now, writes what waits.</summary>
	public async Task AllowWritingAsync()
	{
		CanWrite = await folder.RequestWriteAccessAsync();
		Raise();
		Wake();
	}

	/// <summary><b>Apply</b> (a path) / <b>Apply all</b> (null): the waiting changes under the hash-guard, also while automatic apply is off.</summary>
	public Task ApplyAsync(string? path) => ExclusiveAsync(async (repo, generation) =>
	{
		CanWrite = await folder.HasWriteAccessAsync();
		if (CanWrite)
		{
			await ApplyWaitingAsync(repo, generation, path, CancellationToken.None);
		}
	});

	/// <summary><b>Keep mine</b>: the server's version counts as seen, so the next delta uploads this folder's version (or its deletion).</summary>
	public Task KeepMineAsync(string path) => ExclusiveAsync(async (repo, _) =>
	{
		if (_remote.GetValueOrDefault(path) is { } item)
		{
			await AckAsync(repo, item, CancellationToken.None);
		}
	});

	/// <summary><b>Take server's</b>: writes (or deletes) the server's version without the guard; asks for write access first when needed.</summary>
	public async Task TakeServersAsync(string path)
	{
		if (!CanWrite)
		{
			await AllowWritingAsync();
		}

		await ExclusiveAsync(async (repo, _) =>
		{
			if (CanWrite && _remote.GetValueOrDefault(path) is { } item && await WriteChangeAsync(repo, item.Change, CancellationToken.None))
			{
				await AckAsync(repo, item, CancellationToken.None);
			}
		});
	}

	/// <summary>The server's version of a file as text, for the conflict preview; null when it is larger than 256 KB, not UTF-8, or cannot be fetched.</summary>
	public async Task<string?> ServerTextAsync(string path)
	{
		if (_repo is not { } repo)
		{
			return null;
		}

		try
		{
			return await FetchAsync(repo, path, MaxPreviewSize, CancellationToken.None) is { } content ? StrictUtf8.GetString(content) : null;
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			return null;
		}
	}

	/// <summary>Saves the project settings on the server (<c>project.settings.set</c>), then rescans with them.</summary>
	/// <exception cref="InvalidOperationException">No session is open (not connected).</exception>
	public async Task SaveSettingsAsync(ProjectSettings settings)
	{
		var repo = _repo ?? throw new InvalidOperationException(ConnectionLost);
		var saved = Read<ProjectSettingsPayload>(await RequestAsync(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload(repo, settings), CancellationToken.None));
		Settings = saved.Settings ?? settings;
		Raise();
		Wake();
	}

	/// <summary>A folder change: nothing of the previous folder's server changes or settings applies.</summary>
	private void ResetRemote()
	{
		_remote.Clear();
		RemoteChanged();
		_rules = null;
		CanWrite = false;
		Settings = ProjectSettings.Default;
	}

	/// <summary>A new session: its full manifest pushes again whatever still differs; conflicts stay until resolved.</summary>
	private void ForgetWaiting()
	{
		foreach (var item in _remoteList.Where(r => r.Status == RemoteStatus.Waiting))
		{
			_remote.Remove(item.Change.Path);
		}

		RemoteChanged();
	}

	/// <summary>Queues a push of the open repo (a newer change replaces an older one; a conflict stays one) and wakes the loop.</summary>
	private void OnReceived(Envelope envelope)
	{
		if (envelope.Type != MessageTypes.SyncRemote || _repo is not { } repo || Read<SyncRemotePayload>(envelope) is not { } payload || payload.Repo != repo)
		{
			return;
		}

		foreach (var change in payload.Changes ?? [])
		{
			// An upload the server refused because of this change is decided afresh, not backed off.
			_failures.Remove(change.Path);
			var old = _remote.GetValueOrDefault(change.Path);
			_remote[change.Path] = old is { Status: RemoteStatus.Conflict } ? old with { Change = change } : new RemoteItem(change, RemoteStatus.Waiting, null);
		}

		RemoteChanged();
		Raise();
		Wake();
	}

	/// <summary>At the start of a cycle: applies the waiting changes when the folder may be written and automatic apply is on.</summary>
	private async Task ApplyRemoteAsync(int generation, CancellationToken ct)
	{
		if (_repo is not { } repo || !_remoteList.Any(r => r.Status == RemoteStatus.Waiting))
		{
			return;
		}

		CanWrite = await folder.HasWriteAccessAsync();
		if (CanWrite && Settings.ApplyServerChangesOrDefault)
		{
			await ApplyWaitingAsync(repo, generation, null, ct);
		}
	}

	/// <summary>
	/// The hash-guard, per waiting change in path order: the file as it is now equals the change → acknowledge only; equals the agreed base
	/// (absent when there is none) → write or delete, then acknowledge; anything else → conflict. Never writes a path this folder excludes
	/// or <see cref="SyncPath"/> rejects. Losing write access stops it with the rest still waiting.
	/// </summary>
	private async Task ApplyWaitingAsync(string repo, int generation, string? only, CancellationToken ct)
	{
		List<string> received = [];
		List<string> deleted = [];
		try
		{
			foreach (var item in _remoteList.Where(r => r.Status == RemoteStatus.Waiting && (only is null || r.Change.Path == only)))
			{
				EnsureCurrent(generation, repo);
				var change = item.Change;
				if (!SyncPath.IsValid(change.Path) || _rules?.IsIgnored(change.Path) != false)
				{
					Update(item, null);
					Log(SyncActivityKind.Error, $"Not written: the server sent '{change.Path}', which this folder excludes or cannot hold.");
					continue;
				}

				try
				{
					var now = await folder.HashNowAsync(change.Path);
					if (now != change.Sha256 && now != change.Base)
					{
						Update(item, item with { Status = RemoteStatus.Conflict, Local = now });
						Log(SyncActivityKind.Conflict, $"Conflict: {change.Path} changed here and on the server.");
						continue;
					}

					if (now != change.Sha256)
					{
						if (!await WriteChangeAsync(repo, change, ct))
						{
							Update(item, null);
							continue;
						}

						(change.Sha256 is null ? deleted : received).Add(change.Path);
					}

					await AckAsync(repo, item, ct);
				}
				catch (Exception ex) when (ex is RequestFailedException or JSException or IOException or FormatException)
				{
					EnsureFolder(generation);
					if (ex is JSException && !(CanWrite = await folder.HasWriteAccessAsync()))
					{
						return;
					}

					Update(item, null);
					Log(SyncActivityKind.Error, $"Could not apply the server's version of {change.Path}: {ex.Message}");
				}
			}
		}
		finally
		{
			if (received.Count > 0)
			{
				Log(SyncActivityKind.Received, Group("Received", received, " from the server"));
			}

			if (deleted.Count > 0)
			{
				Log(SyncActivityKind.Received, Group("Deleted", deleted, " on the server"));
			}
		}
	}

	/// <returns>False when the fetched file is not the change's version (it changed again; its newer push follows).</returns>
	private async Task<bool> WriteChangeAsync(string repo, RemoteChange change, CancellationToken ct)
	{
		if (change.Sha256 is null)
		{
			await folder.DeleteAsync(change.Path);
			return true;
		}

		var content = await FetchAsync(repo, change.Path, SyncLimits.MaxFileSize, ct);
		if (content is null || Convert.ToHexStringLower(SHA256.HashData(content)) != change.Sha256)
		{
			return false;
		}

		await folder.WriteAsync(change.Path, content);
		return true;
	}

	/// <summary>The mirror file, one <c>sync.fetch</c> per part (ponytail: sequential; pipeline like the uploads if large files are slow).</summary>
	/// <returns>Its bytes, or null when it is larger than <paramref name="max"/>.</returns>
	private async Task<byte[]?> FetchAsync(string repo, string path, long max, CancellationToken ct)
	{
		using (var content = new MemoryStream())
		{
			while (true)
			{
				var part = Read<SyncDataPayload>(await RequestAsync(MessageTypes.SyncFetch, new SyncFetchPayload(repo, path, content.Length), ct));
				var data = SyncData.Decode(part.Data);
				if (content.Length + data.Length > max)
				{
					return null;
				}

				content.Write(data);
				if (part.Last)
				{
					return content.ToArray();
				}

				if (data.Length == 0)
				{
					throw new IOException($"The server sent an empty part of '{path}'.");
				}
			}
		}
	}

	/// <summary>
	/// Tells the server the folder has the change's version now (or, for Keep mine, that it has seen it): it becomes the agreed base, and
	/// what the server has as far as this page knows.
	/// </summary>
	private async Task AckAsync(string repo, RemoteItem item, CancellationToken ct)
	{
		var change = item.Change;
		await RequestAsync(MessageTypes.SyncAck, new SyncAckPayload(repo, change.Path, change.Sha256), ct);
		if (change.Sha256 is null)
		{
			_known.Remove(change.Path);
		}
		else
		{
			_known[change.Path] = new ManifestEntry(change.Path, change.Size, change.Sha256);
		}

		if (_remote.GetValueOrDefault(change.Path) is { } current && current != item)
		{
			// A newer push arrived meanwhile, decided against the base this ack just replaced.
			if (current.Change.Base == change.Base)
			{
				Update(current, current with { Change = current.Change with { Base = change.Sha256 } });
			}
		}
		else
		{
			Update(item, null);
		}
	}

	/// <summary>Replaces <paramref name="item"/> with <paramref name="next"/> (null removes it), unless a newer push replaced it meanwhile.</summary>
	private void Update(RemoteItem item, RemoteItem? next)
	{
		var path = item.Change.Path;
		if (_remote.GetValueOrDefault(path) != item)
		{
			return;
		}

		if (next is null)
		{
			_remote.Remove(path);
		}
		else
		{
			_remote[path] = next;
		}

		RemoteChanged();
	}

	private void RemoteChanged()
	{
		_remoteList = [.. _remote.Values.OrderBy(r => r.Change.Path, StringComparer.Ordinal)];
		ConflictCount = _remoteList.Count(r => r.Status == RemoteStatus.Conflict);
	}

	/// <summary>
	/// Runs a user's action (Apply, Keep mine, Take server's) inside the cycle guard, so it never races a scan, then wakes the loop; a failure
	/// goes to the Actions history instead of the click handler. No-op while no session is open.
	/// </summary>
	private async Task ExclusiveAsync(Func<string, int, Task> action)
	{
		// ponytail: polls a running cycle every 50 ms; a cycle is short and the click is rare.
		while (Interlocked.Exchange(ref _cycleRunning, 1) == 1)
		{
			await Task.Delay(50);
		}

		try
		{
			if (_repo is { } repo)
			{
				await action(repo, _generation);
			}
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			if (ex is JSException)
			{
				CanWrite = await folder.HasWriteAccessAsync();
			}

			Log(SyncActivityKind.Error, ex.Message);
		}
		finally
		{
			Volatile.Write(ref _cycleRunning, 0);
			Raise();
			Wake();
		}
	}
}
