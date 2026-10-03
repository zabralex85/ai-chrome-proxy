using System.Diagnostics.CodeAnalysis;
using Velopack;
using Velopack.Sources;

namespace AiChromeProxy.Tray.Updates;

/// <summary>
/// GitHub Releases through Velopack. No updates when the tray was not installed by Velopack (dotnet run, tests)
/// or the build has no repository (<c>-p:UpdateRepository=</c>, set by the release workflow).
/// Excluded from coverage: talks to GitHub and runs Velopack's Update.exe; the flow around it is in <see cref="UpdateOrchestrator"/>.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class VelopackUpdateSource(string? repositoryUrl) : IUpdateSource
{
	private UpdateManager? _manager;
	private UpdateInfo? _update;

	public async Task<string?> CheckAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(repositoryUrl))
		{
			return null;
		}

		_manager ??= new UpdateManager(new GithubSource(repositoryUrl, accessToken: null, prerelease: false));
		if (!_manager.IsInstalled)
		{
			return null;
		}

		_update = await _manager.CheckForUpdatesAsync();
		return _update?.TargetFullRelease.Version.ToString();
	}

	public Task DownloadAsync(CancellationToken ct) => Manager().DownloadUpdatesAsync(Pending(), null, ct);

	public void ApplyAndRestart() => Manager().ApplyUpdatesAndRestart(Pending().TargetFullRelease);

	private UpdateManager Manager() => _manager ?? throw new InvalidOperationException("Check for updates first.");

	private UpdateInfo Pending() => _update ?? throw new InvalidOperationException("No update available.");
}
