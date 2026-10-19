using System.Diagnostics.CodeAnalysis;
using AiChromeProxy.Client.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Packages;

/// <summary>Thin interop wrapper over <c>Scripts/pkgaccess.ts</c> (compiled to <c>wwwroot/js</c>); no logic of its own (checked by the E2E scenarios and the manual checklist).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsPackageFolder(IJSRuntime js) : IPackageFolder, IAsyncDisposable
{
	private IJSObjectReference? _module;

	public async Task<string?> PickAsync() => await (await ModuleAsync()).InvokeAsync<string?>("pick");

	public async Task<FolderGrant?> RestoreAsync() => await (await ModuleAsync()).InvokeAsync<FolderGrant?>("restore");

	public async Task<bool> RequestAccessAsync() => await (await ModuleAsync()).InvokeAsync<bool>("requestAccess");

	public async Task ForgetAsync() => await (await ModuleAsync()).InvokeVoidAsync("forget");

	public async Task<string?> ReadTextAsync(string path) => await (await ModuleAsync()).InvokeAsync<string?>("readText", path);

	public async Task<IReadOnlyList<FileMeta>?> ListAsync(string folder) =>
		await (await ModuleAsync()).InvokeAsync<FileMeta[]?>("list", folder);

	public async Task<byte[]> ReadChunkAsync(string path, long offset, int length) =>
		await (await ModuleAsync()).InvokeAsync<byte[]>("readChunk", path, offset, length);

	public async ValueTask DisposeAsync()
	{
		if (_module is not null)
		{
			await _module.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ModuleAsync() => _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/pkgaccess.js");
}
