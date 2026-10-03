using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// Runs <c>cloudflared tunnel --no-autoupdate run</c> as a child of the Server while <c>Tunnel:Token</c> is set, restarting it with
/// exponential backoff. The token reaches the child only through its <c>TUNNEL_TOKEN</c> environment variable: never a command line or a log.
/// Never while the Cloudflare Access check is off (Development): a Server without it must not be reachable from the internet.
/// </summary>
/// <param name="start">Starts the process and sends each stdout/stderr line to the callback (<see cref="CloudflaredProcess.Start"/>; a fake in tests).</param>
public sealed class CloudflaredSupervisor(
	TunnelOptions options,
	CloudflareAccessOptions access,
	ILogger<CloudflaredSupervisor> logger,
	TimeProvider time,
	Func<ProcessStartInfo, Action<string>, ICloudflaredProcess> start) : BackgroundService
{
	public const string TokenVariable = "TUNNEL_TOKEN";
	public const string BundledFileName = "cloudflared.exe";

	public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(1);
	public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);

	/// <summary>A run at least this long counts as healthy: the next restart waits <see cref="FirstBackoff"/> again.</summary>
	public static readonly TimeSpan StableRun = TimeSpan.FromMinutes(5);

	/// <summary><c>Tunnel:CloudflaredPath</c>, else the bundled <c>cloudflared.exe</c> next to the Server, else <c>cloudflared</c> from PATH.</summary>
	public static string ResolveExecutable(string? configuredPath, string baseDirectory)
	{
		if (!string.IsNullOrWhiteSpace(configuredPath))
		{
			return configuredPath;
		}

		var bundled = Path.Combine(baseDirectory, BundledFileName);
		return File.Exists(bundled) ? bundled : "cloudflared";
	}

	/// <summary>The supervision loop; returns when <paramref name="ct"/> is cancelled (the running process is killed), no token is configured or Access is off.</summary>
	public async Task RunAsync(CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(options.Token))
		{
			logger.LogInformation("Cloudflare Tunnel not configured (Tunnel:Token is empty); cloudflared is not started.");
			return;
		}

		if (!access.Enabled)
		{
			logger.LogWarning("Cloudflare Access check is disabled; cloudflared is not started (never expose a Server without it).");
			return;
		}

		var backoff = FirstBackoff;
		while (true)
		{
			var started = time.GetTimestamp();
			var info = StartInfo();
			ICloudflaredProcess? process = null;
			try
			{
				process = start(info, line => logger.LogInformation("cloudflared: {Line}", line));
				logger.LogInformation("cloudflared started ({Path}).", info.FileName);
			}
			catch (Exception ex)
			{
				logger.LogError(ex, "cloudflared could not be started ({Path}).", info.FileName);
			}

			if (process is not null)
			{
				using (process)
				{
					try
					{
						var exitCode = await process.WaitForExitAsync(ct).ConfigureAwait(false);
						logger.LogWarning("cloudflared exited with code {ExitCode}.", exitCode);
					}
					catch (OperationCanceledException) when (ct.IsCancellationRequested)
					{
						process.Kill();
						return;
					}
					catch (Exception ex)
					{
						// Lost track of the child: stop it so the restart cannot run a second tunnel next to it.
						logger.LogError(ex, "Waiting for cloudflared failed; it is stopped and restarted.");
						process.Kill();
					}
				}
			}

			if (time.GetElapsedTime(started) >= StableRun)
			{
				backoff = FirstBackoff;
			}

			logger.LogInformation("Restarting cloudflared in {Delay}.", backoff);
			try
			{
				await Task.Delay(backoff, time, ct).ConfigureAwait(false);
			}
			catch (OperationCanceledException)
			{
				return;
			}

			backoff = backoff * 2 < MaxBackoff ? backoff * 2 : MaxBackoff;
		}
	}

	protected override Task ExecuteAsync(CancellationToken stoppingToken) => RunAsync(stoppingToken);

	private ProcessStartInfo StartInfo()
	{
		var info = new ProcessStartInfo(ResolveExecutable(options.CloudflaredPath, AppContext.BaseDirectory)) { ArgumentList = { "tunnel", "--no-autoupdate", "run" } };
		info.Environment[TokenVariable] = options.Token;
		return info;
	}
}
