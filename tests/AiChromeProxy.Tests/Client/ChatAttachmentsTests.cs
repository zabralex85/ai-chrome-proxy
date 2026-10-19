using System.Text.Json;
using AiChromeProxy.Client.Chat;
using AiChromeProxy.Client.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;

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
	public async Task Remove_AfterAChunkWasSent_ClosesTheUploadSoTheServerFreesTheSlot()
	{
		_source.Files["h1"] = new byte[40_000];
		_source.Gate = new TaskCompletionSource();
		_source.GateFrom = ChatLimits.AttachmentChunkSize;
		var adding = _attachments.AddAsync("repo", [File("h1", "a.bin", 40_000)]);
		while (!_server.Transport.Sent.Any(e => e.Type == MessageTypes.ChatAttach))
		{
			await Task.Delay(5, TestContext.Current.CancellationToken);
		}

		await _attachments.RemoveAsync("h1");
		_source.Gate.SetResult();
		await adding;

		var attach = _server.Transport.Sent.Where(e => e.Type == MessageTypes.ChatAttach).Select(e => e.Payload.Deserialize<ChatAttachPayload>(JsonSerializerOptions.Web)!).ToList();
		Assert.Equal(2, attach.Count);
		Assert.Equal(attach[0].Id, attach[1].Id);
		Assert.True(attach[1].Last);
		Assert.Equal(string.Empty, attach[1].Data);
		Assert.Equal(ChatLimits.AttachmentChunkSize, attach[1].Offset);
		Assert.False(Directory.Exists(Path.Combine(_server.AttachmentsRoot, "repo", attach[0].Id)));
	}

	[Fact]
	public async Task AMidUploadRefusal_ShowsTheServersText_OnTheChip()
	{
		_source.Files["h1"] = new byte[40_000];
		foreach (var i in Enumerable.Range(0, global::AiChromeProxy.Application.Chat.ChatAttachments.MaxUploadsPerRepo))
		{
			_server.Attachments.Append(new ChatAttachPayload("repo", $"{i:x32}", "x.bin", 10, 0, SyncData.Encode([1]), false));
		}

		await _attachments.AddAsync("repo", [File("h1", "a.bin", 40_000)]);

		var item = Assert.Single(_attachments.Items);
		Assert.Equal(AttachmentState.Failed, item.State);
		Assert.Equal("Too many uploads in progress.", item.Error);
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
	public async Task RemoveByHandles_KeepsTheOthers()
	{
		_source.Files["h1"] = [1];
		_source.Files["h2"] = [2];
		await _attachments.AddAsync("repo", [File("h1", "a.txt", 1), File("h2", "b.txt", 1)]);

		await _attachments.RemoveAsync(["h1"]);

		Assert.Equal("h2", Assert.Single(_attachments.Items).File.Handle);
		Assert.Equal(["h1"], _source.Forgotten);
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

		/// <summary>Reads at or after this offset wait for the gate.</summary>
		public long GateFrom { get; set; }

		public async Task<byte[]> ReadChunkAsync(string handle, long offset, int length)
		{
			if (Gate is not null && offset >= GateFrom)
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
