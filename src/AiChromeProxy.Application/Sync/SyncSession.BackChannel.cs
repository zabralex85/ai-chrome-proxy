using System.Text;
using System.Text.Json;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Sync;

/// <summary>The server → client half: decisions with the bases, pushes (<c>sync.remote</c>), <c>sync.fetch</c>, <c>sync.ack</c> and mirror changes.</summary>
public sealed partial class SyncSession
{
	/// <summary>Mirror files too large to push that were already logged (path and size), so each is logged once.</summary>
	private readonly HashSet<(string Path, long Size)> _tooLarge = [];

	/// <summary>
	/// Re-decides these mirror paths (null: every mirror file and every base) with the client's last agreed state, the base, and pushes
	/// those the mirror moved away from. No-op before <c>sync.open</c> and after <see cref="Dispose"/>; pushes go to the context of the last
	/// handled message.
	/// </summary>
	public async Task MirrorChangedAsync(IReadOnlyCollection<string>? paths, CancellationToken ct)
	{
		await _gate.WaitAsync(ct);
		try
		{
			if (_disposed || _repo is not { } repo)
			{
				return;
			}

			var batch = await BeginAsync(repo, ct);
			var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			IReadOnlyList<string>? everything = null;
			IReadOnlyList<string> Everything() => everything ??= [.. store.ListFiles(repo).Union(batch.Bases.Keys, StringComparer.OrdinalIgnoreCase)];

			foreach (var path in paths ?? Everything())
			{
				var isFolder = !await DecideMirrorPathAsync(batch, path, done, ct);
				if (isFolder && paths is not null && !batch.Bases.ContainsKey(path))
				{
					// Neither a file nor a known path: a folder renamed or deleted, whose files get no events of their own.
					var prefix = path + "/";
					foreach (var inside in Everything().Where(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
					{
						await DecideMirrorPathAsync(batch, inside, done, ct);
					}
				}
			}

			await FinishAsync(batch, ct);
		}
		finally
		{
			_gate.Release();
			DiscardIfDisposed();
		}
	}

	/// <summary>Pushes the path if the mirror moved away from its base.</summary>
	/// <returns>False when the path is not a file in the mirror (a gone file or a folder); true otherwise, also when it was skipped.</returns>
	private async Task<bool> DecideMirrorPathAsync(Batch batch, string path, HashSet<string> done, CancellationToken ct)
	{
		if (!SyncPath.IsValid(path) || batch.Rules.IsIgnored(path) || !done.Add(path))
		{
			return true;
		}

		if (await TryGetHashAsync(batch.Repo, path, ct) is not (true, var mirror))
		{
			return true;
		}

		var baseHash = batch.Bases.GetValueOrDefault(path);
		if (SyncDecision.Decide(baseHash, mirror, baseHash, baselined: true) == SyncAction.Push)
		{
			// An upload of it requested earlier, or under way, must not overwrite the server's version.
			_expected.Remove(path);
			if (string.Equals(_upload?.Entry.Path, path, StringComparison.OrdinalIgnoreCase))
			{
				DiscardUpload();
			}

			AddPush(batch, path, mirror, baseHash);
		}

		return mirror is not null;
	}

	private async Task<Envelope> FetchAsync(SyncFetchPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var path = CheckPath(payload.Path);
		if (payload.Offset < 0)
		{
			throw BadRequest("sync.fetch needs an offset of 0 or more.");
		}

		if ((await RulesAsync(repo, ct)).IsIgnored(path))
		{
			throw BadRequest($"'{path}' is excluded from sync.");
		}

		var data = await store.ReadAsync(repo, path, payload.Offset, SyncLimits.ChunkSize, ct)
			?? throw new EnvelopeException(ErrorCodes.NotFound, $"'{path}' is not in the mirror.");

		// A file ending exactly at a chunk boundary gets one more, empty, last chunk.
		var last = data.Length < SyncLimits.ChunkSize;
		var sha256 = last ? await store.GetHashAsync(repo, path, ct) : null;
		return Reply(MessageTypes.SyncData, new SyncDataPayload(repo, path, payload.Offset, SyncData.Encode(data), last, sha256), request);
	}

	private async Task<Envelope> AckAsync(SyncAckPayload payload, Envelope request, CancellationToken ct)
	{
		var repo = CheckRepo(payload.Repo);
		var path = CheckPath(payload.Path);
		if (payload.Sha256 is not null && !IsSha256(payload.Sha256))
		{
			throw BadRequest($"sync.ack of '{path}' needs a SHA-256 as 64 lower-case hex characters, or null.");
		}

		if ((await RulesAsync(repo, ct)).IsIgnored(path))
		{
			throw BadRequest($"'{path}' is excluded from sync.");
		}

		projects.SetBases(repo, [new(path, payload.Sha256)]);
		return Reply(MessageTypes.SyncAck, payload, request);
	}

	/// <summary>The mirror's <c>.gitignore</c> and the project's extra excludes: excluded files are never deleted, pushed or served.</summary>
	private async Task<IgnoreRules> RulesAsync(string repo, CancellationToken ct)
	{
		var gitignore = await store.ReadAsync(repo, ".gitignore", 0, (int)SyncLimits.MaxFileSize, ct);
		return IgnoreRules.Create(gitignore is null ? null : Encoding.UTF8.GetString(gitignore), projects.GetSettings(repo).Excludes);
	}

	private async Task<Batch> BeginAsync(string repo, CancellationToken ct) =>
		new(repo, projects.GetBases(repo), projects.IsBaselined(repo), await RulesAsync(repo, ct));

	/// <summary>
	/// Decides paths the client does not have (deleted, or missing from a full manifest): returns those the client deleted (the mirror still
	/// has the agreed version, or the repo is not baselined yet), pushes those the server created or changed, forgets the base when neither has it.
	/// </summary>
	private async Task<List<string>> DecideAbsentAsync(Batch batch, IEnumerable<string> paths, CancellationToken ct)
	{
		var deletes = new List<string>();
		foreach (var path in paths)
		{
			if (await TryGetHashAsync(batch.Repo, path, ct) is not (true, var mirror))
			{
				continue;
			}

			var baseHash = batch.Bases.GetValueOrDefault(path);
			switch (SyncDecision.Decide(null, mirror, baseHash, batch.Baselined))
			{
				case SyncAction.DeleteMirror:
					deletes.Add(path);
					break;
				case SyncAction.Push:
					AddPush(batch, path, mirror, baseHash);
					break;
				default:
					batch.BaseChanges[path] = null;
					break;
			}
		}

		return deletes;
	}

	/// <returns>The mirror's hash, or not checked when the file cannot be read now (locked by a writer, or behind a link): one path must not
	/// hold back the others, and a locked file changes again when it is closed.</returns>
	private async Task<(bool Checked, string? Hash)> TryGetHashAsync(string repo, string path, CancellationToken ct)
	{
		try
		{
			return (true, await store.GetHashAsync(repo, path, ct));
		}
		catch (Exception ex) when (ex is EnvelopeException or IOException or UnauthorizedAccessException)
		{
			logger.LogDebug(ex, "Sync {Repo}: cannot check {Path}", repo, path);
			return (false, null);
		}
	}

	private void Delete(Batch batch, List<string> paths)
	{
		foreach (var path in paths)
		{
			store.Delete(batch.Repo, path);
			batch.BaseChanges[path] = null;
		}
	}

	/// <summary>Queues a push, unless the path is excluded, invalid, or the mirror file is larger than the sync limit (logged once per path and size).</summary>
	private void AddPush(Batch batch, string path, string? mirror, string? baseHash)
	{
		if (!SyncPath.IsValid(path) || batch.Rules.IsIgnored(path))
		{
			return;
		}

		var size = mirror is null ? 0 : store.GetSize(batch.Repo, path);
		if (size > SyncLimits.MaxFileSize)
		{
			if (_tooLarge.Add((path, size)))
			{
				logger.LogInformation("Not sending {Path} ({Size} bytes) to the browser: larger than the sync limit", path, size);
			}

			return;
		}

		batch.Pushes.Add(new RemoteChange(path, mirror, size, baseHash));
	}

	/// <summary>Writes the batch's base changes in one transaction.</summary>
	private void SaveBases(Batch batch)
	{
		if (batch.BaseChanges.Count > 0)
		{
			projects.SetBases(batch.Repo, [.. batch.BaseChanges]);
			batch.BaseChanges.Clear();
		}
	}

	/// <summary>Saves the bases, then sends the pushes in pages (a push may reach the client before the reply of the message that caused it).</summary>
	private async Task FinishAsync(Batch batch, CancellationToken ct)
	{
		SaveBases(batch);
		foreach (var page in SyncPages.Split(batch.Pushes, c => JsonSerializer.SerializeToUtf8Bytes(c, JsonSerializerOptions.Web).Length + 1))
		{
			if (_disposed)
			{
				return;
			}

			await _context!.SendAsync(Envelope.Create(MessageTypes.SyncRemote, new SyncRemotePayload(batch.Repo, page)), ct);
		}
	}

	/// <summary>One manifest page, delta or mirror change: the repo's state when it started, and what it changes.</summary>
	private sealed class Batch(string repo, IReadOnlyDictionary<string, string> bases, bool baselined, IgnoreRules rules)
	{
		public string Repo => repo;

		public IReadOnlyDictionary<string, string> Bases => bases;

		public bool Baselined => baselined;

		public IgnoreRules Rules => rules;

		/// <summary>New bases (null removes one), written together by <see cref="SaveBases"/>.</summary>
		public Dictionary<string, string?> BaseChanges { get; } = new(StringComparer.OrdinalIgnoreCase);

		public List<RemoteChange> Pushes { get; } = [];
	}
}
