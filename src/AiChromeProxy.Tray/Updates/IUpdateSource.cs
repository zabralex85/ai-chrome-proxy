namespace AiChromeProxy.Tray.Updates;

/// <summary>Release feed + installer seam (Velopack in production, a fake in tests).</summary>
public interface IUpdateSource
{
	/// <returns>The newer version available, or null.</returns>
	Task<string?> CheckAsync(CancellationToken ct);

	/// <summary>Downloads the version found by the last <see cref="CheckAsync"/>.</summary>
	Task DownloadAsync(CancellationToken ct);

	/// <summary>Exits this process, applies the downloaded update and restarts the tray; returns only by throwing.</summary>
	void ApplyAndRestart();
}
