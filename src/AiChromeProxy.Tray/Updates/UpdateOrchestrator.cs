using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tray.Updates;

/// <summary>
/// Check at start and every 24 h; update = download, stop the service (its exe lives in the folder being replaced), apply and restart the tray.
/// A failed download leaves everything untouched; a failed apply starts the service again.
/// </summary>
/// <param name="pendingMarker">File recording "service stopped for an update", so the next tray start resumes it if Update.exe failed out of process.</param>
public sealed class UpdateOrchestrator(IUpdateSource source, IServiceControl service, string pendingMarker)
{
	public static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);

	public string? AvailableVersion { get; private set; }

	public async Task<string?> CheckAsync(CancellationToken ct) => AvailableVersion = await source.CheckAsync(ct);

	/// <summary>Checks now and then every <see cref="CheckInterval"/> until cancelled; results and errors go to <paramref name="report"/>, never thrown.</summary>
	public async Task RunChecksAsync(TimeProvider time, Action<string?, Exception?> report, CancellationToken ct)
	{
		using var timer = new PeriodicTimer(CheckInterval, time);
		do
		{
			try
			{
				report(await CheckAsync(ct), null);
			}
			catch (Exception ex) when (ex is not OperationCanceledException)
			{
				report(null, ex);
			}
		}
		while (await timer.WaitForNextTickAsync(ct));
	}

	public async Task UpdateAsync(CancellationToken ct)
	{
		await source.DownloadAsync(ct);

		var wasRunning = service.GetState() is ServiceState.Running or ServiceState.Starting;
		if (wasRunning)
		{
			await File.WriteAllTextAsync(pendingMarker, string.Empty, ct);
			await service.StopAsync(ct);
		}

		try
		{
			source.ApplyAndRestart();
		}
		catch
		{
			if (wasRunning)
			{
				await service.StartAsync(CancellationToken.None);
				File.Delete(pendingMarker);
			}

			throw;
		}
	}

	/// <summary>At tray start: the service was stopped for an update that never came back (Update.exe failed or was killed), so start it again.</summary>
	public async Task ResumeServiceAfterUpdateAsync(CancellationToken ct)
	{
		if (!File.Exists(pendingMarker))
		{
			return;
		}

		File.Delete(pendingMarker);
		if (service.GetState() == ServiceState.Stopped)
		{
			await service.StartAsync(ct);
		}
	}
}
