using System.Text.Json.Nodes;
using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/ClaudeToolsSection.razor.</summary>
[Binding]
public sealed class ClaudeToolsSteps(IPage page)
{
	private ILocator Section => page.GetByTestId("claude-tools");

	[Then("the Claude tools section says {string}")]
	public async Task ThenTheClaudeToolsSectionSaysAsync(string text) => await Expect(page.GetByTestId("tools-checked")).ToContainTextAsync(text);

	[When("I click Check now")]
	public async Task WhenIClickCheckNowAsync()
	{
		await Section.GetByRole(AriaRole.Button, new() { Name = "Check now" }).ClickAsync();
		await Expect(page.GetByTestId("tools-checked")).ToContainTextAsync("(by Check now)");
	}

	/// <param name="rows">"name: Badge, ..." in the order shown.</param>
	[Then("the MCP servers are {string}")]
	public async Task ThenTheMcpServersAreAsync(string rows)
	{
		var expected = rows.Split(", ");
		await Expect(page.Locator("[data-testid=tool-server] .tools-name")).ToHaveTextAsync([.. expected.Select(r => r[..r.IndexOf(':', StringComparison.Ordinal)])]);
		await Expect(page.Locator("[data-testid=tool-server] [data-testid=tool-status]")).ToHaveTextAsync([.. expected.Select(r => r[(r.IndexOf(':', StringComparison.Ordinal) + 2)..])]);
	}

	[Then("the plugins are {string}")]
	public async Task ThenThePluginsAreAsync(string names) =>
		await Expect(page.Locator("[data-testid=tool-plugin] .tools-name")).ToHaveTextAsync(names.Split(", "));

	[Then("the hint under {string} says {string}")]
	public async Task ThenTheHintUnderSaysAsync(string name, string hint) =>
		await Expect(Server(name).GetByTestId("tool-hint")).ToHaveTextAsync(hint);

	[Then("the failed server {string} shows the reason {string}")]
	public async Task ThenTheFailedServerShowsTheReasonAsync(string name, string reason) =>
		await Expect(Server(name).GetByTestId("tool-reason")).ToHaveTextAsync(reason);

	[Then("the server {string} has no switch and says {string}")]
	public async Task ThenTheServerHasNoSwitchAsync(string name, string source)
	{
		await Expect(Server(name).Locator(".tools-source")).ToHaveTextAsync(source);
		await Expect(Server(name).GetByRole(AriaRole.Switch)).ToHaveCountAsync(0);
	}

	[When("I switch {string} off")]
	public async Task WhenISwitchOffAsync(string label) => await page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true }).UncheckAsync();

	[Then("the switch {string} is off")]
	public async Task ThenTheSwitchIsOffAsync(string label) =>
		await Expect(page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true })).Not.ToBeCheckedAsync();

	[When("I save the settings")]
	public async Task WhenISaveTheSettingsAsync()
	{
		await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
		await Expect(page.GetByTestId("settings-status")).ToHaveTextAsync("Saved.");
	}

	[Then("the MCP server {string} says {string}")]
	public async Task ThenTheMcpServerSaysAsync(string name, string badge) =>
		await Expect(Server(name).GetByTestId("tool-status")).ToHaveTextAsync(badge);

	[Then("the run's --settings deny the MCP server {string} and turn off the plugin {string}")]
	public async Task ThenTheRunsSettingsDenyAsync(string server, string plugin)
	{
		var settings = (await RunSettingsAsync())!;
		Xunit.Assert.Equal(server, (string?)settings["deniedMcpServers"]![0]!["serverName"]);
		Xunit.Assert.False((bool)settings["enabledPlugins"]![plugin]!);
	}

	/// <summary>Writes <c>.mcp.json</c> straight into the repo's mirror (as Claude would), with one stdio server.</summary>
	[Given("the mirror's .mcp.json runs {string} as {string}")]
	[When("the mirror's .mcp.json runs {string} as {string}")]
	public async Task GivenTheMirrorsMcpJsonRunsAsync(string command, string name)
	{
		var parts = command.Split(' ');
		var json = new JsonObject { ["mcpServers"] = new JsonObject { [name] = new JsonObject { ["command"] = parts[0], ["args"] = new JsonArray([.. parts[1..].Select(a => (JsonNode?)a)]) } } };
		await File.WriteAllTextAsync(Path.Combine(BrowserHooks.MirrorRoot, await RepoAsync(), ".mcp.json"), json.ToJsonString());
	}

	[Then("the server {string} shows the command {string}")]
	public async Task ThenTheServerShowsTheCommandAsync(string name, string command) =>
		await Expect(Server(name).GetByTestId("tool-command")).ToHaveTextAsync(command);

	[When("I switch {string} on")]
	public async Task WhenISwitchOnAsync(string label) => await page.GetByRole(AriaRole.Switch, new() { Name = label, Exact = true }).CheckAsync();

	/// <summary>So that the next run's arguments are the ones read (the steps below wait for the file).</summary>
	[When("I forget the last run's arguments")]
	public async Task WhenIForgetTheLastRunsArgumentsAsync() => File.Delete(await ArgsFileAsync());

	[Then("the run's --settings approve the .mcp.json server {string}")]
	public async Task ThenTheRunsSettingsApproveAsync(string name) =>
		Xunit.Assert.Equal([name], (await RunSettingsAsync())?["enabledMcpjsonServers"]?.AsArray().Select(n => (string?)n) ?? []);

	[Then("the run's --settings do not approve the .mcp.json server {string}")]
	public async Task ThenTheRunsSettingsDoNotApproveAsync(string name) =>
		Xunit.Assert.DoesNotContain(name, (await RunSettingsAsync())?["enabledMcpjsonServers"]?.AsArray().Select(n => (string?)n) ?? []);

	/// <summary>When E2E_SCREENSHOT_DIR is set: the section at 1280x900 in the dark and the light theme, as claude-tools-{theme}.png.</summary>
	[Then("I save Claude tools screenshots")]
	public async Task ThenISaveClaudeToolsScreenshotsAsync()
	{
		var directory = Environment.GetEnvironmentVariable("E2E_SCREENSHOT_DIR");
		if (string.IsNullOrEmpty(directory))
		{
			return;
		}

		Directory.CreateDirectory(directory);
		await page.SetViewportSizeAsync(1280, 900);
		foreach (var theme in new[] { "dark", "light" })
		{
			for (var i = 0; i < 3 && await page.GetByTestId("shell").GetAttributeAsync("data-theme") != theme; i++)
			{
				await page.GetByTestId("theme-toggle").ClickAsync();
			}

			await Section.ScrollIntoViewIfNeededAsync();
			await page.WaitForTimeoutAsync(400);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"claude-tools-{theme}.png") });
			await Section.ScreenshotAsync(new() { Path = Path.Combine(directory, $"claude-tools-section-{theme}.png") });
		}
	}

	private ILocator Server(string name) => page.Locator($"[data-testid=tool-server][data-name='{name}']");

	private async Task<string> RepoAsync() => (await page.GetByTestId("folder-name").TextContentAsync())!;

	private async Task<string> ArgsFileAsync() => Path.Combine(BrowserHooks.ArgsDirectory, await RepoAsync() + ".args");

	/// <summary>The <c>--settings</c> JSON of the last run (null without one), once the fake agent has written its arguments.</summary>
	private async Task<JsonNode?> RunSettingsAsync()
	{
		var file = await ArgsFileAsync();
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (true)
		{
			try
			{
				var args = await File.ReadAllLinesAsync(file);
				var at = Array.IndexOf(args, "--settings");
				return at < 0 ? null : JsonNode.Parse(args[at + 1]);
			}
			catch (IOException) when (DateTime.UtcNow < deadline)
			{
				// Not written yet, or still being written by the fake agent.
				await page.WaitForTimeoutAsync(100);
			}
		}
	}
}
