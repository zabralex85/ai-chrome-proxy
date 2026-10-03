using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// Thin P/Invoke layer (advapi32: SCM, LSA, LogonUser) for the elevated <c>--admin install|uninstall</c> instance.
/// Excluded from coverage: every call needs elevation and changes the machine; the decisions it applies live in
/// <see cref="ServiceSetup"/> (tested) and the calls themselves are covered by the manual acceptance checklist.
/// </summary>
[ExcludeFromCodeCoverage]
internal static partial class ServiceInstaller
{
	private const string SeServiceLogonRight = "SeServiceLogonRight";

	private const uint ScManagerConnect = 0x0001;
	private const uint ScManagerCreateService = 0x0002;
	private const uint ServiceChangeConfig = 0x0002;
	private const uint ServiceQueryStatus = 0x0004;
	private const uint ServiceStart = 0x0010;
	private const uint ServiceStop = 0x0020;
	private const uint Delete = 0x0001_0000;
	private const uint ReadControl = 0x0002_0000;
	private const uint WriteDac = 0x0004_0000;
	private const uint ServiceWin32OwnProcess = 0x0010;
	private const uint ServiceAutoStart = 0x0002;
	private const uint ServiceErrorNormal = 0x0001;
	private const uint ServiceNoChange = 0xFFFF_FFFF;
	private const uint ServiceConfigFailureActions = 2;
	private const uint ScActionRestart = 1;
	private const uint DaclSecurityInformation = 0x0004;
	private const uint Logon32LogonService = 5;
	private const uint Logon32ProviderDefault = 0;
	private const uint PolicyCreateAccount = 0x0010;
	private const uint PolicyLookupNames = 0x0800;
	private const int ErrorInsufficientBuffer = 122;
	private const int ErrorServiceMarkedForDelete = 1072;
	private const int ErrorServiceExists = 1073;
	private const int ErrorServiceDoesNotExist = 1060;
	private const int ErrorLogonFailure = 1326;

	private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

	/// <summary>Creates (or reconfigures) the service to run as <paramref name="account"/>, then starts it.</summary>
	public static void Install(string serviceName, string account, string password, string controlUser, DataDirectory dataDir)
	{
		var accountSid = ServiceSetup.Sid(account);
		var controlSid = ServiceSetup.Sid(controlUser);
		ServiceSetup.EnsureServiceAccountIsControlUser(accountSid, controlSid);
		ServiceSetup.EnsureDataDirectorySafe(dataDir, accountSid, controlSid);

		// "Log on as a service" first: LogonUser(LOGON32_LOGON_SERVICE) below then fails only for bad credentials.
		GrantLogonAsService(accountSid);
		VerifyPassword(account, password);

		using var manager = OpenSCManager(null, null, ScManagerConnect | ScManagerCreateService);
		ThrowIfInvalid(manager);
		var binaryPath = ServiceSetup.BinaryPathName(ServiceSetup.ServerExecutable(AppContext.BaseDirectory));
		using var service = CreateOrReconfigure(manager, serviceName, binaryPath, ServiceSetup.ServiceStartName(account), password);
		SetFailureActions(service);
		GrantUserControl(service, controlSid);
		ServiceSetup.PrepareDataDirectory(dataDir, accountSid, controlSid);

		using var controller = new ServiceController(serviceName);
		if (controller.Status == ServiceControllerStatus.Stopped)
		{
			controller.Start();
		}
	}

	/// <summary>Marks the service for deletion, then stops it (deletion completes once it has stopped); the data directory is kept.</summary>
	public static void Uninstall(string serviceName)
	{
		using var manager = OpenSCManager(null, null, ScManagerConnect);
		ThrowIfInvalid(manager);
		using var service = OpenService(manager, serviceName, ServiceStop | ServiceQueryStatus | Delete);
		if (service.IsInvalid && Marshal.GetLastPInvokeError() == ErrorServiceDoesNotExist)
		{
			return;
		}

		ThrowIfInvalid(service);
		using var controller = new ServiceController(serviceName);
		ServiceSetup.RunUninstallSequence(
			() =>
			{
				if (!DeleteService(service) && Marshal.GetLastPInvokeError() != ErrorServiceMarkedForDelete)
				{
					throw new Win32Exception();
				}
			},
			() =>
			{
				if (controller.Status is not (ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending))
				{
					controller.Stop(stopDependentServices: false);
				}
			},
			() => controller.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout));
	}

	private static void VerifyPassword(string account, string password)
	{
		var (domain, user) = ServiceSetup.SplitAccount(account);
		if (!LogonUser(user, domain, password, Logon32LogonService, Logon32ProviderDefault, out var token))
		{
			var error = Marshal.GetLastPInvokeError();
			throw error == ErrorLogonFailure
				? new Win32Exception(error, $"Wrong password for {account}. Use the Windows account password (a PIN does not work for services).")
				: new Win32Exception(error);
		}

		CloseHandle(token);
	}

	private static void GrantLogonAsService(SecurityIdentifier sid)
	{
		var attributes = default(LsaObjectAttributes);
		CheckNtStatus(LsaOpenPolicy(IntPtr.Zero, ref attributes, PolicyCreateAccount | PolicyLookupNames, out var policy));
		var right = Marshal.StringToHGlobalUni(SeServiceLogonRight);
		try
		{
			var sidBytes = new byte[sid.BinaryLength];
			sid.GetBinaryForm(sidBytes, 0);
			LsaUnicodeString[] rights =
			[
				new()
				{
					Length = (ushort)(SeServiceLogonRight.Length * sizeof(char)),
					MaximumLength = (ushort)((SeServiceLogonRight.Length + 1) * sizeof(char)),
					Buffer = right,
				},
			];
			CheckNtStatus(LsaAddAccountRights(policy, sidBytes, rights, 1));
		}
		finally
		{
			Marshal.FreeHGlobal(right);
			_ = LsaClose(policy);
		}
	}

	private static ServiceHandle CreateOrReconfigure(ServiceHandle manager, string serviceName, string binaryPath, string account, string password)
	{
		const uint access = ServiceChangeConfig | ServiceStart | ReadControl | WriteDac;
		var service = CreateService(
			manager, serviceName, ServiceSetup.DisplayName, access, ServiceWin32OwnProcess, ServiceAutoStart, ServiceErrorNormal,
			binaryPath, null, IntPtr.Zero, null, account, password);
		if (!service.IsInvalid)
		{
			return service;
		}

		var error = Marshal.GetLastPInvokeError();
		service.Dispose();
		if (error != ErrorServiceExists)
		{
			throw new Win32Exception(error);
		}

		// Re-running "Install service…" updates binary path, account and password (e.g. after a Windows password change).
		service = OpenService(manager, serviceName, access);
		ThrowIfInvalid(service);
		if (!ChangeServiceConfig(
			service, ServiceNoChange, ServiceAutoStart, ServiceNoChange, binaryPath, null, IntPtr.Zero, null, account, password, ServiceSetup.DisplayName))
		{
			throw new Win32Exception();
		}

		return service;
	}

	private static void SetFailureActions(ServiceHandle service)
	{
		var actions = Enumerable.Repeat(
			new ScAction { Type = ScActionRestart, Delay = (uint)ServiceSetup.RestartDelay.TotalMilliseconds },
			ServiceSetup.RestartAttempts).ToArray();
		var pinned = GCHandle.Alloc(actions, GCHandleType.Pinned);
		try
		{
			var info = new ServiceFailureActions
			{
				ResetPeriod = (uint)ServiceSetup.FailureResetPeriod.TotalSeconds,
				ActionCount = (uint)actions.Length,
				Actions = pinned.AddrOfPinnedObject(),
			};
			if (!ChangeServiceConfig2(service, ServiceConfigFailureActions, ref info))
			{
				throw new Win32Exception();
			}
		}
		finally
		{
			pinned.Free();
		}
	}

	private static void GrantUserControl(ServiceHandle service, SecurityIdentifier user)
	{
		if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, null, 0, out var needed) && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
		{
			throw new Win32Exception();
		}

		var descriptor = new byte[needed];
		if (!QueryServiceObjectSecurity(service, DaclSecurityInformation, descriptor, needed, out _)
			|| !SetServiceObjectSecurity(service, DaclSecurityInformation, ServiceSetup.GrantUserControl(descriptor, user)))
		{
			throw new Win32Exception();
		}
	}

	private static void ThrowIfInvalid(SafeHandle handle)
	{
		if (handle.IsInvalid)
		{
			throw new Win32Exception();
		}
	}

	private static void CheckNtStatus(uint status)
	{
		if (status != 0)
		{
			throw new Win32Exception(LsaNtStatusToWinError(status));
		}
	}

	[LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

	[LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle OpenService(ServiceHandle manager, string serviceName, uint desiredAccess);

	[LibraryImport("advapi32.dll", EntryPoint = "CreateServiceW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial ServiceHandle CreateService(
		ServiceHandle manager,
		string serviceName,
		string displayName,
		uint desiredAccess,
		uint serviceType,
		uint startType,
		uint errorControl,
		string binaryPathName,
		string? loadOrderGroup,
		IntPtr tagId,
		string? dependencies,
		string? serviceStartName,
		string? password);

	[LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfigW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ChangeServiceConfig(
		ServiceHandle service,
		uint serviceType,
		uint startType,
		uint errorControl,
		string? binaryPathName,
		string? loadOrderGroup,
		IntPtr tagId,
		string? dependencies,
		string? serviceStartName,
		string? password,
		string? displayName);

	[LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool ChangeServiceConfig2(ServiceHandle service, uint infoLevel, ref ServiceFailureActions info);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool QueryServiceObjectSecurity(ServiceHandle service, uint securityInformation, byte[]? securityDescriptor, uint bufferSize, out uint bytesNeeded);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetServiceObjectSecurity(ServiceHandle service, uint securityInformation, byte[] securityDescriptor);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool DeleteService(ServiceHandle service);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseServiceHandle(IntPtr handle);

	[LibraryImport("advapi32.dll", EntryPoint = "LogonUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool LogonUser(string userName, string domain, string password, uint logonType, uint logonProvider, out IntPtr token);

	[LibraryImport("kernel32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool CloseHandle(IntPtr handle);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaOpenPolicy(IntPtr systemName, ref LsaObjectAttributes objectAttributes, uint desiredAccess, out IntPtr policyHandle);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaAddAccountRights(IntPtr policyHandle, byte[] accountSid, LsaUnicodeString[] userRights, uint countOfRights);

	[LibraryImport("advapi32.dll")]
	private static partial uint LsaClose(IntPtr policyHandle);

	[LibraryImport("advapi32.dll")]
	private static partial int LsaNtStatusToWinError(uint status);

	[StructLayout(LayoutKind.Sequential)]
	private struct LsaObjectAttributes
	{
		public int Length;
		public IntPtr RootDirectory;
		public IntPtr ObjectName;
		public uint Attributes;
		public IntPtr SecurityDescriptor;
		public IntPtr SecurityQualityOfService;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct LsaUnicodeString
	{
		public ushort Length;
		public ushort MaximumLength;
		public IntPtr Buffer;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ServiceFailureActions
	{
		public uint ResetPeriod;
		public IntPtr RebootMessage;
		public IntPtr Command;
		public uint ActionCount;
		public IntPtr Actions;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct ScAction
	{
		public uint Type;
		public uint Delay;
	}

	/// <summary>SC_HANDLE closed with CloseServiceHandle.</summary>
	private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
	{
		public ServiceHandle()
			: base(ownsHandle: true)
		{
		}

		protected override bool ReleaseHandle() => CloseServiceHandle(handle);
	}
}
