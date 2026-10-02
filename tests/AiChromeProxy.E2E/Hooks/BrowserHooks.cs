using Microsoft.Playwright;
using Reqnroll;
using Reqnroll.BoDi;

namespace AiChromeProxy.E2E.Hooks;

/// <summary>One server and one browser per test run; a fresh browser context (and <see cref="IPage"/>) per scenario.</summary>
[Binding]
public sealed class BrowserHooks(IObjectContainer container)
{
	private static AppServer? _server;
	private static IPlaywright? _playwright;
	private static IBrowser? _browser;

	private IBrowserContext? _context;

	[BeforeTestRun]
	public static async Task StartAsync()
	{
		_server = new AppServer();
		_playwright = await Playwright.CreateAsync();
		_browser = await _playwright.Chromium.LaunchAsync(new() { Headless = Environment.GetEnvironmentVariable("HEADED") != "1" });
		Assertions.SetDefaultExpectTimeout(15_000);
	}

	[AfterTestRun]
	public static async Task StopAsync()
	{
		if (_browser is not null)
		{
			await _browser.DisposeAsync();
		}

		_playwright?.Dispose();
		if (_server is not null)
		{
			await _server.DisposeAsync();
		}
	}

	[BeforeScenario]
	public async Task OpenPageAsync()
	{
		_context = await _browser!.NewContextAsync(new() { BaseURL = _server!.BaseUrl });
		container.RegisterInstanceAs(await _context.NewPageAsync());
	}

	[AfterScenario]
	public async Task ClosePageAsync()
	{
		if (_context is not null)
		{
			await _context.DisposeAsync();
		}
	}
}
