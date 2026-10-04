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
	/// <summary>
	/// Waiting server deletions beyond this many (and beyond <see cref="MassDeleteShare"/> of the synced files) are held for <b>Apply all</b>,
	/// even with automatic apply on: a mistake on the server must not empty the folder unseen.
	/// </summary>
	public const int MassDeleteCount = 20;

	/// <summary>Share of the synced files that waiting server deletions must also exceed to be held (see <see cref="MassDeleteCount"/>).</summary>
	public const double MassDeleteShare = 0.10;

	/// <summary>Largest server file <see cref="ServerTextAsync"/> previews.</summary>
	private const int MaxPreviewSize = 256 * 1024;

	private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

	/// <summary>Server changes not applied yet, by path.</summary>
	private readonly Dictionary<string, RemoteItem> _remote = new(StringComparer.Ordinal);

	private IReadOnlyList<RemoteItem> _remoteList = [];

	/// <summary>Whether the deletions held now were logged (once per hold).</summary>
	private bool _heldLogged;

	/// <summary>The rules of the last scan (null before the first): a server change of a path they exclude is never written.</summary>
	private IgnoreRules? _rules;

	/// <summary>Server changes not applied yet (waiting or in conflict), sorted by path; a new list whenever it changes.</summary>
	public IReadOnlyList<RemoteItem> Remote => _remoteList;

	/// <summary>Items of <see cref="Remote"/> in <see cref="RemoteStatus.Conflict"/>.</summary>
	public int ConflictCount { get; private set; }

	/// <summary>
	/// Whether the waiting server deletions are more than <see cref="MassDeleteCount"/> and more than <see cref="MassDeleteShare"/> of the
	/// synced files: automatic apply leaves them for <b>Apply all</b>.
	/// </summary>
	public bool DeletionsHeld =>
		_remoteList.Count(r => r.Status == RemoteStatus.Waiting && r.Change.Sha256 is null) is var deletes
		&& deletes > MassDeleteCount
		&& deletes > _known.Count * MassDeleteShare;

	/// <summary>The browser's last answer to "may the folder be written".</summary>
	public bool CanWrite { get; private set; }

	/// <summary>The project's settings, from <c>sync.opened</c> or <see cref="SaveSettingsAsync"/>.</summary>
	public ProjectSettings Settings { get; private set; } = ProjectSettings.Default;

	/// <summary>Asks for write access (call it straight from the <b>Allow writing</b> click); the next cycle, started now, writes what waits.</summary>
	public async Task AllowWritingAsync()
	{
		try
		{
			CanWrite = await folder.RequestWriteAccessAsync();
		}
		catch (JSException ex)
		{
			CanWrite = false;
			ClickFailed("Could not get write access to the folder", ex);
			return;
		}

		Raise();
		Wake();
	}

	/// <summary><b>Apply</b> (a path) / <b>Apply all</b> (null): the waiting changes under the hash-guard, also while automatic apply is off.</summary>
	public Task ApplyAsync(string? path) => ExclusiveAsync(async (repo, generation) =>
	{
		CanWrite = await folder.HasWriteAccessAsync();
		if (CanWrite)
		{
			await ApplyWaitingAsync(repo, generation, c => path is null || c.Path == path, CancellationToken.None);
		}
		else
		{
			Log(SyncActivityKind.Error, "Server changes wait: allow writing to the folder first.");
		}
	});

	/// <summary>
	/// <b>Keep mine</b> of a conflict (no-op for anything else): the server's version counts as seen, so the next delta uploads this
	/// folder's version (or its deletion).
	/// </summary>
	public Task KeepMineAsync(string path) => ExclusiveAsync(async (repo, _) =>
	{
		if (Conflicted(path) is { } item && !Refused(item))
		{
			await AckAsync(repo, item, CancellationToken.None);
		}
	});

	/// <summary>
	/// <b>Take server's</b> of a conflict (no-op for anything else): writes (or deletes) the server's version without the guard; asks for
	/// write access first when needed.
	/// </summary>
	public async Task TakeServersAsync(string path)
	{
		if (Conflicted(path) is null)
		{
			return;
		}

		if (!CanWrite)
		{
			await AllowWritingAsync();
		}

		await ExclusiveAsync(async (repo, _) =>
		{
			if (CanWrite && Conflicted(path) is { } item && !Refused(item) && await WriteChangeAsync(repo, item.Change, CancellationToken.None))
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

	/// <summary>
	/// Reads the project settings again (<c>project.settings.get</c>): a chat's <b>Allow always</b> or another tab may have changed them
	/// since <c>sync.opened</c>. A change of the excludes rescans.
	/// </summary>
	/// <exception cref="InvalidOperationException">No session is open (not connected).</exception>
	public async Task<ProjectSettings> ReloadSettingsAsync()
	{
		var repo = _repo ?? throw new InvalidOperationException(ConnectionLost);
		var current = Read<ProjectSettingsPayload>(await RequestAsync(MessageTypes.ProjectSettingsGet, new ProjectSettingsPayload(repo), CancellationToken.None)).Settings ?? ProjectSettings.Default;
		var rescan = current.Excludes != Settings.Excludes;
		Settings = current;
		Raise();
		if (rescan)
		{
			Wake();
		}

		return current;
	}

	/// <summary>
	/// Saves <paramref name="change"/> of the settings read again right before (<see cref="ReloadSettingsAsync"/>), so that what changed on
	/// the server since the form loaded (an <b>Allow always</b> rule) is not overwritten.
	/// </summary>
	/// <param name="change">Applies the user's edits to the current settings.</param>
	/// <exception cref="InvalidOperationException">No session is open (not connected).</exception>
	public async Task SaveSettingsAsync(Func<ProjectSettings, ProjectSettings> change) =>
		await SaveSettingsAsync(change(await ReloadSettingsAsync()));

	private static bool IsVersion(byte[]? content, string sha256) => content is not null && Convert.ToHexStringLower(SHA256.HashData(content)) == sha256;

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

	/// <summary>
	/// At the start of a cycle: applies the waiting changes when the folder may be written and automatic apply is on; many deletions
	/// (<see cref="DeletionsHeld"/>) keep waiting for <b>Apply all</b>.
	/// </summary>
	private async Task ApplyRemoteAsync(int generation, CancellationToken ct)
	{
		if (_repo is not { } repo || !_remoteList.Any(r => r.Status == RemoteStatus.Waiting))
		{
			return;
		}

		CanWrite = await folder.HasWriteAccessAsync();
		if (CanWrite && Settings.ApplyServerChangesOrDefault)
		{
			var held = DeletionsHeld;
			if (held && !_heldLogged)
			{
				var deletes = _remoteList.Count(r => r.Status == RemoteStatus.Waiting && r.Change.Sha256 is null);
				Log(SyncActivityKind.Received, $"The server deleted {deletes} files; review and click Apply all to delete them here.");
			}

			_heldLogged = held;
			await ApplyWaitingAsync(repo, generation, c => !held || c.Sha256 is not null, ct);
		}
	}

	/// <summary>
	/// The hash-guard, per waiting change in path order: the file as it is now equals the change → acknowledge only; equals the agreed base
	/// (absent when there is none) → write or delete, then acknowledge; anything else → conflict. Never writes a path this folder excludes
	/// or <see cref="SyncPath"/> rejects. Losing write access stops it with the rest still waiting.
	/// </summary>
	private async Task ApplyWaitingAsync(string repo, int generation, Func<RemoteChange, bool> select, CancellationToken ct)
	{
		List<string> received = [];
		List<string> deleted = [];
		try
		{
			foreach (var item in _remoteList.Where(r => r.Status == RemoteStatus.Waiting && select(r.Change)))
			{
				EnsureCurrent(generation, repo);
				var change = item.Change;
				if (Refused(item))
				{
					continue;
				}

				var written = false;
				try
				{
					var now = await folder.HashNowAsync(change.Path);
					byte[]? content = null;
					if (now == change.Base && change.Sha256 is not null)
					{
						content = await FetchAsync(repo, change.Path, SyncLimits.MaxFileSize, ct);
						if (!IsVersion(content, change.Sha256))
						{
							Update(item, null);
							continue;
						}

						// The folder again, after the fetch's round trips: a save made meanwhile wins (a conflict), it is never overwritten.
						now = await folder.HashNowAsync(change.Path);
					}

					if (now != change.Sha256 && now != change.Base)
					{
						Update(item, item with { Status = RemoteStatus.Conflict, Local = now });
						Log(SyncActivityKind.Conflict, $"Conflict: {change.Path} changed here and on the server.");
						continue;
					}

					if (now != change.Sha256)
					{
						if (content is null)
						{
							await folder.DeleteAsync(change.Path);
							WriteCount++;
							deleted.Add(change.Path);
						}
						else
						{
							await folder.WriteAsync(change.Path, content);
							WriteCount++;
							received.Add(change.Path);
						}

						written = true;
					}

					await AckAsync(repo, item, ct);
				}
				catch (Exception ex) when (ex is RequestFailedException or JSException or IOException or FormatException)
				{
					EnsureFolder(generation);
					if (written)
					{
						// The folder has the server's version: known as such, so it is not uploaded back; the next full manifest agrees the base.
						SetKnown(change);
						Update(item, null);
						Log(SyncActivityKind.Error, $"Wrote {change.Path}, but the server did not take the confirmation: {ex.Message}");
						continue;
					}

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
			WriteCount++;
			return true;
		}

		var content = await FetchAsync(repo, change.Path, SyncLimits.MaxFileSize, ct);
		if (!IsVersion(content, change.Sha256))
		{
			return false;
		}

		await folder.WriteAsync(change.Path, content!);
		WriteCount++;
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
		SetKnown(change);

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

	/// <summary>What the server has, as far as this page knows: the change's version.</summary>
	private void SetKnown(RemoteChange change)
	{
		if (change.Sha256 is null)
		{
			_known.Remove(change.Path);
		}
		else
		{
			_known[change.Path] = new ManifestEntry(change.Path, change.Size, change.Sha256);
		}
	}

	/// <summary>The item of <paramref name="path"/> when it is a conflict; null otherwise.</summary>
	private RemoteItem? Conflicted(string path) => _remote.GetValueOrDefault(path) is { Status: RemoteStatus.Conflict } item ? item : null;

	/// <summary>
	/// Drops a change of a path this folder excludes (current rules, settings included) or <see cref="SyncPath"/> rejects, with an
	/// Actions-history error: such a path is never written or acknowledged.
	/// </summary>
	private bool Refused(RemoteItem item)
	{
		var path = item.Change.Path;
		if (SyncPath.IsValid(path) && _rules?.IsIgnored(path) == false)
		{
			return false;
		}

		Update(item, null);
		Log(SyncActivityKind.Error, $"Not written: the server sent '{path}', which this folder excludes or cannot hold.");
		return true;
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
