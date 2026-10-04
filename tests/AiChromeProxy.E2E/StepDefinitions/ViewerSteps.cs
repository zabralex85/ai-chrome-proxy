using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Drives <c>wwwroot/js/viewer.js</c> in the app page: a fixed-size host element, the module imported through the page's import map.</summary>
[Binding]
public sealed class ViewerSteps(IPage page)
{
	private const string Code = "namespace Sample;\n\npublic sealed class Greeter\n{\n\tpublic string Hello(string name) => $\"Hello, {name}\";\n}\n";

	private readonly List<string> _failed = [];

	[When("the viewer opens {string} with line {int} revealed")]
	public async Task WhenTheViewerOpensAsync(string path, int line)
	{
		page.Response += (_, response) =>
		{
			if (response.Url.Contains("/lib/monaco/", StringComparison.Ordinal) && response.Status >= 400)
			{
				_failed.Add($"{response.Status} {response.Url}");
			}
		};
		page.RequestFailed += (_, request) =>
		{
			if (request.Url.Contains("/lib/monaco/", StringComparison.Ordinal))
			{
				_failed.Add($"{request.Failure} {request.Url}");
			}
		};
		await page.EvaluateAsync(
			"""
			async ([path, text, line]) => {
				const host = document.createElement('div');
				host.id = 'viewer-host';
				host.style.cssText = 'position:fixed;left:0;top:0;width:800px;height:400px;z-index:1000';
				document.body.appendChild(host);
				const viewer = await import('./js/viewer.js');
				await viewer.open(host, 'viewer-test', path, text, line);
			}
			""",
			new object[] { path, Code, line });
	}

	[Then("the viewer shows the code with line numbers and syntax highlighting")]
	public async Task ThenTheViewerShowsTheCodeAsync()
	{
		var editor = page.Locator("#viewer-host .monaco-editor");
		await Expect(editor).ToBeVisibleAsync();
		await Expect(editor.Locator(".line-numbers").First).ToHaveTextAsync("1");
		await Expect(editor.Locator(".view-lines")).ToContainTextAsync("public sealed class Greeter");

		// mtk1 is the default foreground: a keyword gets another token class from the language's Monarch grammar.
		await Expect(editor.Locator(".view-line span[class^='mtk']:not(.mtk1)").First).ToBeVisibleAsync();
	}

	[Then("the viewer's language is {string}")]
	public async Task ThenTheLanguageIsAsync(string language)
	{
		// editor.main publishes its API as globalThis.monaco.
		Xunit.Assert.Equal(language, await page.EvaluateAsync<string>(
			"monaco.editor.getEditors().find(e => document.getElementById('viewer-host').contains(e.getContainerDomNode())).getModel().getLanguageId()"));
	}

	[Then("line {int} is highlighted")]
	public async Task ThenLineIsHighlightedAsync(int line)
	{
		// The overlays are one row per visible line, in order; the file is short, so line 1 is the first row.
		await Expect(page.Locator($"#viewer-host .view-overlays > div:nth-child({line}) .viewer-revealed-line")).ToHaveCountAsync(1);
		await Expect(page.Locator("#viewer-host .viewer-revealed-line")).ToHaveCountAsync(1);
	}

	[Then("the editor worker runs")]
	public async Task ThenTheEditorWorkerRunsAsync()
	{
		if (page.Workers.Count == 0)
		{
			await page.WaitForWorkerAsync();
		}
	}

	[Then("no request for Monaco's files failed")]
	public void ThenNoRequestFailed()
	{
		Xunit.Assert.True(_failed.Count == 0, "Failed Monaco requests:\n" + string.Join('\n', _failed));
	}
}
