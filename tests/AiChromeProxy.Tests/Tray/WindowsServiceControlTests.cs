using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>Read-only queries against the real Service Control Manager (no elevation, nothing changed).</summary>
public sealed class WindowsServiceControlTests
{
	[Fact]
	public void UnknownService_NotInstalled()
	{
		Assert.Equal(ServiceState.NotInstalled, new WindowsServiceControl("AiChromeProxyTests-" + Guid.NewGuid().ToString("N")).GetState());
	}

	[Fact]
	public void EventLogService_Running()
	{
		Assert.Equal(ServiceState.Running, new WindowsServiceControl("EventLog").GetState());
	}

	[Fact]
	public void BinaryPathName_UnknownService_Null()
	{
		Assert.Null(new WindowsServiceControl("AiChromeProxyTests-" + Guid.NewGuid().ToString("N")).GetBinaryPathName());
	}

	[Fact]
	public void BinaryPathName_EventLogService_Svchost()
	{
		Assert.Contains("svchost.exe", new WindowsServiceControl("EventLog").GetBinaryPathName(), StringComparison.OrdinalIgnoreCase);
	}
}
