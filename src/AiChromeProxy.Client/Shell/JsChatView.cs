using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Shell;

/// <summary>Thin interop wrapper over <c>Scripts/diagrams.ts</c> and <c>Scripts/chat.ts</c> (compiled to <c>wwwroot/js</c>); no logic of its own (checked by the E2E scenarios and the manual checklist).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsChatView(IJSRuntime js) : IAsyncDisposable
{
	private IJSObjectReference? _diagrams;
	private IJSObjectReference? _chat;

	/// <summary>Draws the mermaid diagrams under the element that were not drawn yet.</summary>
	/// <param name="container">The chat log.</param>
	public async Task RenderDiagramsAsync(ElementReference container) =>
		await (_diagrams ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/diagrams.js")).InvokeVoidAsync("render", container);

	/// <summary>Scrolls the log to its end when the user is at the end.</summary>
	/// <param name="log">The chat log.</param>
	public async Task ScrollToEndAsync(ElementReference log) =>
		await (await ChatAsync()).InvokeVoidAsync("scrollToEnd", log);

	/// <summary>Makes Enter in the textarea call <paramref name="submit"/> with its text (Shift+Enter keeps its line break).</summary>
	/// <param name="textarea">The message box.</param>
	/// <param name="submit">What to do with the text.</param>
	/// <returns>The reference to dispose with the box.</returns>
	public async Task<IDisposable> SendOnEnterAsync(ElementReference textarea, Func<string, Task> submit)
	{
		var callback = DotNetObjectReference.Create(new SendCallback(submit));
		await (await ChatAsync()).InvokeVoidAsync("sendOnEnter", textarea, callback);
		return callback;
	}

	/// <summary>Makes a click on a <c>path:line</c> link in the log call <paramref name="open"/> with its <c>#open=path:line</c> address.</summary>
	/// <param name="log">The chat log.</param>
	/// <param name="open">What to do with the address.</param>
	/// <returns>The reference to dispose with the log.</returns>
	public async Task<IDisposable> OpenLinksAsync(ElementReference log, Func<string, Task> open)
	{
		var callback = DotNetObjectReference.Create(new OpenCallback(open));
		await (await ChatAsync()).InvokeVoidAsync("openLinks", log, callback);
		return callback;
	}

	/// <summary>Puts the text cursor in the element.</summary>
	/// <param name="element">The message box.</param>
	public async Task FocusAsync(ElementReference element) =>
		await (await ChatAsync()).InvokeVoidAsync("focus", element);

	public async ValueTask DisposeAsync()
	{
		if (_diagrams is not null)
		{
			await _diagrams.DisposeAsync();
		}

		if (_chat is not null)
		{
			await _chat.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ChatAsync() => _chat ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/chat.js");

	/// <summary>Target of <c>callback.invokeMethodAsync('Open', href)</c>.</summary>
	[ExcludeFromCodeCoverage]
	public sealed class OpenCallback(Func<string, Task> open)
	{
		[JSInvokable]
		public Task Open(string href) => open(href);
	}

	/// <summary>Target of <c>callback.invokeMethodAsync('Submit', text)</c>.</summary>
	[ExcludeFromCodeCoverage]
	public sealed class SendCallback(Func<string, Task> submit)
	{
		[JSInvokable]
		public Task Submit(string text) => submit(text);
	}
}
