using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
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
	private const uint ReadControl = 0x20000;
	private const uint ShareReadWrite = 0x1 | 0x2;
	private const uint OpenExisting = 3;
	private const uint BackupSemanticsAndOpenReparsePoint = 0x02000000 | 0x00200000;

	private readonly SafeFileHandle _handle;

	private DataDirectoryGuard(SafeFileHandle handle) => _handle = handle;

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
			throw new IOException($"Cannot open {path} (error {error}).", error);
		}

		// Checked while the handle is held, so the path cannot change between this check and the caller's later use.
		if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
		{
			handle.Dispose();
			throw new InvalidOperationException($"{path} is a link; delete it and retry.");
		}

		return new DataDirectoryGuard(handle);
	}

	public void Dispose() => _handle.Dispose();

	// Win32 call: exercised by the guard tests but not meaningful to cover line by line.
	[ExcludeFromCodeCoverage]
	private static SafeFileHandle Open(string path) =>
		CreateFileW(path, DirectoryAccess | ReadControl, ShareReadWrite, IntPtr.Zero, OpenExisting, BackupSemanticsAndOpenReparsePoint, IntPtr.Zero);

	[LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
	private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
}
