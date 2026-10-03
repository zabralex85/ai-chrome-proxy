using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

public sealed class FakeAutoStart : IAutoStart
{
	public bool IsEnabled { get; set; }
}
