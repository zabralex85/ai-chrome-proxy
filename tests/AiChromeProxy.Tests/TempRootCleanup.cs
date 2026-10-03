[assembly: AssemblyFixture(typeof(AiChromeProxy.Tests.TempRootCleanup))]

namespace AiChromeProxy.Tests;

/// <summary>
/// Deletes <c>%TEMP%\aicp-tests</c> once the whole run is over: each test class removes its own folder, but a run that was killed
/// or crashed never ran those Dispose methods, so their folders would pile up. The next run sweeps them.
/// </summary>
public sealed class TempRootCleanup : IDisposable
{
	public static readonly string Root = Path.Combine(Path.GetTempPath(), "aicp-tests");

	public void Dispose()
	{
		try
		{
			if (Directory.Exists(Root))
			{
				Directory.Delete(Root, recursive: true);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			// Best effort: another test run may still be using its folder.
		}
	}
}
