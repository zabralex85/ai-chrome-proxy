namespace AiChromeProxy.Server.Hosting;

/// <summary>A started <c>cloudflared</c>: the seam that keeps <see cref="CloudflaredSupervisor"/> testable without a real process.</summary>
public interface ICloudflaredProcess : IDisposable
{
	/// <returns>The exit code, once the process has exited.</returns>
	Task<int> WaitForExitAsync(CancellationToken ct);

	/// <summary>Kills the process and its children; no-op once it has exited.</summary>
	void Kill();
}
