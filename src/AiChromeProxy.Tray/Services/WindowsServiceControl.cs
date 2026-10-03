using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.ServiceProcess;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary>Status, start and stop through the Service Control Manager; no elevation once the service DACL grants the user start/stop.</summary>
public sealed class WindowsServiceControl(string serviceName = WindowsServiceControl.ServiceName) : IServiceControl
{
	public const string ServiceName = "AiChromeProxy";

	private const int ErrorServiceDoesNotExist = 1060;
	private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

	public ServiceState GetState()
	{
		using (var service = new ServiceController(serviceName))
		{
			try
			{
				return service.Status switch
				{
					ServiceControllerStatus.Running => ServiceState.Running,
					ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => ServiceState.Starting,
					ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending => ServiceState.Stopping,
					_ => ServiceState.Stopped,
				};
			}
			catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: ErrorServiceDoesNotExist })
			{
				return ServiceState.NotInstalled;
			}
		}
	}

	public string? GetBinaryPathName() => ServiceInstaller.QueryBinaryPathName(serviceName);

	/// <summary>Not unit-tested: starting a real service needs one installed with a DACL for the test user (manual checklist).</summary>
	[ExcludeFromCodeCoverage]
	public Task StartAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using (var service = new ServiceController(serviceName))
			{
				if (service.Status == ServiceControllerStatus.Stopped)
				{
					service.Start();
				}

				service.WaitForStatus(ServiceControllerStatus.Running, Timeout);
			}
		},
		ct);

	[ExcludeFromCodeCoverage]
	public Task StopAsync(CancellationToken ct) => Task.Run(
		() =>
		{
			using (var service = new ServiceController(serviceName))
			{
				if (service.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending)
				{
					// Stop() would enumerate dependent services first, which needs SERVICE_ENUMERATE_DEPENDENTS; there are none.
					service.Stop(stopDependentServices: false);
				}

				service.WaitForStatus(ServiceControllerStatus.Stopped, Timeout);
			}
		},
		ct);

	/// <summary>Always the default data directory: <c>AICP_DATA_DIR</c> can be set unelevated (HKCU), so the elevated code must not trust it.</summary>
	[ExcludeFromCodeCoverage]
	public void Install(string account, string password, string controlUser) =>
		ServiceInstaller.Install(serviceName, account, password, controlUser, DataDirectory.Resolve(null));

	[ExcludeFromCodeCoverage]
	public void Uninstall() => ServiceInstaller.Uninstall(serviceName);
}
