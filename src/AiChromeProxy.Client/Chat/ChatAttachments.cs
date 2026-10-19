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
	/// <param name="repo">The repo the message goes to, or null without an open folder.</param>
	/// <param name="files">The files the browser reported.</param>
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
	/// <param name="handle">The browser's key of the file.</param>
	/// <returns>A task that completes when the browser was told.</returns>
	public async Task RemoveAsync(string handle)
	{
		CancellationTokenSource? cancel;
		lock (_gate)
		{
			_items.RemoveAll(i => i.File.Handle == handle);
			_cancels.Remove(handle, out cancel);
			_note = null;
		}

		cancel?.Cancel();
		await source.ForgetAsync(handle);
		Changed?.Invoke();
	}

	/// <summary>Empties the box after the message went out.</summary>
	/// <returns>A task that completes when every file is forgotten.</returns>
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
		var id = Guid.NewGuid().ToString("N");
		long sent = 0; // bytes the server may hold in its .part
		var finished = false; // the last chunk went out: the server is done with the upload either way
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
					finished = true;
					await transport.RequestAsync(envelope, ChatEngine.RequestTimeout, ct);
					Update(file.Handle, p => p with { State = AttachmentState.Ready, Sent = file.Size, Id = id }, raise: true);
					return;
				}

				// Not awaited for a reply: the server answers only the last chunk (or the upload's first error, which the last chunk then gets).
				await transport.SendAsync(envelope, ct);
				sent = offset + length;
				Update(file.Handle, p => p with { Sent = sent }, raise: chunk % ProgressEvery == 0);
			}
		}
		catch (OperationCanceledException) when (!Items.Any(i => i.File.Handle == file.Handle))
		{
			// Removed while uploading.
			await CloseAsync(repo, id, file, sent, finished);
		}
		catch (Exception ex)
		{
			Update(file.Handle, p => p with { State = AttachmentState.Failed, Error = Describe(ex) }, raise: true);
			await CloseAsync(repo, id, file, sent, finished);
		}
		finally
		{
			_uploading.Release();
		}
	}

	/// <summary>Best effort: a closing chunk that is short of the declared size makes the server delete the .part and free the upload slot.</summary>
	private async Task CloseAsync(string repo, string id, AttachmentFile file, long sent, bool finished)
	{
		if (sent == 0 || finished)
		{
			return;
		}

		try
		{
			await transport.SendAsync(Envelope.Create(MessageTypes.ChatAttach, new ChatAttachPayload(repo, id, file.Name, file.Size, sent, string.Empty, true)), CancellationToken.None);
		}
		catch (Exception)
		{
			// The daily sweep frees it.
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
