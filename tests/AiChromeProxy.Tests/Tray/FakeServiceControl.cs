using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

/// <summary>In-memory service: records the call order and can be told to fail.</summary>
public sealed class FakeServiceControl(ServiceState state = ServiceState.Running) : IServiceControl
{
	public ServiceState State { get; set; } = state;

	public List<string> Calls { get; } = [];

	public Exception? FailStart { get; set; }

	public Exception? FailStop { get; set; }

	public Exception? FailGetState { get; set; }

	public ServiceState GetState() => FailGetState is null ? State : throw FailGetState;

	public Task StartAsync(CancellationToken ct)
	{
		Calls.Add("start");
		if (FailStart is not null)
		{
			throw FailStart;
		}

		State = ServiceState.Running;
		return Task.CompletedTask;
	}

	public Task StopAsync(CancellationToken ct)
	{
		Calls.Add("stop");
		if (FailStop is not null)
		{
			throw FailStop;
		}

		State = ServiceState.Stopped;
		return Task.CompletedTask;
	}
}
