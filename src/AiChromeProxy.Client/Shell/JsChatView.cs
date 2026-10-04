using System.Diagnostics.CodeAnalysis;
using AiChromeProxy.Client.Chat;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Shell;

/// <summary>Thin interop wrapper over <c>Scripts/diagrams.ts</c> and <c>Scripts/chat.ts</c> (compiled to <c>wwwroot/js</c>); no logic of its own (checked by the E2E scenarios and the manual checklist).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsChatView(IJSRuntime js) : IAsyncDisposable
{
	private IJSObjectReference? _diagrams;
	private IJSObjectReference? _chat;

	/// <summary>Draws the mermaid diagrams under the element that were not drawn yet, each with its toolbar.</summary>
	/// <param name="container">The chat log.</param>
	/// <param name="callback">Receives the Save clicks and names the downloads; without one the Save menu is left out.</param>
	public async Task RenderDiagramsAsync(ElementReference container, DotNetObjectReference<DiagramCallback>? callback = null) =>
		await (await DiagramsAsync()).InvokeVoidAsync("render", container, callback);

	/// <summary>The diagram drawn again in the current theme as an SVG or PNG file (the source is saved as its UTF-8 text, without JS).</summary>
	/// <param name="source">The mermaid source.</param>
	/// <param name="kind"><see cref="DiagramFileKind.Svg"/> or <see cref="DiagramFileKind.Png"/>.</param>
	/// <returns>The file's bytes.</returns>
	public async Task<byte[]> ExportDiagramAsync(string source, DiagramFileKind kind) =>
		await (await DiagramsAsync()).InvokeAsync<byte[]>(
			kind switch
			{
				DiagramFileKind.Svg => "exportSvg",
				DiagramFileKind.Png => "exportPng",
				_ => throw new ArgumentException("Only SVG and PNG are drawn; the source is its own UTF-8 text.", nameof(kind)),
			},
			source);

	/// <summary>Enables the diagrams' Save items while a folder is open (else they say "Pick a folder first").</summary>
	/// <param name="open">A folder is open.</param>
	public async Task SetFolderOpenAsync(bool open) =>
		await (await DiagramsAsync()).InvokeVoidAsync("setFolderOpen", open);

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

	private async Task<IJSObjectReference> DiagramsAsync() => _diagrams ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/diagrams.js");

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
