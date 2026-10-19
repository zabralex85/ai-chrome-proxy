using AiChromeProxy.E2E.Hooks;
using Microsoft.Playwright;
using Reqnroll;
using static Microsoft.Playwright.Assertions;

namespace AiChromeProxy.E2E.StepDefinitions;

/// <summary>Attachments in the chat box: a paste and a drop dispatched with a real <c>DataTransfer</c> (the OS clipboard and drag cannot be automated).</summary>
[Binding]
public sealed class AttachmentSteps(IPage page)
{
	private const string PastePicture =
		"""
		async () => {
			const canvas = document.createElement('canvas');
			canvas.width = 40; canvas.height = 30;
			const ctx = canvas.getContext('2d');
			ctx.fillStyle = '#c33'; ctx.fillRect(0, 0, 40, 30);
			const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
			const data = new DataTransfer();
			data.items.add(new File([blob], 'image.png', { type: 'image/png' }));
			document.querySelector('[data-testid=chat-input]').dispatchEvent(new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true }));
		}
		""";

	// Office puts text and a picture on the clipboard: the text must win, so the listener leaves the event alone (not default-prevented).
	private const string PasteTextAndPicture =
		"""
		async () => {
			const canvas = document.createElement('canvas');
			canvas.width = 40; canvas.height = 30;
			const blob = await new Promise(resolve => canvas.toBlob(resolve, 'image/png'));
			const data = new DataTransfer();
			data.setData('text/plain', 'hello from excel');
			data.items.add(new File([blob], 'image.png', { type: 'image/png' }));
			const event = new ClipboardEvent('paste', { clipboardData: data, bubbles: true, cancelable: true });
			document.querySelector('[data-testid=chat-input]').dispatchEvent(event);
			return !event.defaultPrevented;
		}
		""";

	private const string DropFile =
		"""
		name => {
			const data = new DataTransfer();
			data.items.add(new File(['hello notes'], name, { type: 'text/plain' }));
			const zone = document.querySelector('[data-testid=chat-compose]');
			for (const type of ['dragover', 'drop']) {
				zone.dispatchEvent(new DragEvent(type, { dataTransfer: data, bubbles: true, cancelable: true }));
			}
		}
		""";

	private bool _pasteLeftAlone;

	[When("I paste a picture into the chat box")]
	public async Task WhenIPasteAPictureAsync() => await page.EvaluateAsync(PastePicture);

	[When("I paste text together with a picture into the chat box")]
	public async Task WhenIPasteTextAndPictureAsync() => _pasteLeftAlone = await page.EvaluateAsync<bool>(PasteTextAndPicture);

	[Then("the paste was left to the browser")]
	public void ThenThePasteWasLeftToTheBrowser() => Xunit.Assert.True(_pasteLeftAlone);

	[When("I drop a file {string} on the chat box")]
	public async Task WhenIDropAFileAsync(string name) => await page.EvaluateAsync(DropFile, name);

	[Then("the chat box shows {int} attachments")]
	public async Task ThenTheChatBoxShowsAttachmentsAsync(int count)
	{
		await Expect(page.GetByTestId("chat-attachment")).ToHaveCountAsync(count);
		if (count > 0)
		{
			await Expect(page.Locator("[data-testid=chat-attachment].ready")).ToHaveCountAsync(count);
		}
	}

	[Then("the message lists the attachments {string} and {string}")]
	public async Task ThenTheMessageListsTheAttachmentsAsync(string first, string second)
	{
		var chips = page.GetByTestId("chat-user-attachment");
		await Expect(chips).ToHaveCountAsync(2);
		await Expect(chips.Nth(0)).ToContainTextAsync(first);
		await Expect(chips.Nth(1)).ToContainTextAsync(second);
	}

	[Then("the run could read the attachments folder, which holds {string}")]
	public async Task ThenTheRunCouldReadTheAttachmentsFolderAsync(string name)
	{
		var repo = (await page.GetByTestId("folder-name").TextContentAsync())!;
		var folder = Path.Combine(BrowserHooks.AttachmentsRoot, repo);
		var args = await File.ReadAllLinesAsync(Path.Combine(BrowserHooks.ArgsDirectory, repo + ".args"));

		Xunit.Assert.Equal(folder, args[Array.IndexOf(args, "--add-dir") + 1]);
		Xunit.Assert.Single(Directory.GetFiles(folder, name, SearchOption.AllDirectories));
	}
}
