using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AiChromeProxy.E2E.Hooks;

/// <summary>The real Server on a Kestrel listener (a free loopback port) so a real browser can reach it.</summary>
public sealed class AppServer : IAsyncDisposable
{
	private readonly WebApplicationFactory<Program> _factory;

	public AppServer()
	{
		// A fresh mirror and database per run: project settings and baselines of an earlier run must not leak in.
		var root = Path.Combine(Path.GetTempPath(), "aicp-e2e", Guid.NewGuid().ToString("N"));
		MirrorRoot = Path.Combine(root, "mirror");
		ArgsDirectory = Directory.CreateDirectory(Path.Combine(root, "args")).FullName;
		_factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
		{
			b.UseEnvironment(Environments.Development);
			b.UseSetting("CloudflareAccess:Enabled", "false");

			// A free port, not 0: the approval endpoint's address that the agent is given comes from the configured port.
			b.UseSetting("Server:Port", FreePort().ToString(System.Globalization.CultureInfo.InvariantCulture));
			b.UseSetting("Mirror:Root", MirrorRoot);
			b.UseSetting("Projects:Database", Path.Combine(root, "aicp.db"));

			// Claude is a fake agent: it prints the fixture named after the first word of the message (Fixtures/tour.jsonl, slow.jsonl, twotext.jsonl), a little slowly.
			b.UseSetting("Agent:Command", Path.Combine(AppContext.BaseDirectory, "AiChromeProxy.FakeAgent.exe"));
			b.UseSetting("Agent:Env:FAKE_AGENT_SCRIPT_DIR", Path.Combine(AppContext.BaseDirectory, "Fixtures"));
			b.UseSetting("Agent:Env:FAKE_AGENT_DELAY_MS", "120");

			// Check now: the fake's answers to `mcp list` and `plugin list --json`; each run's arguments land in <ArgsDirectory>/<repo>.args.
			b.UseSetting("Agent:Env:FAKE_AGENT_MCP_LIST", Path.Combine(AppContext.BaseDirectory, "Fixtures", "mcp-list.txt"));
			b.UseSetting("Agent:Env:FAKE_AGENT_PLUGIN_LIST", Path.Combine(AppContext.BaseDirectory, "Fixtures", "plugin-list.json"));
			b.UseSetting("Agent:Env:FAKE_AGENT_ARGS_DIR", ArgsDirectory);
		});
		_factory.UseKestrel();
		_factory.StartServer();

		var addresses = _factory.Services.GetRequiredService<IServer>().Features.GetRequiredFeature<IServerAddressesFeature>().Addresses;
		BaseUrl = addresses.Single();
	}

	public string BaseUrl { get; }

	/// <summary>Where the server keeps the mirrors (one folder per repo).</summary>
	public string MirrorRoot { get; }

	/// <summary>Where the fake agent writes each run's arguments, as <c>&lt;repo&gt;.args</c>.</summary>
	public string ArgsDirectory { get; }

	public ValueTask DisposeAsync() => _factory.DisposeAsync();

	private static int FreePort()
	{
		var listener = new TcpListener(IPAddress.Loopback, 0);
		listener.Start();
		try
		{
			return ((IPEndPoint)listener.LocalEndpoint).Port;
		}
		finally
		{
			listener.Stop();
		}
	}
}
