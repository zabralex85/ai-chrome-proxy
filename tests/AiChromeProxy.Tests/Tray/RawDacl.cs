#pragma warning disable SYSLIB1054 // Use LibraryImportAttribute instead of DllImportAttribute (file must not require unsafe code)

using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Reads the stored DACL of a file or folder exactly as NTFS holds it. The managed <c>GetAccessControl</c> re-evaluates inheritance against the parent and so hides what is really stored.</summary>
internal static class RawDacl
{
	private const uint ReadControl = 0x20000;
	private const uint ShareAll = 0x7;
	private const uint OpenExisting = 3;
	private const uint BackupSemanticsAndOpenReparsePoint = 0x02000000 | 0x00200000;
	private const uint DaclSecurityInformation = 0x4;

	public static string Sddl(string path)
	{
		using var handle = CreateFileW(path, ReadControl, ShareAll, IntPtr.Zero, OpenExisting, BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);
		Assert.False(handle.IsInvalid);
		GetKernelObjectSecurity(handle, DaclSecurityInformation, null, 0, out var needed);
		var buffer = new byte[needed];
		Assert.True(GetKernelObjectSecurity(handle, DaclSecurityInformation, buffer, needed, out _));
		return new RawSecurityDescriptor(buffer, 0).GetSddlForm(AccessControlSections.Access);
	}

	[DllImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, CharSet = CharSet.Unicode)]
	private static extern SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

	[DllImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte[]? descriptor, uint length, out uint needed);
}
