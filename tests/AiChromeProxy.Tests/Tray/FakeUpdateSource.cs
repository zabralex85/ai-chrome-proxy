using AiChromeProxy.Tray.Updates;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Records into the same call list as <see cref="FakeServiceControl"/>, so tests can assert the cross-object order.</summary>
public sealed class FakeUpdateSource(List<string> calls) : IUpdateSource
{
	public string? Available { get; set; } = "1.2.3";

	public Exception? FailCheck { get; set; }

	public Exception? FailDownload { get; set; }

	public Exception? FailApply { get; set; }

	public int Checks { get; private set; }

	public Task<string?> CheckAsync(CancellationToken ct)
	{
		Checks++;
		return FailCheck is null ? Task.FromResult(Available) : Task.FromException<string?>(FailCheck);
	}

	public Task DownloadAsync(CancellationToken ct)
	{
		calls.Add("download");
		return FailDownload is null ? Task.CompletedTask : Task.FromException(FailDownload);
	}

	public void ApplyAndRestart()
	{
		calls.Add("apply");
		if (FailApply is not null)
		{
			throw FailApply;
		}
	}
}
