using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// The real <c>cloudflared</c> child process. On Windows it is put in a Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the
/// job handle lives as long as the Server process, so even a crashed or killed Server leaves no orphan <c>cloudflared</c> holding the tunnel.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process and Win32 Job object glue; the decisions are in CloudflaredSupervisor, tested through a fake.")]
public sealed class CloudflaredProcess : ICloudflaredProcess
{
	private readonly Process _process;

	private CloudflaredProcess(Process process) => _process = process;

	/// <summary>Starts <paramref name="info"/> with redirected output (each line to <paramref name="output"/>) and no window.</summary>
	/// <exception cref="Win32Exception">The executable was not found or could not be started, or the job object failed (then nothing is started).</exception>
	public static ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)
	{
		// The job first: if it cannot be created, no process holding TUNNEL_TOKEN is started (and leaked) on every retry.
		KillOnCloseJob.Ensure();
		info.UseShellExecute = false;
		info.CreateNoWindow = true;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => Forward(e.Data, output);
		process.ErrorDataReceived += (_, e) => Forward(e.Data, output);
		process.Start();

		// cloudflared spawns no children, so the window between Start and the assignment below is theoretical; Kill(entireProcessTree) covers shutdown.
		try
		{
			KillOnCloseJob.Assign(process);
		}
		catch (Win32Exception)
		{
			using (var started = new CloudflaredProcess(process))
			{
				started.Kill();
			}

			throw;
		}

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();
		return new CloudflaredProcess(process);
	}

	public async Task<int> WaitForExitAsync(CancellationToken ct)
	{
		await _process.WaitForExitAsync(ct);
		return _process.ExitCode;
	}

	public void Kill()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
		catch (Exception ex) when (ex is Win32Exception or AggregateException)
		{
			// Kill(entireProcessTree) reports a failed terminate as AggregateException.
			// Access denied or already terminating: the kill-on-close job still ends it with the Server.
		}
	}

	public void Dispose() => _process.Dispose();

	private static void Forward(string? line, Action<string> output)
	{
		if (!string.IsNullOrEmpty(line))
		{
			output(line);
		}
	}
}
