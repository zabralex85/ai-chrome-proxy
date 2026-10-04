using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Selectors are the data-testid attributes of Shell/FileView.razor and the Monaco classes; the folder is the page's origin-private file system.</summary>
[Binding]
public sealed class NavigatorSteps(IPage page)
{
	/// <summary>Set it to a folder to get the screenshots of the "I save navigator screenshots named" steps.</summary>
	private const string ScreenshotDirectory = "E2E_SCREENSHOT_DIR";

	private const string Code = "namespace Demo;\n\n// line three\n\npublic sealed class Foo\n{\n\tpublic int Bar() => 42;\n}\n";

	private string _folder = string.Empty;

	private ILocator Editor => page.Locator("[data-testid=file-view]:not([hidden]) .monaco-editor");

	[Given("the app is connected with a folder holding the sample files")]
	public async Task GivenTheAppIsConnectedWithAFolderAsync() => await ConnectAsync(11);

	[When("I open the text files f01 to f{int} from the file tree")]
	public async Task WhenIOpenTheTextFilesAsync(int last)
	{
		for (var i = 1; i <= last; i++)
		{
			var name = $"f{i:00}.txt";
			await page.Locator($"[role=treeitem][title='{name}']").ClickAsync();
			await Expect(page.Locator("[data-testid=file-view]:not([hidden]) .view-lines")).ToContainTextAsync($"file f{i:00}");
		}
	}

	[When("I close the tab of {string}")]
	public async Task WhenICloseTheTabAsync(string path) =>
		await page.Locator($"[data-testid=tab-file][data-path='{path}'] .tab-close").ClickAsync();

	[When("I change the folder")]
	public async Task WhenIChangeTheFolderAsync() => await page.GetByTestId("change-folder").First.ClickAsync();

	[When("the file {string} is deleted from the folder")]
	public async Task WhenTheFileIsDeletedAsync(string name)
	{
		await page.EvaluateAsync(
			"""
			async ([folder, name]) => {
				const dir = await (await navigator.storage.getDirectory()).getDirectoryHandle(folder);
				await dir.removeEntry(name);
				window.dispatchEvent(new Event('focus')); // wakes the sync scan
			}
			""",
			new object[] { _folder, name });
	}

	/// <summary>Counts Monaco's live editors and text models (the page's globalThis.monaco): equal to what is open, so nothing leaked.</summary>
	[Then("Monaco holds {int} editors and {int} text models")]
	public async Task ThenMonacoHoldsAsync(int editors, int models)
	{
		const string Count = "() => globalThis.monaco ? [monaco.editor.getEditors().length, monaco.editor.getModels().length] : [0, 0]";
		await page.WaitForFunctionAsync("([e, m]) => { const [a, b] = (" + Count + ")(); return a === e && b === m; }", new object[] { editors, models });

		// A late disposal or a late open would show up after a moment: the count must hold.
		await page.WaitForTimeoutAsync(800);
		Xunit.Assert.Equal([editors, models], await page.EvaluateAsync<int[]>(Count));
	}

	[When("I click {string} and then {string} in the file tree")]
	public async Task WhenIClickInTheTreeAsync(string folder, string file)
	{
		await page.Locator($"[role=treeitem][title='{folder}']").ClickAsync();
		await page.Locator($"[role=treeitem][title='{folder}/{file}']").ClickAsync();
	}

	[When("I click {string} in the file tree")]
	public async Task WhenIClickInTheTreeAsync(string file) => await page.Locator($"[role=treeitem][title='{file}']").ClickAsync();

	[Then("the viewer shows the code of {string} with line numbers and highlighting")]
	public async Task ThenTheViewerShowsTheCodeAsync(string name)
	{
		await Expect(page.Locator("[data-testid=file-view]:not([hidden])")).ToContainTextAsync(name);
		await Expect(Editor).ToBeVisibleAsync();
		await Expect(Editor.Locator(".line-numbers").First).ToHaveTextAsync("1");
		await Expect(Editor.Locator(".view-lines")).ToContainTextAsync("public sealed class Foo");

		// mtk1 is the default foreground: a keyword gets another token class from the language's Monarch grammar.
		await Expect(Editor.Locator(".view-line span[class^='mtk']:not(.mtk1)").First).ToBeVisibleAsync();
	}

	[Then("line {int} of the viewer is highlighted")]
	public async Task ThenLineOfTheViewerIsHighlightedAsync(int line)
	{
		await Expect(Editor.Locator($".view-overlays > div:nth-child({line}) .viewer-revealed-line")).ToHaveCountAsync(1);
		await Expect(Editor.Locator(".viewer-revealed-line")).ToHaveCountAsync(1);
	}

	[Then("the viewer line {int} reads {string}")]
	public async Task ThenTheViewerLineReadsAsync(int line, string text)
	{
		// The lines are absolutely positioned in no fixed order: find the number's row through its top.
		var top = await Editor.Locator($".view-overlays > div:nth-child({line})").EvaluateAsync<string>("e => e.style.top");
		await Expect(Editor.Locator($".view-lines > div[style*='top:{top}']")).ToContainTextAsync(text);
	}

	[Then("the viewer shows {string}")]
	public async Task ThenTheViewerShowsTextAsync(string text) => await Expect(Editor.Locator(".view-lines")).ToContainTextAsync(text);

	[Then("the viewer says {string}")]
	public async Task ThenTheViewerSaysAsync(string text) => await Expect(page.Locator("[data-testid=file-view]:not([hidden]) [data-testid=symbol-note]")).ToHaveTextAsync(text);

	[Then("the file note says {string}")]
	public async Task ThenTheFileNoteSaysAsync(string text) => await Expect(page.Locator("[data-testid=file-view]:not([hidden]) [data-testid=file-note]")).ToHaveTextAsync(text);

	[Then("the viewer has no editor")]
	public async Task ThenTheViewerHasNoEditorAsync() => await Expect(page.Locator("[data-testid=file-view]:not([hidden]) .monaco-editor")).ToHaveCountAsync(0);

	[When("the file {string} changes in the folder")]
	public async Task WhenTheFileChangesAsync(string path)
	{
		var changed = Code.Replace("// line three", "// line three, edited", StringComparison.Ordinal);
		var segments = path.Split('/');
		await page.EvaluateAsync(
			"""
			async ([folder, segments, text]) => {
				let dir = await (await navigator.storage.getDirectory()).getDirectoryHandle(folder);
				for (const s of segments.slice(0, -1)) { dir = await dir.getDirectoryHandle(s); }
				const w = await (await dir.getFileHandle(segments[segments.length - 1])).createWritable();
				await w.write(text);
				await w.close();
				window.dispatchEvent(new Event('focus')); // wakes the sync scan instead of waiting for the next one
			}
			""",
			new object[] { _folder, segments, changed });
	}

	[Then("the viewer marks line {int} as changed and shows the new text")]
	public async Task ThenTheViewerMarksLineAsChangedAsync(int line)
	{
		// The marks last two seconds: look for them first.
		var changed = new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 };
		await Expect(Editor.Locator($".view-overlays > div:nth-child({line}) .viewer-changed-line")).ToHaveCountAsync(1, changed);
		await Expect(Editor.Locator(".view-lines")).ToContainTextAsync("line three, edited");
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the page at 1280x720 in the dark and the light theme, once the editor is drawn in it.</summary>
	[Then("I save navigator screenshots named {string}")]
	public async Task ThenISaveScreenshotsAsync(string name)
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

			await Expect(Editor).ToHaveClassAsync(new Regex(theme == "dark" ? @"\bvs-dark\b" : @"\bvs\b"));
			await page.WaitForTimeoutAsync(500);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"nav-{name}-{theme}.png") });
		}
	}

	private async Task ConnectAsync(int textFiles)
	{
		_folder = "e2e-nav-" + Guid.NewGuid().ToString("N")[..8];
		await page.AddInitScriptAsync(
			"window.showDirectoryPicker = async () => {"
			+ $" const root = await (await navigator.storage.getDirectory()).getDirectoryHandle('{_folder}', {{ create: true }});"
			+ " const put = async (dir, name, data) => { const w = await (await dir.getFileHandle(name, { create: true })).createWritable(); await w.write(data); await w.close(); };"
			+ " const src = await root.getDirectoryHandle('src', { create: true });"
			+ $" await put(src, 'A.cs', {System.Text.Json.JsonSerializer.Serialize(Code)});"
			+ " await put(root, 'notes.bin', new Uint8Array([1, 2, 0, 3, 255, 0, 7]));"
			+ " await put(root, 'README.md', '# e2e repo');"
			+ $" for (let i = 1; i <= {textFiles}; i++) {{ const n = String(i).padStart(2, '0'); await put(root, 'f' + n + '.txt', 'file f' + n); }}"
			+ " return root; };");
		await page.GotoAsync("/");
		await Expect(page.GetByTestId("connection-state")).ToHaveTextAsync("Connected");
		await page.GetByTestId("open-folder").ClickAsync();
		await Expect(page.GetByTestId("folder-name")).ToHaveTextAsync(_folder);
		await Expect(page.GetByTestId("status-sync")).ToHaveTextAsync(new Regex("^Synced"));
	}
}
