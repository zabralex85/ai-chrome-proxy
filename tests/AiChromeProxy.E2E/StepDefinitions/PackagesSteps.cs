using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/PackagesView.razor and Shell/Explorer.razor. Both folders are made in the page's origin-private file system.</summary>
[Binding]
public sealed class PackagesSteps(IPage page)
{
	/// <summary>Set it to a folder to get the screenshots of the "I save package screenshots named" step.</summary>
	private const string ScreenshotDirectory = "E2E_SCREENSHOT_DIR";

	/// <summary>A sync pass (every 10 s) starts the packages pass that notices a change of the cache folder.</summary>
	private const float NextPassTimeout = 30_000;
	// The repo folder and a NuGet cache folder (picked with id 'aicp-pkg'). The metadata hash of the private package is computed here, like NuGet does.
	private const string PickerStub =
		"""
		async (names) => {
			const root = await navigator.storage.getDirectory();
			const write = async (dir, path, data) => {
				const parts = path.split('/');
				for (const part of parts.slice(0, -1)) { dir = await dir.getDirectoryHandle(part, { create: true }); }
				const file = await dir.getFileHandle(parts[parts.length - 1], { create: true });
				const writer = await file.createWritable(); await writer.write(data); await writer.close();
			};
			const library = (id, version) => ({ type: 'package', path: id.toLowerCase() + '/' + version });
			window.showDirectoryPicker = async (options) => {
				if (options && options.id === 'aicp-pkg') {
					const dir = await root.getDirectoryHandle(names.nuget, { create: true });
					const nupkg = new TextEncoder().encode('PK e2e private package');
					const hash = btoa(String.fromCharCode(...new Uint8Array(await crypto.subtle.digest('SHA-512', nupkg))));
					await write(dir, 'corp.private/1.2.3/corp.private.1.2.3.nupkg', nupkg);
					await write(dir, 'corp.private/1.2.3/corp.private.nuspec', '<package><metadata><id>Corp.Private</id><version>1.2.3</version></metadata></package>');
					await write(dir, 'corp.private/1.2.3/.nupkg.metadata', JSON.stringify({ version: 2, contentHash: hash, source: 'https://artifactory.corp.invalid/api/nuget/v3/corp/index.json' }));
					await write(dir, 'corp.private/1.2.3/lib/net10.0/corp.private.dll', 'MZ');
					await write(dir, 'humanizer.core/2.14.1/.nupkg.metadata', JSON.stringify({ version: 2, contentHash: hash, source: 'https://api.nuget.org/v3/index.json' }));
					return dir;
				}

				const dir = await root.getDirectoryHandle(names.repo, { create: true });
				await write(dir, 'README.md', '# e2e repo');
				await write(dir, 'app/app.csproj', '<Project Sdk="Microsoft.NET.Sdk"></Project>');
				await write(dir, 'app/obj/project.assets.json', JSON.stringify({ version: 3, libraries: {
					'Corp.Private/1.2.3': library('Corp.Private', '1.2.3'),
					'Humanizer.Core/2.14.1': library('Humanizer.Core', '2.14.1'),
				} }));
				return dir;
			};
		}
		""";

	/// <summary>The NuGet cache folder's name in the origin-private file system.</summary>
	private string _nuget = string.Empty;

	[Given("the app is connected with a synced folder that restored a private and a public package")]
	public async Task GivenTheAppIsConnectedWithARestoredFolderAsync()
	{
		var suffix = Guid.NewGuid().ToString("N")[..8];
		var repo = "e2e-repo-" + suffix;
		_nuget = "e2e-nuget-" + suffix;
		await page.AddInitScriptAsync($"({PickerStub})({{ repo: '{repo}', nuget: '{_nuget}' }});");
		await page.GotoAsync("/");
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
		await page.GetByTestId("open-folder").ClickAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(repo);
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^Synced"));
	}

	[When("I open the Packages tab")]
	public async Task WhenIOpenThePackagesTabAsync()
	{
		await page.GetByTestId("left-tab-packages").ClickAsync();
		await Expect(page.GetByTestId("packages-view")).ToBeVisibleAsync();
	}

	[When("I add the NuGet cache folder")]
	public async Task WhenIAddTheNuGetCacheFolderAsync()
	{
		await page.GetByTestId("packages-add-source").ClickAsync();
		await Expect(page.GetByTestId("packages-source")).ToContainTextAsync("Ready");
	}

	[Then("the package {string} {string} says {string}")]
	public async Task ThenThePackageSaysAsync(string id, string version, string text) =>
		await Expect(page.GetByTestId("packages-row").Filter(new() { HasText = id }).Filter(new() { HasText = version })).ToContainTextAsync(text);

	[When("the NuGet cache folder is deleted")]
	public async Task WhenTheNuGetCacheFolderIsDeletedAsync() =>
		await page.EvaluateAsync("async (name) => (await navigator.storage.getDirectory()).removeEntry(name, { recursive: true })", _nuget);

	[Then("the NuGet cache source says {string}")]
	public async Task ThenTheNuGetCacheSourceSaysAsync(string state) =>
		await Expect(page.GetByTestId("packages-source")).ToContainTextAsync(state, new() { Timeout = NextPassTimeout });

	[Then("the server holds {string} {string}")]
	public async Task ThenTheServerHoldsAsync(string id, string version)
	{
		var file = Path.Combine(BrowserHooks.PackagesRoot, "nuget", id, version, $"{id}.{version}.nupkg");
		for (var i = 0; i < 100 && !File.Exists(file); i++)
		{
			await Task.Delay(100);
		}

		Xunit.Assert.True(File.Exists(file), file);
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the page at 1280x720 in the dark and the light theme.</summary>
	[Then("I save package screenshots named {string}")]
	public async Task ThenISavePackageScreenshotsNamedAsync(string name)
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

			await page.WaitForTimeoutAsync(400);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"packages-{name}-{theme}.png") });
		}
	}
}
