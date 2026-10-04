using System.Text.RegularExpressions;
using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/FileTreeView.razor, TreeContextMenu, TreeNameInput and ConfirmDialog; the folder is the page's origin-private file system.</summary>
[Binding]
public sealed class TreeActionSteps(IPage page)
{
	/// <summary>Set it to a folder to get the screenshots of the "I save tree screenshots named" steps.</summary>
	private const string ScreenshotDirectory = "E2E_SCREENSHOT_DIR";

	private const string ReadFile =
		"""
		async ([folder, name]) => {
			const dir = await (await navigator.storage.getDirectory()).getDirectoryHandle(folder);
			try { return await (await (await dir.getFileHandle(name)).getFile()).text(); } catch (e) { return null; }
		}
		""";

	private string _folder = string.Empty;
	private string _suffix = string.Empty;

	private ILocator Menu => page.GetByTestId("tree-menu");

	private ILocator Dialog => page.GetByTestId("confirm-dialog");

	private ILocator Input => page.GetByTestId("tree-input");

	[Given("the app is connected with a folder for tree actions")]
	public async Task GivenConnectedAsync() => await ConnectAsync(copy: false, blocked: null);

	[Given("the app is connected with a folder for tree actions and a browser that renames by {string}")]
	public async Task GivenConnectedByAsync(string mode) => await ConnectAsync(copy: mode == "copy", blocked: null);

	[Given("the app is connected with a folder for tree actions and a browser that renames by {string} and cannot write {string}")]
	public async Task GivenConnectedCannotWriteAsync(string mode, string blocked) => await ConnectAsync(copy: mode == "copy", blocked);

	[When("I right-click {string} in the file tree")]
	public async Task WhenIRightClickAsync(string path)
	{
		await Row(path).ClickAsync(new() { Button = MouseButton.Right });
		await Expect(Menu).ToBeVisibleAsync();
	}

	[When("I right-click {string} in the file tree and choose {string}")]
	public async Task WhenIRightClickAndChooseAsync(string path, string item)
	{
		await WhenIRightClickAsync(path);
		await MenuItem(item).ClickAsync();
	}

	[When("I right-click the empty part of the tree and choose {string}")]
	public async Task WhenIRightClickEmptyAndChooseAsync(string item)
	{
		await RightClickEmptyAsync();
		await MenuItem(item).ClickAsync();
	}

	[When("I type the name {string} and press Enter")]
	public async Task WhenITypeTheNameAsync(string name)
	{
		await Input.FillAsync(name);
		await Input.PressAsync("Enter");
	}

	[When("I press F2 on {string} and rename it to {string}")]
	public async Task WhenIPressF2Async(string path, string name)
	{
		await Row(path).FocusAsync();
		await page.Keyboard.PressAsync("F2");
		await Expect(Input).ToBeVisibleAsync();
		await Input.FillAsync(name);
		await Input.PressAsync("Enter");
	}

	[When("I type the name {string} without pressing Enter")]
	public async Task WhenITypeWithoutEnterAsync(string name) => await Input.FillAsync(name);

	[When("I click elsewhere")]
	public async Task WhenIClickElsewhereAsync() => await page.GetByTestId("main").ClickAsync();

	[When("I click the text of the dialog")]
	public async Task WhenIClickTheDialogTextAsync() => await page.GetByTestId("dialog-body").ClickAsync();

	[When("I click the dimmed part of the dialog")]
	public async Task WhenIClickTheDimmedPartAsync() => await Dialog.ClickAsync(new() { Position = new() { X = 5, Y = 5 } });

	[When("I press Tab {int} times, the focus never reaches the page behind the dialog")]
	public async Task WhenIPressTabAsync(int times)
	{
		for (var i = 0; i < times; i++)
		{
			await page.Keyboard.PressAsync("Tab");
			Xunit.Assert.True(await InsideDialogAsync(), "The focus reached the page behind the dialog.");
		}
	}

	[When("I press {word}")]
	public async Task WhenIPressAsync(string key) => await page.Keyboard.PressAsync(key);

	[When("I press the {string} button of the dialog")]
	public async Task WhenIPressTheDialogButtonAsync(string button) =>
		await Dialog.GetByRole(AriaRole.Button, new() { Name = button, Exact = true }).ClickAsync();

	[When("I focus the tree row {string}")]
	public async Task WhenIFocusTheRowAsync(string path) => await Row(path).FocusAsync();

	[Then("the file tree shows {string}")]
	public async Task ThenTheTreeShowsAsync(string path) => await Expect(Row(path)).ToHaveCountAsync(1);

	[Then("the file tree does not show {string}")]
	public async Task ThenTheTreeDoesNotShowAsync(string path) => await Expect(Row(path)).ToHaveCountAsync(0);

	[Then("{string} has no children in the tree")]
	public async Task ThenNoChildrenAsync(string path)
	{
		await Expect(Row(path)).ToHaveAttributeAsync("aria-expanded", "true");
		await Expect(page.Locator($"[role=treeitem][title^='{path}/']")).ToHaveCountAsync(0);
	}

	[Then("the name input says {string}")]
	public async Task ThenTheNameInputSaysAsync(string text)
	{
		await Expect(page.GetByTestId("tree-error")).ToHaveTextAsync(text);
		await Expect(Input).ToBeVisibleAsync();
	}

	[Then("no name input is open")]
	public async Task ThenNoInputAsync() => await Expect(Input).ToHaveCountAsync(0);

	[Then("no name error is shown")]
	public async Task ThenNoErrorAsync() => await Expect(page.GetByTestId("tree-error")).ToHaveCountAsync(0);

	[Then("the file {string} is in the mirror folder")]
	public async Task ThenTheMirrorHasAsync(string path) => await WaitMirrorAsync(path, present: true);

	[Then("the name input says a failure")]
	public async Task ThenTheNameInputSaysAFailureAsync()
	{
		await Expect(page.GetByTestId("tree-error")).ToContainTextAsync("Disk full");
		await Expect(Input).ToBeVisibleAsync();
	}

	[Then("the tree note says {string}")]
	public async Task ThenTheNoteSaysAsync(string text) => await Expect(page.GetByTestId("tree-note")).ToHaveTextAsync(text);

	[Then("the file tab {string} is active with the viewer open")]
	public async Task ThenTheTabIsActiveAsync(string name)
	{
		await Expect(page.Locator("[data-testid=tab-file][aria-selected=true] .tab-label")).ToHaveTextAsync(name);
		await Expect(page.Locator("[data-testid=file-view]:not([hidden]) .monaco-editor")).ToBeVisibleAsync();
	}

	[Then("the file tab {string} is active with the viewer showing {string}")]
	public async Task ThenTheTabIsActiveShowingAsync(string name, string text)
	{
		await ThenTheTabIsActiveAsync(name);
		await Expect(page.Locator("[data-testid=file-view]:not([hidden]) .view-lines")).ToContainTextAsync(text);
	}

	[Then("the active tab is for the path {string}")]
	public async Task ThenTheActiveTabPathAsync(string path) =>
		await Expect(page.Locator("[data-testid=tab-file][aria-selected=true]")).ToHaveAttributeAsync("data-path", path);

	[Then("no file tab {string} is open")]
	public async Task ThenNoTabAsync(string name) =>
		await Expect(page.Locator("[data-testid=tab-file] .tab-label").Filter(new() { HasTextRegex = new Regex("^" + Regex.Escape(name) + "$") })).ToHaveCountAsync(0);

	[Then("the menu item {string} is disabled with the tooltip {string}")]
	public async Task ThenTheItemIsDisabledAsync(string item, string tooltip)
	{
		await Expect(MenuItem(item)).ToHaveAttributeAsync("aria-disabled", "true");
		await Expect(MenuItem(item)).ToHaveAttributeAsync("title", tooltip);
	}

	[Then("the menu is open with {string} focused")]
	public async Task ThenTheMenuIsOpenAsync(string item)
	{
		await Expect(Menu).ToBeVisibleAsync();
		await Expect(MenuItem(item)).ToBeFocusedAsync();
	}

	[Then("the menu is closed")]
	public async Task ThenTheMenuIsClosedAsync() => await Expect(Menu).ToHaveCountAsync(0);

	[Then("the tree row {string} has the focus")]
	public async Task ThenTheRowHasTheFocusAsync(string path) => await Expect(Row(path)).ToBeFocusedAsync();

	[Then("the dialog asks {string}")]
	public async Task ThenTheDialogAsksAsync(string text) => await Expect(page.GetByTestId("dialog-title")).ToHaveTextAsync(text);

	[Then("the dialog says {string}")]
	public async Task ThenTheDialogSaysAsync(string text) => await Expect(page.GetByTestId("dialog-body")).ToHaveTextAsync(text);

	[Then("the dialog has the focus on {string}")]
	public async Task ThenTheDialogFocusAsync(string button)
	{
		Xunit.Assert.True(await Dialog.EvaluateAsync<bool>("d => d.matches(':modal')"), "The dialog is not modal.");
		await Expect(Dialog.GetByRole(AriaRole.Button, new() { Name = button, Exact = true })).ToBeFocusedAsync();
	}

	[Then("no dialog is open")]
	public async Task ThenNoDialogAsync() => await Expect(Dialog).ToHaveCountAsync(0);

	[Then("the mirror gets the file {string} after the sync")]
	public async Task ThenTheMirrorGetsAsync(string path) => await WaitMirrorAsync(path, present: true);

	[Then("the mirror no longer has the file {string}")]
	public async Task ThenTheMirrorLostAsync(string path) => await WaitMirrorAsync(path, present: false);

	[Then("the file {string} in the folder holds {string}")]
	public async Task ThenTheFileHoldsAsync(string name, string text) =>
		Xunit.Assert.Equal(text, await page.EvaluateAsync<string?>(ReadFile, new object[] { _folder, name }));

	[Then("the file {string} is not in the folder")]
	public async Task ThenTheFileIsNotThereAsync(string name) =>
		Xunit.Assert.Null(await page.EvaluateAsync<string?>(ReadFile, new object[] { _folder, name }));

	/// <summary>When E2E_SCREENSHOT_DIR is set: the page at 1280x720 with the menu (or the delete dialog) open, in the dark and the light theme.</summary>
	[Then("I save tree screenshots named {string}")]
	public async Task ThenISaveScreenshotsAsync(string name)
	{
		var directory = Environment.GetEnvironmentVariable(ScreenshotDirectory);
		await page.SetViewportSizeAsync(1280, 720);
		foreach (var theme in new[] { "dark", "light" })
		{
			for (var i = 0; i < 3 && !string.IsNullOrEmpty(directory) && await page.GetByTestId("shell").GetAttributeAsync("data-theme") != theme; i++)
			{
				await page.GetByTestId("theme-toggle").ClickAsync();
			}

			if (name == "menu")
			{
				await WhenIRightClickAsync("src");
				await Expect(MenuItem("New File")).ToBeFocusedAsync();
			}
			else
			{
				await WhenIRightClickAndChooseAsync("docs", "Delete");
				await Expect(Dialog).ToBeVisibleAsync();
			}

			await page.WaitForTimeoutAsync(300);
			if (!string.IsNullOrEmpty(directory))
			{
				Directory.CreateDirectory(directory);
				await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"tree-{name}-{theme}.png") });
			}

			await page.Keyboard.PressAsync("Escape");
			await Expect(name == "menu" ? Menu : Dialog).ToHaveCountAsync(0);
			if (string.IsNullOrEmpty(directory))
			{
				return;
			}
		}
	}

	/// <summary>Inside the dialog, or nowhere (the browser's own UI took it: the page behind is inert, so no element of it is focused).</summary>
	private async Task<bool> InsideDialogAsync() =>
		await page.EvaluateAsync<bool>("document.activeElement === null || document.activeElement === document.body || document.activeElement.closest('dialog') !== null");

	private ILocator Row(string path) => page.Locator($"[role=treeitem][title='{path}']");

	private ILocator MenuItem(string label) => Menu.Locator("[role=menuitem]").Filter(new() { Has = page.Locator($"text='{label}'") });

	private async Task RightClickEmptyAsync()
	{
		var tree = page.GetByTestId("tree");
		var box = await tree.BoundingBoxAsync() ?? throw new InvalidOperationException("The tree is not on screen.");
		await tree.ClickAsync(new() { Button = MouseButton.Right, Position = new() { X = box.Width / 2, Y = box.Height - 12 } });
		await Expect(Menu).ToBeVisibleAsync();
	}

	/// <summary>The mirror is the repo folder under the server's mirror root whose name carries this scenario's folder suffix.</summary>
	private async Task WaitMirrorAsync(string path, bool present)
	{
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (true)
		{
			var repo = Directory.Exists(BrowserHooks.MirrorRoot)
				? Directory.GetDirectories(BrowserHooks.MirrorRoot).FirstOrDefault(d => Path.GetFileName(d).Contains(_suffix, StringComparison.OrdinalIgnoreCase))
				: null;
			var exists = repo is not null && File.Exists(Path.Combine(repo, path.Replace('/', Path.DirectorySeparatorChar)));
			if (exists == present)
			{
				return;
			}

			Xunit.Assert.True(DateTime.UtcNow < deadline, $"The mirror {(present ? "never got" : "still has")} '{path}'.");
			await page.WaitForTimeoutAsync(250);
		}
	}

	private async Task ConnectAsync(bool copy, string? blocked)
	{
		_suffix = Guid.NewGuid().ToString("N")[..8];
		_folder = "e2e-tree-" + _suffix;
		const string Put = " const put = async (dir, name, data) => { const w = await (await dir.getFileHandle(name, { create: true })).createWritable(); await w.write(data); await w.close(); };";
		var move = copy
			? "for (const proto of [FileSystemHandle.prototype, FileSystemFileHandle.prototype, FileSystemDirectoryHandle.prototype]) { delete proto.move; }"
			: string.Empty;
		var block = blocked is null
			? string.Empty
			: "const createWritable = FileSystemFileHandle.prototype.createWritable;"
				+ $" FileSystemFileHandle.prototype.createWritable = function (...args) {{ return this.name === {System.Text.Json.JsonSerializer.Serialize(blocked)} ? Promise.reject(new Error('Disk full')) : createWritable.apply(this, args); }};";
		if (move.Length + block.Length > 0)
		{
			await page.AddInitScriptAsync(move + block);
		}

		await page.AddInitScriptAsync(
			"window.showDirectoryPicker = async () => {"
			+ $" const root = await (await navigator.storage.getDirectory()).getDirectoryHandle('{_folder}', {{ create: true }});"
			+ Put
			+ " const src = await root.getDirectoryHandle('src', { create: true });"
			+ " const docs = await root.getDirectoryHandle('docs', { create: true });"
			+ " await put(src, 'A.cs', 'class A;');"
			+ " await put(docs, 'guide.md', '# guide');"
			+ " await put(docs, 'more.md', '# more');"
			+ " await put(root, 'a.txt', 'alpha');"
			+ " await put(root, 'keep.txt', 'original');"
			+ " return root; };");
		await page.GotoAsync("/");
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
		await page.GetByTestId("open-folder").ClickAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(_folder);
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(new Regex("^Synced"));
		// Chromium moves files (not folders) in the origin-private file system; the copy mode removes move altogether.
		Xunit.Assert.Equal(!copy, await page.EvaluateAsync<bool>("'move' in FileSystemFileHandle.prototype"));
	}
}
