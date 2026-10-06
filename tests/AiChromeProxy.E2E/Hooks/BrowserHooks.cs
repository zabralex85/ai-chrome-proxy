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

	private readonly List<string> _violations = [];

	private IBrowserContext? _context;

	/// <summary>Where the server keeps the mirrors of this run.</summary>
	public static string MirrorRoot => _server!.MirrorRoot;

	/// <summary>Where the fake agent writes each run's arguments (<c>&lt;repo&gt;.args</c>).</summary>
	public static string ArgsDirectory => _server!.ArgsDirectory;

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

		// The page's Content-Security-Policy must never block anything of the app itself: a violation fails the scenario.
		await _context.AddInitScriptAsync("window.__cspViolations = []; document.addEventListener('securitypolicyviolation', e => window.__cspViolations.push(e.violatedDirective + ' ' + e.blockedURI));");
		var page = await _context.NewPageAsync();
		page.Console += (_, message) =>
		{
			if (message.Text.Contains("Content Security Policy", StringComparison.OrdinalIgnoreCase))
			{
				lock (_violations)
				{
					_violations.Add(message.Text);
				}
			}
		};
		container.RegisterInstanceAs(page);
	}

	[AfterScenario]
	public async Task ClosePageAsync()
	{
		if (_context is not null)
		{
			var seen = new List<string>();
			lock (_violations)
			{
				seen.AddRange(_violations);
				_violations.Clear();
			}

			try
			{
				seen.AddRange(await container.Resolve<IPage>().EvaluateAsync<string[]>("window.__cspViolations || []"));
			}
			catch (PlaywrightException)
			{
				// The page is gone; the console messages above still count.
			}

			await _context.DisposeAsync();
			Xunit.Assert.True(seen.Count == 0, "Content-Security-Policy violations:\n" + string.Join('\n', seen));
		}
	}
}
