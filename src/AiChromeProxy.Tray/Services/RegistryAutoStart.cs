using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value pointing at the running tray exe
/// (Velopack's stable <c>current\AiChromeProxy.Tray.exe</c>, so it survives updates).
/// Excluded from coverage: tests must not write the user's registry; covered by the manual checklist.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class RegistryAutoStart : IAutoStart
{
	private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
	private const string ValueName = "AiChromeProxy";

	public bool IsEnabled
	{
		get
		{
			using var key = Registry.CurrentUser.OpenSubKey(RunKey);
			return key?.GetValue(ValueName) is not null;
		}

		set
		{
			using var key = Registry.CurrentUser.CreateSubKey(RunKey);
			if (value)
			{
				key.SetValue(ValueName, $"\"{Environment.ProcessPath}\"");
			}
			else
			{
				key.DeleteValue(ValueName, throwOnMissingValue: false);
			}
		}
	}
}
