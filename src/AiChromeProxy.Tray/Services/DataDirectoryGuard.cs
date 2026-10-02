using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// Holds an open handle on a directory (no <c>FILE_SHARE_DELETE</c>) so it cannot be renamed, deleted or swapped for a link
/// while the elevated code checks it and then writes its ACL by path.
/// </summary>
public sealed partial class DataDirectoryGuard : IDisposable
{
	// FILE_LIST_DIRECTORY is needed: a handle with attribute-only access does not take part in share-mode checks.
	private const uint DirectoryAccess = 0x1 | 0x80;
	private const uint WriteDac = 0x40000;
	private const uint DaclSecurityInformation = 0x4;
	private const uint ReadControl = 0x20000;
	private const uint ShareReadWrite = 0x1 | 0x2;
	private const uint OpenExisting = 3;
	private const uint BackupSemanticsAndOpenReparsePoint = 0x02000000 | 0x00200000;

	private readonly SafeFileHandle _handle;

	private DataDirectoryGuard(SafeFileHandle handle, string path)
	{
		_handle = handle;
		DirectoryPath = path;
	}

	public string DirectoryPath { get; }

	/// <summary>Creates <paramref name="path"/> when missing, opens it and refuses it when it is a link.</summary>
	/// <exception cref="InvalidOperationException">The path is a reparse point.</exception>
	public static DataDirectoryGuard Acquire(string path)
	{
		Directory.CreateDirectory(path);
		var handle = Open(path);
		if (handle.IsInvalid)
		{
			var error = Marshal.GetLastPInvokeError();
			handle.Dispose();
			throw new IOException($"Cannot open {path} (error {error}).", new Win32Exception(error));
		}

		// Checked while the handle is held, so the path cannot change between this check and the caller's later use.
		if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
		{
			handle.Dispose();
			throw new InvalidOperationException($"{path} is a link; delete it and retry.");
		}

		return new DataDirectoryGuard(handle, path);
	}

	public void Dispose() => _handle.Dispose();

	/// <summary>Current security of the held directory (read while the handle is held).</summary>
	public DirectorySecurity GetSecurity() => new DirectoryInfo(DirectoryPath).GetAccessControl();

	/// <summary>
	/// Replaces only this directory's DACL through the held handle. Unlike <c>SetNamedSecurityInfo</c> (what <c>SetAccessControl</c> uses),
	/// <c>SetKernelObjectSecurity</c> does not propagate inheritable ACEs into existing children.
	/// </summary>
	public void SetDacl(DirectorySecurity security)
	{
		// The DACL keeps its inherited ACEs, so say so: a descriptor without the auto-inherited flag makes NTFS re-evaluate the children.
		var raw = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
		raw.SetFlags(raw.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
		var descriptor = new byte[raw.BinaryLength];
		raw.GetBinaryForm(descriptor, 0);
		if (!SetKernelObjectSecurity(_handle, DaclSecurityInformation, descriptor))
		{
			var error = Marshal.GetLastPInvokeError();
			throw new IOException($"Cannot set the permissions of {DirectoryPath} (error {error}).", new Win32Exception(error));
		}
	}

	// Win32 call: exercised by the guard tests but not meaningful to cover line by line.
	[ExcludeFromCodeCoverage]
	private static SafeFileHandle Open(string path) =>
		CreateFileW(path, DirectoryAccess | ReadControl | WriteDac, ShareReadWrite, IntPtr.Zero, OpenExisting, BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

	[LibraryImport("advapi32.dll", SetLastError = true)]
	[return: MarshalAs(UnmanagedType.Bool)]
	private static partial bool SetKernelObjectSecurity(SafeFileHandle handle, uint securityInformation, byte[] descriptor);
}
