using AiChromeProxy.Client.Navigator;
using AiChromeProxy.Client.Projects;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Client;

/// <summary>The view of another computer's project against <see cref="LoopbackServer"/> (the real <c>MirrorBrowseHandler</c>).</summary>
public sealed class MirrorBrowserTests : IDisposable
{
	private const string Other = "Other_PC";

	private readonly LoopbackServer _server = new();
	private readonly FakeTimeProvider _time = new();
	private readonly SyncEngine _sync;
	private readonly MirrorBrowser _mirror;

	public MirrorBrowserTests()
	{
		_sync = new SyncEngine(_server.Transport, new FakeFolder(), TimeProvider.System);
		_mirror = new MirrorBrowser(_server.Transport, _sync, _time);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	public void Dispose() => _server.Dispose();

	[Fact]
	public async Task ListRepos_TheServersFolders()
	{
		Write(Other, "a.txt", "a");

		var repos = await _mirror.ListReposAsync(Ct);

		Assert.Equal([Other], repos.Repos.Select(r => r.Name));
	}

	[Fact]
	public async Task View_LoadsEveryPage_Leave_Forgets()
	{
		for (var i = 0; i < SyncLimits.MaxPageEntries + 20; i++)
		{
			Write(Other, $"f{i:D4}.txt", "x");
		}

		var changes = 0;
		_mirror.Changed += () => changes++;
		await _mirror.ViewAsync(Other, Ct);

		Assert.Equal(Other, _mirror.Repo);
		Assert.Equal(SyncLimits.MaxPageEntries + 20, _mirror.Files.Count);
		Assert.Equal(1, _mirror.FileAt("f0000.txt")?.Size);
		Assert.True(changes > 0);
		Assert.Equal(2, _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorList));
		var generation = _mirror.Generation;

		_mirror.Leave();

		Assert.Null(_mirror.Repo);
		Assert.Empty(_mirror.Files);
		Assert.NotEqual(generation, _mirror.Generation);
	}

	[Fact]
	public async Task View_MissingRepo_Problem()
	{
		await _mirror.ViewAsync("gone", Ct);

		Assert.Equal(MirrorBrowser.Gone, _mirror.Problem);
		Assert.Empty(_mirror.Files);
	}

	[Fact]
	public async Task ReadFile_AssemblesChunksInOrder_WithReadsInFlight()
	{
		var content = string.Concat(Enumerable.Range(0, 5 * SyncLimits.ChunkSize / 10).Select(i => $"{i % 10_000:D9}\n"));
		Write(Other, "big.txt", content);
		await _mirror.ViewAsync(Other, Ct);

		// Delayed replies, so that requests in flight are counted (FakeTransport.MaxPendingReplies).
		_server.Transport.ReplyDelay = TimeSpan.FromMilliseconds(5);
		var bytes = await _mirror.ReadFileAsync("big.txt", Ct);

		Assert.Equal(content, System.Text.Encoding.UTF8.GetString(bytes!.Bytes));
		Assert.Equal(content.Length, bytes.Size);
		Assert.InRange(_server.Transport.MaxPendingReplies, 2, MirrorBrowser.ReadsInFlight);
	}

	[Fact]
	public async Task ReadFile_Missing_Null_TooLarge_NotRead_Excluded_Throws()
	{
		Write(Other, ".gitignore", "*.log\n");
		Write(Other, "debug.log", "x");
		File.WriteAllBytes(_server.PathOf(Other, "huge.bin"), new byte[FileText.MaxBytes + 1]);
		await _mirror.ViewAsync(Other, Ct);

		Assert.Null(await _mirror.ReadFileAsync("missing.txt", Ct));
		var reads = _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorRead);
		var huge = await _mirror.ReadFileAsync("huge.bin", Ct);
		Assert.Equal((0, (long)FileText.MaxBytes + 1), (huge!.Bytes.Length, huge.Size));
		Assert.Equal(reads, _server.Transport.Sent.Count(e => e.Type == MessageTypes.MirrorRead));
		var excluded = await Assert.ThrowsAsync<InvalidOperationException>(() => _mirror.ReadFileAsync("debug.log", Ct));
		Assert.Contains("excluded", excluded.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task RunAsync_RefreshesEveryInterval_WhileViewedAndVisible()
	{
		Write(Other, "a.txt", "a");
		await _mirror.ViewAsync(Other, Ct);
		using (var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct))
		{
			var loop = _mirror.RunAsync(stop.Token);
			Write(Other, "b.txt", "b");

			_sync.SetVisible(false);
			_time.Advance(MirrorBrowser.RefreshInterval);
			await Task.Delay(100, Ct);
			Assert.Null(_mirror.FileAt("b.txt"));

			_sync.SetVisible(true);
			_time.Advance(MirrorBrowser.RefreshInterval);
			await UntilAsync(() => _mirror.FileAt("b.txt") is not null);

			stop.Cancel();
			await loop;
		}
	}

	private static async Task UntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the mirror browser.");
			await Task.Delay(10, Ct);
		}
	}

	private void Write(string repo, string path, string text)
	{
		var full = _server.PathOf(repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}
}
