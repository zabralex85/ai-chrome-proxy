using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/ProjectsMenu.razor, TopBar.razor and MirrorTreeView.razor.</summary>
[Binding]
public sealed class ProjectsSteps(IPage page)
{
	[Given("the server holds the project {string} with the file {string} reading {string}")]
	public void GivenTheServerHoldsTheProject(string repo, string path, string text)
	{
		var full = Path.Combine(BrowserHooks.MirrorRoot, repo, path.Replace('/', Path.DirectorySeparatorChar));
		Directory.CreateDirectory(Path.GetDirectoryName(full)!);
		File.WriteAllText(full, text);
	}

	[When("I open the projects menu")]
	public async Task WhenIOpenTheProjectsMenuAsync() => await page.GetByTestId("projects-button").ClickAsync();

	[Then("the projects menu lists {string} as {string}")]
	public async Task ThenTheProjectsMenuListsAsync(string repo, string line) =>
		await Expect(page.Locator($"[data-testid=project-item][data-repo='{repo}']")).ToContainTextAsync(line);

	[When("I pick the project {string}")]
	public async Task WhenIPickTheProjectAsync(string repo) =>
		await page.Locator($"[data-testid=project-item][data-repo='{repo}']").ClickAsync();

	[Then("the shell shows {string} read-only")]
	public async Task ThenTheShellShowsReadOnlyAsync(string repo)
	{
		await Expect(page.GetByTestId("read-only")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(repo);
		await Expect(page.GetByTestId("mirror-tree")).ToBeVisibleAsync();
		await Expect(page.GetByTestId("chat-input")).ToBeDisabledAsync();
		await Expect(page.GetByTestId("open-settings")).ToBeDisabledAsync();
	}

	[When("I go back to my folder")]
	public async Task WhenIGoBackToMyFolderAsync() => await page.GetByTestId("back-to-folder").ClickAsync();

	[Then("the shell shows my folder {string} again")]
	public async Task ThenTheShellShowsMyFolderAgainAsync(string folder)
	{
		await Expect(page.GetByTestId("read-only")).ToHaveCountAsync(0);
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(folder);
		await Expect(page.GetByTestId("mirror-tree")).ToHaveCountAsync(0);
	}
}
