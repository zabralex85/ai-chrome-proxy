using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Pages/Home.razor and Shell/*.razor.</summary>
[Binding]
public sealed class ShellSteps(IPage page)
{
	[Then("I see the top bar, the file tree, the tabs, the main area and the chat input")]
	public async Task ThenISeeTheShellAsync()
	{
		await Expect(page.GetByTestId("top-bar")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("app-name")).ToHaveTextAsync("ai-chrome-proxy");
		await Expect(page.GetByTestId("file-tree")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("tabs")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("main")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("chat-input")).ToBeVisibleAsync();
	}

	[Then("I see the actions history, the sync status and the connection status")]
	public async Task ThenISeeTheHistoryAndStatusBoxesAsync()
	{
		await Expect(page.GetByTestId("actions-history")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("sync-status")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("connection-status")).ToBeVisibleAsync();
	}

	[Then("the user is {string}")]
	public async Task ThenTheUserIsAsync(string user)
	{
		await Expect(page.GetByTestId("user")).ToContainTextAsync(user);
	}

	[Then("the file tree offers {string}")]
	public async Task ThenTheFileTreeOffersAsync(string button)
	{
		await Expect(page.GetByTestId("open-folder")).ToContainTextAsync(button);
	}

	[Then("the Welcome tab is active and shows the welcome page")]
	public async Task ThenTheWelcomeTabIsActiveAsync()
	{
		await Expect(page.GetByTestId("tab-welcome")).ToHaveAttributeAsync("aria-selected", "true");
		await Expect(page.GetByTestId("welcome")).ToBeVisibleAsync();
	}

	[Then("the chat input is disabled")]
	public async Task ThenTheChatInputIsDisabledAsync()
	{
		await Expect(page.GetByTestId("chat-input")).ToBeDisabledAsync();
	}

	[Then("the sync status says {string}")]
	public async Task ThenTheSyncStatusSaysAsync(string text)
	{
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(text);
	}

	[Then("the theme is {string}")]
	public async Task ThenTheThemeIsAsync(string theme)
	{
		await Expect(page.GetByTestId("theme-toggle")).ToHaveAttributeAsync("data-theme-choice", theme);
		var shell = page.GetByTestId("shell");
		if (theme == "system")
		{
			await Expect(shell).Not.ToHaveAttributeAsync("data-theme", new Regex(".+"));
		}
		else
		{
			await Expect(shell).ToHaveAttributeAsync("data-theme", theme);
		}
	}

	[When("I click the theme toggle")]
	public async Task WhenIClickTheThemeToggleAsync()
	{
		await page.GetByTestId("theme-toggle").ClickAsync();
	}

	[When("I reload the app")]
	public async Task WhenIReloadTheAppAsync()
	{
		await page.ReloadAsync();
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
	}

	[When("I click the file tree toggle")]
	public async Task WhenIClickTheFileTreeToggleAsync()
	{
		await page.GetByTestId("toggle-files").ClickAsync();
	}

	// The panel stays rendered when collapsed (it keeps its state) and is hidden by CSS.
	[Then("the file tree is hidden")]
	public async Task ThenTheFileTreeIsHiddenAsync()
	{
		await Expect(page.GetByTestId("explorer")).ToBeHiddenAsync();
		await Expect(page.GetByTestId("toggle-files")).ToHaveAttributeAsync("aria-pressed", "false");
	}

	[Then("the file tree is visible")]
	public async Task ThenTheFileTreeIsVisibleAsync()
	{
		await Expect(page.GetByTestId("explorer")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("toggle-files")).ToHaveAttributeAsync("aria-pressed", "true");
	}

	[When("I click the error count in the sync status")]
	public async Task WhenIClickTheErrorCountAsync()
	{
		await page.GetByTestId("status-errors").ClickAsync();
	}

	[Then("the error list says {string}")]
	public async Task ThenTheErrorListSaysAsync(string text)
	{
		await Expect(page.GetByTestId("error-list")).ToContainTextAsync(text);
	}

	// The folder picker cannot be automated: the page's own origin-private file system stands in for the picked folder.
	[Given("the app is connected with a folder open")]
	public async Task GivenTheAppIsConnectedWithAFolderOpenAsync()
	{
		await page.AddInitScriptAsync("window.showDirectoryPicker = async () => (await navigator.storage.getDirectory()).getDirectoryHandle('e2e-repo', { create: true });");
		await page.GotoAsync("/");
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
		await page.GetByTestId("open-folder").ClickAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync("e2e-repo");
	}

	[Then("the project settings button is disabled")]
	public async Task ThenTheProjectSettingsButtonIsDisabledAsync()
	{
		await Expect(page.GetByTestId("open-settings")).ToBeDisabledAsync();
	}

	[When("I click the project settings button")]
	public async Task WhenIClickTheProjectSettingsButtonAsync()
	{
		await page.GetByRole(AriaRole.Button, new() { Name = "Project settings" }).ClickAsync();
	}

	[Then("the Project settings tab is active")]
	public async Task ThenTheProjectSettingsTabIsActiveAsync()
	{
		await Expect(page.GetByTestId("tab-settings")).ToHaveAttributeAsync("aria-selected", "true");
		await Expect(page.GetByTestId("settings")).ToBeVisibleAsync();
	}

	[Then("the settings tab shows the excludes, the automatic apply option and a disabled Save button")]
	public async Task ThenTheSettingsTabShowsItsFieldsAsync()
	{
		await Expect(page.GetByLabel("Extra excludes (.gitignore syntax)")).ToBeVisibleAsync();
		await Expect(page.GetByLabel("Apply server changes automatically")).ToBeCheckedAsync();
		await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Save" })).ToBeDisabledAsync();
	}
}
