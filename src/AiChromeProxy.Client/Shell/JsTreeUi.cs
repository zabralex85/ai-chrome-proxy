using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Shell;

/// <summary>Thin interop wrapper over <c>Scripts/treeui.ts</c> (compiled to <c>wwwroot/js/treeui.js</c>); no logic of its own (checked by the E2E scenarios).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsTreeUi(IJSRuntime js) : IAsyncDisposable
{
	private IJSObjectReference? _module;

	/// <summary>Just under the element (the viewport's corner for none), and the viewport size.</summary>
	public async Task<TreeAnchor> AnchorAsync(ElementReference? element) =>
		await (await ModuleAsync()).InvokeAsync<TreeAnchor>("anchor", element);

	/// <summary>Focuses the input and selects its first <paramref name="stem"/> characters (all when negative).</summary>
	public async Task SelectNameAsync(ElementReference input, int stem) => await (await ModuleAsync()).InvokeVoidAsync("selectName", input, stem);

	/// <summary>Keeps the browser's own context menu from opening on Shift+F10 and the Menu key in the tree.</summary>
	public async Task GuardContextKeysAsync(ElementReference tree) => await (await ModuleAsync()).InvokeVoidAsync("guardContextKeys", tree);

	/// <summary>Opens the dialog element as a modal.</summary>
	public async Task ShowModalAsync(ElementReference dialog) => await (await ModuleAsync()).InvokeVoidAsync("showModal", dialog);

	public async ValueTask DisposeAsync()
	{
		if (_module is not null)
		{
			await _module.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/treeui.js");
}
