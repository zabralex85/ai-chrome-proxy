using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>The toolbar, menus and viewer are built by Scripts/diagrams.ts (roles and data-testid attributes set there).</summary>
[Binding]
public sealed class DiagramSteps(IPage page)
{
	/// <summary>Set it to a folder to get the screenshots and the downloaded files (for a visual check; nothing is saved otherwise).</summary>
	private const string ScreenshotDirectory = "E2E_SCREENSHOT_DIR";

	/// <summary>A file in the picked folder (the page's origin-private file system) as its bytes, or null when it is missing.</summary>
	private const string ReadFile =
		"""
		async ([folder, path]) => {
			const parts = path.split('/');
			try {
				let dir = await (await navigator.storage.getDirectory()).getDirectoryHandle(folder);
				for (const part of parts.slice(0, -1)) { dir = await dir.getDirectoryHandle(part); }
				const file = await (await dir.getFileHandle(parts[parts.length - 1])).getFile();
				return Array.from(new Uint8Array(await file.arrayBuffer()));
			} catch (e) { return null; }
		}
		""";

	private int _fitted;
	private IDownload? _download;
	private string _saved = string.Empty;

	private ILocator Diagram => page.Locator("[data-testid=chat-assistant] .mermaid-source").First;

	private ILocator Toolbar => Diagram.GetByTestId("diagram-toolbar");

	private ILocator Viewer => page.GetByTestId("diagram-viewer");

	private ILocator Zoom => page.GetByTestId("diagram-zoom");

	private ILocator SaveDialog => page.GetByTestId("save-diagram-dialog");

	private ILocator SavePath => page.GetByTestId("save-diagram-path");

	private ILocator SavedNote => page.GetByTestId("diagram-saved");

	[When("I choose {string} from the diagram's Save… menu")]
	public async Task WhenIChooseFromTheSaveMenuAsync(string item)
	{
		await Toolbar.GetByRole(AriaRole.Button, new() { Name = "Save…" }).ClickAsync();
		await Toolbar.GetByRole(AriaRole.Menuitem, new() { Name = item, Exact = true }).ClickAsync();
		await Expect(SaveDialog).ToBeVisibleAsync();
	}

	/// <summary>The path's "&lt;yyyyMMdd-HHmm&gt;" stands for the local time of the save.</summary>
	[Then("the save dialog {string} offers {string} with the name selected")]
	public async Task ThenTheSaveDialogOffersAsync(string title, string path)
	{
		Xunit.Assert.True(await SaveDialog.EvaluateAsync<bool>("d => d.matches(':modal')"), "The save dialog is not modal.");
		await Expect(SaveDialog.GetByRole(AriaRole.Heading)).ToHaveTextAsync(title);
		var pattern = "^" + Regex.Escape(path).Replace("<yyyyMMdd-HHmm>", @"\d{8}-\d{4}", StringComparison.Ordinal) + "$";
		await Expect(SavePath).ToHaveValueAsync(new Regex(pattern));
		await Expect(SavePath).ToBeFocusedAsync();
		var value = await SavePath.InputValueAsync();
		var selection = await SavePath.EvaluateAsync<int[]>("i => [i.selectionStart, i.selectionEnd]");
		Xunit.Assert.Equal(new[] { value.LastIndexOf('/') + 1, value.LastIndexOf('.') }, selection);
	}

	[When("I click Save in the save dialog")]
	public async Task WhenIClickSaveInTheSaveDialogAsync()
	{
		_saved = await SavePath.InputValueAsync();
		await SaveDialog.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
	}

	[When("I type the path {string} in the save dialog and press Enter")]
	public async Task WhenITypeThePathAsync(string path)
	{
		await SavePath.FillAsync(path);
		await SavePath.PressAsync("Enter");
	}

	[Then("no save dialog is open")]
	public async Task ThenNoSaveDialogIsOpenAsync() => await Expect(SaveDialog).ToHaveCountAsync(0);

	[Then("the chat says the diagram was saved with an Open link")]
	public async Task ThenSavedWithOpenAsync() => await ThenTheChatSaysAsync("Saved to " + _saved, open: true);

	[Then("the chat says the diagram was saved without an Open link")]
	public async Task ThenSavedWithoutOpenAsync() => await ThenTheChatSaysAsync("Saved to " + _saved, open: false);

	[Then("the chat says {string} with an Open link")]
	public async Task ThenTheChatSaysWithOpenAsync(string text)
	{
		_saved = text["Saved to ".Length..];
		await ThenTheChatSaysAsync(text, open: true);
	}

	[Then("the saved file in the folder holds the diagram source")]
	public async Task ThenTheSavedFileHoldsTheSourceAsync() =>
		Xunit.Assert.Equal(await Diagram.GetAttributeAsync("data-diagram"), Encoding.UTF8.GetString(await SavedBytesAsync()));

	[Then("the saved file in the folder starts with the PNG signature")]
	public async Task ThenTheSavedFileIsAPngAsync() =>
		Xunit.Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, (await SavedBytesAsync()).Take(8).ToArray());

	[Then("the saved file in the folder starts with the text {string}")]
	public async Task ThenTheSavedFileStartsWithAsync(string start) =>
		Xunit.Assert.StartsWith(start, Encoding.UTF8.GetString(await SavedBytesAsync()), StringComparison.Ordinal);

	[Then("the mirror gets the saved file after the sync")]
	public async Task ThenTheMirrorGetsTheSavedFileAsync()
	{
		var folder = (await page.GetByTestId("folder-name").TextContentAsync())!;
		var suffix = folder[(folder.LastIndexOf('-') + 1)..];
		var deadline = DateTime.UtcNow.AddSeconds(30);
		while (true)
		{
			var repo = Directory.Exists(BrowserHooks.MirrorRoot)
				? Directory.GetDirectories(BrowserHooks.MirrorRoot).FirstOrDefault(d => Path.GetFileName(d).Contains(suffix, StringComparison.OrdinalIgnoreCase))
				: null;
			var file = repo is null ? null : Path.Combine(repo, _saved.Replace('/', Path.DirectorySeparatorChar));
			if (file is not null && File.Exists(file))
			{
				Xunit.Assert.Equal(await Diagram.GetAttributeAsync("data-diagram"), await File.ReadAllTextAsync(file));
				return;
			}

			Xunit.Assert.True(DateTime.UtcNow < deadline, $"The mirror never got '{_saved}'.");
			await page.WaitForTimeoutAsync(250);
		}
	}

	[Then("the save dialog says the saved file already exists here")]
	public async Task ThenTheSaveDialogSaysExistsAsync()
	{
		await Expect(page.GetByTestId("save-diagram-error")).ToHaveTextAsync($"'{_saved[(_saved.LastIndexOf('/') + 1)..]}' already exists here.");
		await Expect(SavePath).ToHaveValueAsync(_saved);
		await Expect(SavePath).ToBeFocusedAsync();
	}

	[When("I click Open in the saved note")]
	public async Task WhenIClickOpenInTheSavedNoteAsync() => await SavedNote.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true }).ClickAsync();

	[Then("the saved file's tab is active with the viewer showing {string}")]
	public async Task ThenTheSavedFilesTabIsActiveAsync(string text)
	{
		await Expect(page.Locator("[data-testid=tab-file][aria-selected=true]")).ToHaveAttributeAsync("data-path", _saved);
		await Expect(page.Locator("[data-testid=file-view]:not([hidden]) .view-lines")).ToContainTextAsync(text);
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the save dialog (opened from Save… &gt; Source) in the dark and the light theme; it is closed again.</summary>
	[Then("I save screenshots of the save dialog")]
	public async Task ThenISaveScreenshotsOfTheSaveDialogAsync()
	{
		if (Environment.GetEnvironmentVariable(ScreenshotDirectory) is not { Length: > 0 } directory)
		{
			return;
		}

		Directory.CreateDirectory(directory);
		await page.SetViewportSizeAsync(1280, 720);
		foreach (var theme in new[] { "dark", "light" })
		{
			await ThemeAsync(theme);
			await WhenIChooseFromTheSaveMenuAsync("Source (.mmd)");
			await page.WaitForTimeoutAsync(200);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-save-dialog-{theme}.png") });
			await page.Keyboard.PressAsync("Escape");
			await Expect(SaveDialog).ToHaveCountAsync(0);
		}
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the chat with the "Saved to" note in the dark and the light theme.</summary>
	[Then("I save screenshots of the saved note")]
	public async Task ThenISaveScreenshotsOfTheSavedNoteAsync()
	{
		if (Environment.GetEnvironmentVariable(ScreenshotDirectory) is not { Length: > 0 } directory)
		{
			return;
		}

		foreach (var theme in new[] { "dark", "light" })
		{
			await ThemeAsync(theme);
			await SavedNote.ScrollIntoViewIfNeededAsync();
			await page.WaitForTimeoutAsync(200);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-save-note-{theme}.png") });
		}
	}

	[Then("the diagram toolbar offers Open, Save… and Download…")]
	public async Task ThenTheDiagramToolbarOffersAsync()
	{
		await Expect(Toolbar).ToHaveAttributeAsync("role", "toolbar");
		await Expect(Toolbar.GetByRole(AriaRole.Button)).ToHaveTextAsync(["Open", "Save…", "Download…"]);
	}

	[Then("the Save… menu lists {string} and closes with Escape")]
	public async Task ThenTheSaveMenuListsAsync(string items)
	{
		var save = Toolbar.GetByRole(AriaRole.Button, new() { Name = "Save…" });
		await save.ClickAsync();
		await Expect(save).ToHaveAttributeAsync("aria-expanded", "true");
		var entries = Toolbar.GetByRole(AriaRole.Menuitem);
		await Expect(entries).ToHaveTextAsync(items.Split(", "));
		await Expect(entries.First).ToBeFocusedAsync();
		await Expect(Toolbar.Locator("[role=menuitem][aria-disabled]")).ToHaveCountAsync(0); // a folder is open
		await page.Keyboard.PressAsync("ArrowDown");
		await Expect(entries.Nth(1)).ToBeFocusedAsync();
		await page.Keyboard.PressAsync("Escape");
		await Expect(Toolbar.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
		await Expect(save).ToBeFocusedAsync();
	}

	[When("I open the diagram")]
	public async Task WhenIOpenTheDiagramAsync() => await Toolbar.GetByRole(AriaRole.Button, new() { Name = "Open" }).ClickAsync();

	[Then("the diagram viewer shows the fitted zoom level")]
	public async Task ThenTheDiagramViewerShowsTheFittedZoomLevelAsync()
	{
		await Expect(Viewer).ToBeVisibleAsync();
		await Expect(Viewer.Locator(".diagram-stage svg")).ToBeVisibleAsync();
		await Expect(Zoom).ToHaveTextAsync(new Regex(@"^\d+%$"));
		await Expect(Viewer.GetByRole(AriaRole.Heading)).ToHaveTextAsync(new Regex(@"^class-diagram-\d{8}-\d{4}$"));
		_fitted = await LevelAsync();
	}

	[When("I press {string} in the diagram viewer")]
	public async Task WhenIPressInTheDiagramViewerAsync(string key)
	{
		if (key.Length == 1)
		{
			await page.Keyboard.TypeAsync(key);
		}
		else
		{
			await page.Keyboard.PressAsync(key);
		}
	}

	[Then("the zoom level has grown")]
	public async Task ThenTheZoomLevelHasGrownAsync()
	{
		await Expect(Zoom).Not.ToHaveTextAsync(_fitted + "%");
		Xunit.Assert.True(await LevelAsync() > _fitted, $"Zoom {await Zoom.TextContentAsync()} is not above the fitted {_fitted}%.");
	}

	[When("I click {string} in the diagram viewer")]
	public async Task WhenIClickInTheDiagramViewerAsync(string name) =>
		await Viewer.GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();

	[Then("the zoom level is the fitted one again")]
	public async Task ThenTheZoomLevelIsTheFittedOneAgainAsync() => await Expect(Zoom).ToHaveTextAsync(_fitted + "%");

	[Then("the diagram viewer is closed and Open has the focus")]
	public async Task ThenTheDiagramViewerIsClosedAsync()
	{
		await Expect(Viewer).ToBeHiddenAsync();
		await Expect(Toolbar.GetByRole(AriaRole.Button, new() { Name = "Open" })).ToBeFocusedAsync();
	}

	[When("I download the diagram as {string}")]
	public async Task WhenIDownloadTheDiagramAsAsync(string kind)
	{
		await Toolbar.GetByRole(AriaRole.Button, new() { Name = "Download…" }).ClickAsync();
		_download = await page.RunAndWaitForDownloadAsync(() => Toolbar.GetByRole(AriaRole.Menuitem, new() { Name = kind, Exact = true }).ClickAsync());
	}

	[Then("the download is named {string} and starts with the text {string}")]
	public async Task ThenTheDownloadStartsWithTheTextAsync(string pattern, string start)
	{
		var bytes = await DownloadedAsync(pattern);
		Xunit.Assert.StartsWith(start, Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
	}

	[Then("the download is named {string} and starts with the PNG signature")]
	public async Task ThenTheDownloadStartsWithThePngSignatureAsync(string pattern)
	{
		var bytes = await DownloadedAsync(pattern);
		Xunit.Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, bytes.Take(8).ToArray());
	}

	/// <summary>The PNG export draws with SVG text labels; the chat's own drawings keep mermaid's HTML labels (foreignObject) afterwards.</summary>
	[Then("the diagram drawn again in the other theme still has HTML labels")]
	public async Task ThenTheDiagramDrawnAgainStillHasHtmlLabelsAsync()
	{
		// The toggle steps through system, dark and light: one of the next two clicks changes the drawn theme.
		var before = await Diagram.GetAttributeAsync("data-rendered");
		for (var i = 0; i < 2 && await page.GetByTestId("shell").GetAttributeAsync("data-theme") is var chosen && (chosen ?? string.Empty) != (before == "dark" ? "light" : "dark"); i++)
		{
			await page.GetByTestId("theme-toggle").ClickAsync();
		}

		await Expect(Diagram).Not.ToHaveAttributeAsync("data-rendered", before!);
		await Expect(Diagram.Locator(".diagram-canvas svg")).ToBeVisibleAsync();
		await Expect(Diagram.Locator("foreignObject").First).ToBeAttachedAsync();
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the diagram with its toolbar, with the Download… menu open, and the open viewer (with its menu), in the dark and the light theme.</summary>
	[Then("I save screenshots of the diagram tools")]
	public async Task ThenISaveScreenshotsOfTheDiagramToolsAsync()
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
			await ThemeAsync(theme);
			await Diagram.EvaluateAsync("e => e.scrollIntoView({ block: 'start' })");
			await Diagram.HoverAsync();
			await page.WaitForTimeoutAsync(400);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-toolbar-{theme}.png") });
			await Toolbar.GetByRole(AriaRole.Button, new() { Name = "Download…" }).ClickAsync();
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-menu-{theme}.png") });
			await page.Keyboard.PressAsync("Escape");
			await WhenIOpenTheDiagramAsync();
			await Expect(Viewer.Locator(".diagram-stage svg")).ToBeVisibleAsync();
			await page.WaitForTimeoutAsync(200);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-viewer-{theme}.png") });
			await Viewer.GetByRole(AriaRole.Button, new() { Name = "Download…" }).ClickAsync();
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, $"diagram-viewer-menu-{theme}.png") });
			await page.Keyboard.PressAsync("Escape");
			await page.Keyboard.PressAsync("Escape");
			await Expect(Viewer).ToBeHiddenAsync();
		}
	}

	private async Task ThenTheChatSaysAsync(string text, bool open)
	{
		await Expect(SavedNote.GetByTestId("diagram-saved-text")).ToHaveTextAsync(text);
		await Expect(SavedNote.GetByRole(AriaRole.Button, new() { Name = "Open", Exact = true })).ToHaveCountAsync(open ? 1 : 0);
	}

	private async Task<byte[]> SavedBytesAsync()
	{
		var folder = await page.GetByTestId("folder-name").TextContentAsync();
		var bytes = await page.EvaluateAsync<int[]?>(ReadFile, new object[] { folder!, _saved });
		Xunit.Assert.NotNull(bytes);
		return [.. bytes.Select(b => (byte)b)];
	}

	/// <summary>The toggle steps through system, dark and light; the chat's diagrams are drawn again in the new theme.</summary>
	private async Task ThemeAsync(string theme)
	{
		for (var i = 0; i < 3 && await page.GetByTestId("shell").GetAttributeAsync("data-theme") != theme; i++)
		{
			await page.GetByTestId("theme-toggle").ClickAsync();
		}

		await page.WaitForFunctionAsync(
			"mode => Array.from(document.querySelectorAll('.mermaid-source')).every(e => e.getAttribute('data-rendered') === mode && e.querySelector('svg') !== null)",
			theme == "dark" ? "dark" : "default");
	}

	private async Task<int> LevelAsync() => int.Parse((await Zoom.TextContentAsync())!.TrimEnd('%'), CultureInfo.InvariantCulture);

	private async Task<byte[]> DownloadedAsync(string pattern)
	{
		var name = _download!.SuggestedFilename;
		var regex = "^" + Regex.Escape(pattern).Replace(@"\*", ".+", StringComparison.Ordinal) + "$";
		Xunit.Assert.Matches(regex, name);
		if (Environment.GetEnvironmentVariable(ScreenshotDirectory) is { Length: > 0 } directory)
		{
			// The file itself, for a look at the exported picture.
			await _download.SaveAsAsync(Path.Combine(directory, "diagram-download" + Path.GetExtension(name)));
		}

		return await File.ReadAllBytesAsync((await _download.PathAsync())!);
	}
}
