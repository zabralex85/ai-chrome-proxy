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
