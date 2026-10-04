using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Navigator;

/// <summary>Thin interop wrapper over <c>Scripts/viewer.ts</c> (compiled to <c>wwwroot/js/viewer.js</c>); no logic of its own (checked by the E2E scenarios).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsFileViewer(IJSRuntime js) : IFileViewer, IAsyncDisposable
{
	private IJSObjectReference? _module;

	public async Task OpenAsync(ElementReference host, string path, string text, int? line) =>
		await (await ModuleAsync()).InvokeVoidAsync("open", host, path, text, line);

	public async Task UpdateAsync(ElementReference host, string text, IReadOnlyList<int> changedLines) =>
		await (await ModuleAsync()).InvokeVoidAsync("update", host, text, changedLines);

	public async Task RevealAsync(ElementReference host, int? line) => await (await ModuleAsync()).InvokeVoidAsync("reveal", host, line);

	public async Task SetThemeAsync(bool? dark) => await (await ModuleAsync()).InvokeVoidAsync("setTheme", dark);

	public async Task DisposeAsync(ElementReference host) => await (await ModuleAsync()).InvokeVoidAsync("dispose", host);

	public async ValueTask DisposeAsync()
	{
		if (_module is not null)
		{
			await _module.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/viewer.js");
}
