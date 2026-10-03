#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute: DllImport keeps the Server free of unsafe code.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Server.Hosting;

/// <summary>
/// The real <c>cloudflared</c> child process. On Windows it is put in a Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c>: the
/// job handle lives as long as the Server process, so even a crashed or killed Server leaves no orphan <c>cloudflared</c> holding the tunnel.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process and Win32 Job object glue; the decisions are in CloudflaredSupervisor, tested through a fake.")]
public sealed class CloudflaredProcess : ICloudflaredProcess
{
	private const int JobObjectExtendedLimitInformationClass = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	/// <summary>One job per Server process, never closed explicitly: Windows closes it when the Server exits, which kills the children.</summary>
	private static readonly Lazy<SafeFileHandle?> Job = new(CreateKillOnCloseJob);

	private readonly Process _process;

	private CloudflaredProcess(Process process) => _process = process;

	/// <summary>Starts <paramref name="info"/> with redirected output (each line to <paramref name="output"/>) and no window.</summary>
	/// <exception cref="Win32Exception">The executable was not found or could not be started, or the job object failed (then nothing is started).</exception>
	public static ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)
	{
		// The job first: if it cannot be created, no process holding TUNNEL_TOKEN is started (and leaked) on every retry.
		var job = OperatingSystem.IsWindows() ? Job.Value : null;
		info.UseShellExecute = false;
		info.CreateNoWindow = true;
		info.RedirectStandardOutput = true;
		info.RedirectStandardError = true;
		var process = new Process { StartInfo = info, EnableRaisingEvents = true };
		process.OutputDataReceived += (_, e) => Forward(e.Data, output);
		process.ErrorDataReceived += (_, e) => Forward(e.Data, output);
		process.Start();

		// cloudflared spawns no children, so the window between Start and the assignment below is theoretical; Kill(entireProcessTree) covers shutdown.
		if (job is not null && !AssignProcessToJobObject(job, process.SafeHandle))
		{
			var error = Marshal.GetLastPInvokeError();
			using (var started = new CloudflaredProcess(process))
			{
				started.Kill();
			}

			throw new Win32Exception(error, "Could not put cloudflared in the Server's job object.");
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

	private static SafeFileHandle? CreateKillOnCloseJob()
	{
		if (!OperatingSystem.IsWindows())
		{
			return null;
		}

		var job = CreateJobObjectW(IntPtr.Zero, null);
		if (job.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object for cloudflared.");
		}

		var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
		if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
		{
			var error = Marshal.GetLastPInvokeError();
			job.Dispose();
			throw new Win32Exception(error, "Could not configure the job object for cloudflared.");
		}

		return job;
	}

	[DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobObjectExtendedLimitInformation info, uint length);

	[DllImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectBasicLimitInformation
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public UIntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IoCounters
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JobObjectExtendedLimitInformation
	{
		public JobObjectBasicLimitInformation BasicLimitInformation;
		public IoCounters IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}
}
