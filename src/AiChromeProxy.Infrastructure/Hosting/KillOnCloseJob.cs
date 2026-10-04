#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute: DllImport keeps the assembly free of unsafe code.

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>
/// One Windows Job object with <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> per host process, never closed explicitly: Windows closes it when
/// the host exits (even when crashed or killed), which kills every child put in it (<c>cloudflared</c>, the agent). No-op off Windows.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Win32 Job object glue.")]
public sealed class KillOnCloseJob : IDisposable
{
	private const int JobObjectExtendedLimitInformationClass = 9;
	private const uint JobObjectLimitKillOnJobClose = 0x2000;

	private static readonly Lazy<KillOnCloseJob> HostJob = new(Create);

	private readonly SafeFileHandle? _handle;

	private KillOnCloseJob(SafeFileHandle? handle) => _handle = handle;

	/// <summary>Creates the host-wide job now (call before starting a process, so a failure leaks nothing).</summary>
	/// <exception cref="Win32Exception">The job object could not be created or configured.</exception>
	public static void Ensure() => _ = HostJob.Value;

	/// <summary>Puts <paramref name="process"/> in the host-wide job.</summary>
	/// <exception cref="Win32Exception">The job object failed; the caller should kill the process.</exception>
	public static void Assign(Process process) => HostJob.Value.Add(process);

	/// <summary>A new job of its own: closing it (<see cref="Dispose"/>) kills every process in it, grandchildren included.</summary>
	/// <exception cref="Win32Exception">The job object could not be created or configured.</exception>
	public static KillOnCloseJob Create()
	{
		if (!OperatingSystem.IsWindows())
		{
			return new KillOnCloseJob(null);
		}

		var job = CreateJobObjectW(IntPtr.Zero, null);
		if (job.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object.");
		}

		var limits = new JobObjectExtendedLimitInformation { BasicLimitInformation = new JobObjectBasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
		if (!SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, ref limits, (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
		{
			var error = Marshal.GetLastPInvokeError();
			job.Dispose();
			throw new Win32Exception(error, "Could not configure the job object.");
		}

		return new KillOnCloseJob(job);
	}

	/// <summary>Puts <paramref name="process"/> in this job (nested in the host job when it is already in one).</summary>
	/// <exception cref="Win32Exception">The job object failed; the caller should kill the process.</exception>
	public void Add(Process process)
	{
		if (_handle is not null && !AssignProcessToJobObject(_handle, process.SafeHandle))
		{
			throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not put the process in a job object.");
		}
	}

	/// <summary>Closes the job, which kills its processes; safe to call more than once.</summary>
	public void Dispose() => _handle?.Dispose();

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
