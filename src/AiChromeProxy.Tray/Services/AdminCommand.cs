using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Security.Principal;

namespace AiChromeProxy.Tray.Services;

/// <summary>
/// <c>AiChromeProxy.Tray.exe --admin install|uninstall "DOMAIN\user"</c>: the only elevated entry points.
/// The user is the tray's (non-elevated) user, who gets start/stop rights; with over-the-shoulder UAC the elevated
/// identity is a different admin, so it is passed explicitly. The password is typed into the elevated dialog, never passed.
/// </summary>
public static class AdminCommand
{
	public const string Flag = "--admin";
	public const string Install = "install";
	public const string Uninstall = "uninstall";

	private const int ErrorCancelled = 1223;

	public static string CurrentUser => WindowsIdentity.GetCurrent().Name;

	/// <returns>The command and the tray user, or null when <paramref name="args"/> is not an admin command line.</returns>
	public static (string Command, string User)? Parse(IReadOnlyList<string>? args) =>
		args is [Flag, Install or Uninstall, var user] && !string.IsNullOrWhiteSpace(user) ? (args[1], user) : null;

	public static string Arguments(string command, string user) => $"{Flag} {command} \"{user}\"";

	/// <summary>The headless <c>--admin uninstall</c> instance (Velopack's before-uninstall hook and the tray menu).</summary>
	/// <returns>Process exit code: 0 on success.</returns>
	public static int RunUninstall(IServiceControl service)
	{
		try
		{
			service.Uninstall();
			return 0;
		}
		catch (Exception)
		{
			return 1;
		}
	}

	/// <summary>Relaunches this exe elevated (UAC prompt) and waits for it.</summary>
	/// <returns>Its exit code, or null when the user declined the UAC prompt.</returns>
	[ExcludeFromCodeCoverage]
	public static async Task<int?> RunElevatedAsync(string command)
	{
		try
		{
			using var process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, Arguments(command, CurrentUser))
			{
				UseShellExecute = true,
				Verb = "runas",
			});
			await process!.WaitForExitAsync();
			return process.ExitCode;
		}
		catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
		{
			return null;
		}
	}
}
