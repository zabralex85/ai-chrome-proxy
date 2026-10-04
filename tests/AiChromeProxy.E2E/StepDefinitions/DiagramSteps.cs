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

	/// <summary>
	/// Two touch pointers on the viewport, 100 px apart around its centre; the second one moves 100 px further out (twice the distance, the
	/// midpoint 50 px to the right). Returns the stage's box before and after ([left, top, width, height] twice) and the centre.
	/// </summary>
	private const string Pinch =
		"""
		v => {
			const box = () => { const r = v.querySelector('.diagram-stage').getBoundingClientRect(); return [r.left, r.top, r.width, r.height]; };
			const r = v.getBoundingClientRect();
			const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
			const fire = (type, id, x) => v.dispatchEvent(new PointerEvent(type, {
				pointerId: id, pointerType: 'touch', isPrimary: id === 101, clientX: x, clientY: cy,
				button: type === 'pointermove' ? -1 : 0, buttons: type === 'pointerup' ? 0 : 1, bubbles: true, cancelable: true }));
			const before = box();
			fire('pointerdown', 101, cx - 50);
			fire('pointerdown', 102, cx + 50);
			fire('pointermove', 102, cx + 100);
			fire('pointermove', 102, cx + 150);
			fire('pointerup', 101, cx - 50);
			fire('pointerup', 102, cx + 150);
			return [...before, ...box(), cx, cy];
		}
		""";

	private int _fitted;
	private double[] _pinch = [];
	private double[] _stage = [];
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
		await ClickToolbarAsync("Save…");
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
		await ClickToolbarAsync("Save…");
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
	public async Task WhenIOpenTheDiagramAsync() => await ClickToolbarAsync("Open");

	[Then("the diagram viewer shows the fitted zoom level")]
	public async Task ThenTheDiagramViewerShowsTheFittedZoomLevelAsync()
	{
		await Expect(Viewer).ToBeVisibleAsync();
		await Expect(Viewer.Locator(".diagram-stage svg")).ToBeVisibleAsync();
		await Expect(Zoom).ToHaveTextAsync(new Regex(@"^\d+%$"));
		await Expect(Viewer.GetByRole(AriaRole.Heading)).ToHaveTextAsync(new Regex(@"^class-diagram-\d{8}-\d{4}$"));
		_fitted = await LevelAsync();
		Xunit.Assert.InRange(_fitted, 10, 100); // fitting never blows a small diagram up
	}

	[When("I pinch the diagram with two fingers to twice their distance")]
	public async Task WhenIPinchTheDiagramAsync() => _pinch = await Viewer.Locator(".diagram-viewport").EvaluateAsync<double[]>(Pinch);

	[Then("the zoom level is twice the fitted one and the point between the fingers followed them")]
	public async Task ThenTheZoomLevelIsTwiceTheFittedOneAsync()
	{
		Xunit.Assert.InRange(await LevelAsync(), (2 * _fitted) - 2, (2 * _fitted) + 2);
		var (left0, top0, width0, height0, left1, top1, width1, height1, cx, cy) =
			(_pinch[0], _pinch[1], _pinch[2], _pinch[3], _pinch[4], _pinch[5], _pinch[6], _pinch[7], _pinch[8], _pinch[9]);
		Xunit.Assert.InRange(width1 / width0, 1.99, 2.01);

		// The diagram's point under the old midpoint (cx, cy) is under the new one (cx + 50, cy).
		Xunit.Assert.InRange(left1 + ((cx - left0) * width1 / width0), cx + 49, cx + 51);
		Xunit.Assert.InRange(top1 + ((cy - top0) * height1 / height0), cy - 1, cy + 1);
	}

	[When("I drag the diagram with the mouse")]
	public async Task WhenIDragTheDiagramWithTheMouseAsync()
	{
		var stage = Viewer.Locator(".diagram-stage");
		var before = (await stage.BoundingBoxAsync())!;
		var box = (await Viewer.Locator(".diagram-viewport").BoundingBoxAsync())!;
		var (x, y) = (box.X + (box.Width / 2), box.Y + (box.Height / 2));
		await page.Mouse.MoveAsync(x, y);
		await page.Mouse.DownAsync();
		await page.Mouse.MoveAsync(x + 80, y + 40, new() { Steps = 8 });
		await page.Mouse.UpAsync();
		var after = (await stage.BoundingBoxAsync())!;
		_stage = [after.X - before.X, after.Y - before.Y];
	}

	[Then("the diagram moved with the mouse and no text is selected")]
	public async Task ThenTheDiagramMovedAsync()
	{
		Xunit.Assert.InRange(_stage[0], 79, 81);
		Xunit.Assert.InRange(_stage[1], 39, 41);
		Xunit.Assert.Equal(string.Empty, await page.EvaluateAsync<string>("() => window.getSelection().toString()"));
	}

	/// <summary>The PNG export's canvas gives no blob, as when the browser runs out of canvas memory.</summary>
	[When("the browser cannot make PNG files")]
	public async Task WhenTheBrowserCannotMakePngFilesAsync() =>
		await page.EvaluateAsync("() => { HTMLCanvasElement.prototype.toBlob = function (callback) { callback(null); }; }");

	[When("I choose {string} from the diagram's Download… menu")]
	public async Task WhenIChooseFromTheDownloadMenuAsync(string item)
	{
		await ClickToolbarAsync("Download…");
		await Toolbar.GetByRole(AriaRole.Menuitem, new() { Name = item, Exact = true }).ClickAsync();
	}

	[When("I choose {string} from the viewer's Download… menu")]
	public async Task WhenIChooseFromTheViewersDownloadMenuAsync(string item)
	{
		await Viewer.GetByRole(AriaRole.Button, new() { Name = "Download…" }).ClickAsync();
		await Viewer.GetByRole(AriaRole.Menuitem, new() { Name = item, Exact = true }).ClickAsync();
	}

	[Then("the diagram toolbar says {string}")]
	public async Task ThenTheDiagramToolbarSaysAsync(string text)
	{
		var problem = Toolbar.GetByRole(AriaRole.Alert);
		await Expect(problem).ToHaveTextAsync(text);
		await Expect(problem).ToBeVisibleAsync();
		await Expect(Toolbar).ToHaveCSSAsync("opacity", "1");
	}

	[Then("the diagram viewer says {string}")]
	public async Task ThenTheDiagramViewerSaysAsync(string text)
	{
		var problem = Viewer.Locator(".diagram-viewer-header").GetByRole(AriaRole.Alert);
		await Expect(problem).ToHaveTextAsync(text);
		await Expect(problem).ToBeVisibleAsync();
	}

	[Then("no failure is shown")]
	public async Task ThenNoFailureIsShownAsync() => await Expect(page.GetByTestId("diagram-problem")).ToHaveCountAsync(0);

	/// <summary>
	/// Without a folder there is no chat to ask, so the diagram is put into a new chat's log the way MarkdownRenderer writes it; the next
	/// render of the chat (a theme switch) draws it with the page's own callback and folder state.
	/// </summary>
	[When("a class diagram is drawn in a new chat without a folder")]
	public async Task WhenAClassDiagramIsDrawnWithoutAFolderAsync()
	{
		await page.GetByTestId("chat-menu-button").ClickAsync();
		await page.GetByTestId("new-chat").ClickAsync();
		await Expect(page.GetByTestId("chat-empty")).ToBeVisibleAsync();
		await page.GetByTestId("chat-log").EvaluateAsync(
			"""
			log => {
				const answer = document.createElement('div');
				answer.className = 'msg msg-assistant markdown';
				answer.setAttribute('data-testid', 'chat-assistant');
				const diagram = document.createElement('div');
				diagram.className = 'mermaid-source';
				diagram.dataset.diagram = 'classDiagram\n  class Animal\n  Animal <|-- Dog';
				answer.appendChild(diagram);
				log.appendChild(answer);
			}
			""");
		await page.GetByTestId("theme-toggle").ClickAsync();
		await Expect(Toolbar).ToBeAttachedAsync();
	}

	[Then("the diagram's Save… items are disabled with the tooltip {string}")]
	public async Task ThenTheSaveItemsAreDisabledAsync(string tooltip)
	{
		await ClickToolbarAsync("Save…");
		var items = Toolbar.GetByRole(AriaRole.Menuitem);
		await Expect(items).ToHaveTextAsync(["Source (.mmd)", "SVG", "PNG"]);
		for (var i = 0; i < 3; i++)
		{
			await Expect(items.Nth(i)).ToHaveAttributeAsync("aria-disabled", "true");
			await Expect(items.Nth(i)).ToHaveAttributeAsync("title", tooltip);
		}

		await items.First.ClickAsync(new() { Force = true }); // aria-disabled: Playwright would wait for it to be enabled
		await Expect(SaveDialog).ToHaveCountAsync(0);
		await page.Keyboard.PressAsync("Escape");
		await Expect(Toolbar.GetByRole(AriaRole.Menu)).ToBeHiddenAsync();
	}

	/// <summary>When E2E_SCREENSHOT_DIR is set: the page as it is now, as &lt;name&gt;.png.</summary>
	[Then("I save a screenshot named {string}")]
	public async Task ThenISaveAScreenshotNamedAsync(string name)
	{
		if (Environment.GetEnvironmentVariable(ScreenshotDirectory) is { Length: > 0 } directory)
		{
			Directory.CreateDirectory(directory);
			await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name + ".png") });
		}
	}

	[Then("the saved note's {string} has the focus")]
	public async Task ThenTheSavedNotesButtonHasTheFocusAsync(string name) =>
		await Expect(SavedNote.GetByRole(AriaRole.Button, new() { Name = name, Exact = true })).ToBeFocusedAsync();

	[Then("the saved note is gone and the message box has the focus")]
	public async Task ThenTheSavedNoteIsGoneAsync()
	{
		await Expect(SavedNote).ToHaveCountAsync(0);
		await Expect(page.GetByTestId("chat-input")).ToBeFocusedAsync();
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

	[Then("the zoom level is {string}")]
	public async Task ThenTheZoomLevelIsAsync(string level) => await Expect(Zoom).ToHaveTextAsync(level);

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
		await ClickToolbarAsync("Download…");
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
			await ClickToolbarAsync("Download…");
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

	/// <summary>The toolbar takes clicks only while the diagram is hovered or focused: the mouse goes over the diagram first, as a user's does.</summary>
	private async Task ClickToolbarAsync(string name)
	{
		await Diagram.HoverAsync();
		await Toolbar.GetByRole(AriaRole.Button, new() { Name = name }).ClickAsync();
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
