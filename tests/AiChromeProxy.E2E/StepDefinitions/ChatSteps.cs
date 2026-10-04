using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/ChatView.razor, ChatInput.razor, AgentStatus.razor and MainArea.razor.</summary>
[Binding]
public sealed class ChatSteps(IPage page)
{
	/// <summary>Set it to a folder to get the screenshots of the "I save screenshots named" steps (for a visual check; nothing is saved otherwise).</summary>
	private const string ScreenshotDirectory = "E2E_SCREENSHOT_DIR";

	// The page's own origin-private file system stands in for the picked folder, with one file so that there is something to sync.
	// Every scenario gets a folder of its own: the server runs one agent per folder, and the scenarios share the server.
	[Given("the app is connected with a synced folder open")]
	public async Task GivenTheAppIsConnectedWithASyncedFolderOpenAsync()
	{
		var folder = "e2e-repo-" + Guid.NewGuid().ToString("N")[..8];
		await page.AddInitScriptAsync(
			"window.showDirectoryPicker = async () => {"
			+ $" const dir = await (await navigator.storage.getDirectory()).getDirectoryHandle('{folder}', {{ create: true }});"
			+ " const file = await dir.getFileHandle('README.md', { create: true });"
			+ " const writer = await file.createWritable(); await writer.write('# e2e repo'); await writer.close();"
			+ " return dir; };");
		await page.GotoAsync("/");
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
		await page.GetByTestId("open-folder").ClickAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(folder);
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(new Regex("^Synced"));
	}

	[Then("the chat input is enabled")]
	public async Task ThenTheChatInputIsEnabledAsync() => await Expect(page.GetByTestId("chat-input")).ToBeEnabledAsync();

	[When("I type {string} and press Enter")]
	public async Task WhenITypeAndPressEnterAsync(string text)
	{
		await page.GetByTestId("chat-input").FillAsync(text);
		await page.GetByTestId("chat-input").PressAsync("Enter");
	}

	[When("I type {string} and press Shift+Enter and type {string}")]
	public async Task WhenITypeAndPressShiftEnterAsync(string first, string second)
	{
		var input = page.GetByTestId("chat-input");
		await input.FillAsync(first);
		await input.PressAsync("Shift+Enter");
		await input.PressSequentiallyAsync(second);
	}

	[Then("the chat input holds two lines")]
	public async Task ThenTheChatInputHoldsTwoLinesAsync() => await Expect(page.GetByTestId("chat-input")).ToHaveValueAsync("first\nsecond");

	[Then("no message is shown")]
	public async Task ThenNoMessageIsShownAsync() => await Expect(page.GetByTestId("chat-user")).ToHaveCountAsync(0);

	[Then("the Chat tab is active")]
	public async Task ThenTheChatTabIsActiveAsync() => await Expect(page.GetByTestId("tab-chat")).ToHaveAttributeAsync("aria-selected", "true");

	[Then("the New chat tab is active")]
	public async Task ThenTheNewChatTabIsActiveAsync() =>
		await Expect(page.Locator("[data-testid=tab-chat][aria-selected=true]")).ToContainTextAsync("New chat");

	[Then("the chat is empty")]
	public async Task ThenTheChatIsEmptyAsync() => await Expect(page.GetByTestId("chat-empty")).ToBeVisibleAsync();

	[Then("my message {string} is shown")]
	public async Task ThenMyMessageIsShownAsync(string text) => await Expect(page.GetByTestId("chat-user").First).ToHaveTextAsync(text);

	[Then("Claude's answer says {string}")]
	public async Task ThenClaudesAnswerSaysAsync(string text) =>
		await Expect(page.GetByTestId("chat-assistant").Filter(new() { HasText = text }).First).ToBeVisibleAsync();

	[Then("the answer contains a drawn diagram")]
	public async Task ThenTheAnswerContainsADrawnDiagramAsync()
	{
		await Expect(page.Locator("[data-testid=chat-assistant] .mermaid-source svg").First).ToBeVisibleAsync();
		await Expect(page.Locator(".mermaid-error")).ToHaveCountAsync(0);
	}

	[Then("the answer lists {string}")]
	public async Task ThenTheAnswerListsAsync(string text) =>
		await Expect(page.Locator("[data-testid=chat-assistant] li").Filter(new() { HasText = text }).First).ToBeVisibleAsync();

	[Then("the chat shows a tool row for {string}")]
	public async Task ThenTheChatShowsAToolRowAsync(string tool)
	{
		var row = page.GetByTestId("chat-tool").Filter(new() { HasText = tool }).First;
		await Expect(row).ToBeVisibleAsync();
		await Expect(row).ToContainTextAsync("dotnet build");
	}

	[Then("the permission card asks about {string}")]
	public async Task ThenThePermissionCardAsksAboutAsync(string command)
	{
		await Expect(page.GetByTestId("approval-card")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("approval-summary")).ToContainTextAsync(command);
		await Expect(page.GetByTestId("approval-allow-always")).ToHaveTextAsync("Allow always in this project");
		await Expect(page.GetByTestId("approval-deny")).ToHaveTextAsync("Deny");
	}

	[When("I click {string} on the permission card")]
	public async Task WhenIClickOnThePermissionCardAsync(string button) =>
		await page.GetByTestId("approval-card").GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync();

	[Then("the permission card is gone")]
	public async Task ThenThePermissionCardIsGoneAsync() => await Expect(page.GetByTestId("approval-card")).ToHaveCountAsync(0);

	[Then("the agent status says {string}")]
	public async Task ThenTheAgentStatusSaysAsync(string text) => await Expect(page.GetByTestId("agent-state")).ToContainTextAsync(text);

	[Then("the last run cost is {string}")]
	public async Task ThenTheLastRunCostIsAsync(string cost) => await Expect(page.GetByTestId("agent-cost")).ToHaveTextAsync(cost);

	[When("I click the code link {string} in the answer")]
	public async Task WhenIClickTheCodeLinkAsync(string text) =>
		await page.Locator("[data-testid=chat-assistant] a").Filter(new() { HasText = text }).First.ClickAsync();

	[Then("a file tab {string} is active")]
	public async Task ThenAFileTabIsActiveAsync(string name)
	{
		await Expect(page.GetByTestId("tab-file")).ToHaveAttributeAsync("aria-selected", "true");
		await Expect(page.GetByTestId("tab-file")).ToContainTextAsync(name);
	}

	[Then("the send button has become Stop")]
	public async Task ThenTheSendButtonHasBecomeStopAsync() => await Expect(page.GetByTestId("chat-stop")).ToBeVisibleAsync();

	[When("I click Stop")]
	public async Task WhenIClickStopAsync() => await page.GetByTestId("chat-stop").ClickAsync();

	[Then("the send button is Send again")]
	public async Task ThenTheSendButtonIsSendAgainAsync()
	{
		await Expect(page.GetByTestId("chat-send")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("chat-stop")).ToHaveCountAsync(0);
	}

	[When("I open the chats menu")]
	public async Task WhenIOpenTheChatsMenuAsync() => await page.GetByTestId("chat-menu-button").ClickAsync();

	[Then("the menu lists a chat titled {string}")]
	public async Task ThenTheMenuListsAChatTitledAsync(string title) =>
		await Expect(page.GetByTestId("chat-session").Filter(new() { HasText = title }).First).ToBeVisibleAsync();

	[When("I choose New chat from the menu")]
	public async Task WhenIChooseNewChatAsync() => await page.GetByTestId("new-chat").ClickAsync();

	[Then("the settings tab offers the three permission options with {string} chosen")]
	public async Task ThenTheSettingsTabOffersThePermissionOptionsAsync(string chosen)
	{
		await Expect(page.GetByLabel("Ask before commands (edits are applied)")).ToBeVisibleAsync();
		await Expect(page.GetByLabel("Allow everything")).ToBeVisibleAsync();
		await Expect(page.GetByLabel("Only what my Claude Code settings allow")).ToBeVisibleAsync();
		await Expect(page.GetByLabel(chosen)).ToBeCheckedAsync();
	}

	[Then("the model field is empty and says {string}")]
	public async Task ThenTheModelFieldIsEmptyAsync(string placeholder)
	{
		await Expect(page.GetByLabel("Model", new() { Exact = true })).ToHaveValueAsync(string.Empty);
		await Expect(page.GetByLabel("Model", new() { Exact = true })).ToHaveAttributeAsync("placeholder", placeholder);
	}

	[When("I choose {string} and type the model {string} and save")]
	public async Task WhenIChooseAndTypeTheModelAndSaveAsync(string permission, string model)
	{
		await page.GetByLabel(permission).CheckAsync();
		await page.GetByLabel("Model", new() { Exact = true }).FillAsync(model);
		await page.GetByRole(AriaRole.Button, new() { Name = "Save" }).ClickAsync();
	}

	[Then("the settings are saved")]
	public async Task ThenTheSettingsAreSavedAsync()
	{
		await Expect(page.GetByTestId("settings-status")).ToHaveTextAsync("Saved.");
		await Expect(page.GetByLabel("Allow everything")).ToBeCheckedAsync();
		await Expect(page.GetByLabel("Model", new() { Exact = true })).ToHaveValueAsync("opus");
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the page at 1280x720 in the dark and the light theme, once the transitions are over.</summary>
	[Then("I save screenshots named {string}")]
	public async Task ThenISaveScreenshotsNamedAsync(string name)
	{
		var directory = Environment.GetEnvironmentVariable(ScreenshotDirectory);
		if (string.IsNullOrEmpty(directory))
		{
			return;
		}

		Directory.CreateDirectory(directory);
		await page.SetViewportSizeAsync(1280, 720);
		foreach (var theme in new[] { "dark", "light" })
		{
			for (var i = 0; i < 3 && await page.GetByTestId("shell").GetAttributeAsync("data-theme") != theme; i++)
			{
				await page.GetByTestId("theme-toggle").ClickAsync();
			}

			// The theme transition and the diagram redraw are over after this.
			await page.WaitForTimeoutAsync(1000);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"chat-{name}-{theme}.png") });
		}
	}
}
