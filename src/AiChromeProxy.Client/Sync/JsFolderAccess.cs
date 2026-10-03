using System.Diagnostics.CodeAnalysis;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

/// <summary>Thin interop wrapper over <c>wwwroot/js/fsaccess.js</c>; no logic of its own (checked manually, see docs/sync.md).</summary>
[ExcludeFromCodeCoverage]
public sealed class JsFolderAccess(IJSRuntime js) : IFolderAccess, IAsyncDisposable
{
	private IJSObjectReference? _module;
	private DotNetObjectReference<VisibilityCallback>? _callback;

	public async Task<string?> PickAsync() => await (await ModuleAsync()).InvokeAsync<string?>("pick");

	public async Task<FolderGrant?> RestoreAsync() => await (await ModuleAsync()).InvokeAsync<FolderGrant?>("restore");

	public async Task<bool> RequestAccessAsync() => await (await ModuleAsync()).InvokeAsync<bool>("requestAccess");

	public async Task<FolderScan> ScanAsync(IReadOnlyList<string> skipDirectories, int maxEntries) =>
		await (await ModuleAsync()).InvokeAsync<FolderScan>("scan", skipDirectories, maxEntries);

	public async Task<IReadOnlyList<string?>> HashAsync(IReadOnlyList<string> paths) =>
		await (await ModuleAsync()).InvokeAsync<string?[]>("hash", paths);

	public async Task<string?> ReadTextAsync(string path) => await (await ModuleAsync()).InvokeAsync<string?>("readText", path);

	public async Task<byte[]> ReadChunkAsync(string path, long offset, int length) =>
		await (await ModuleAsync()).InvokeAsync<byte[]>("readChunk", path, offset, length);

	public async Task WatchVisibilityAsync(Action<bool> changed)
	{
		_callback = DotNetObjectReference.Create(new VisibilityCallback(changed));
		await (await ModuleAsync()).InvokeVoidAsync("watchVisibility", _callback);
	}

	public async ValueTask DisposeAsync()
	{
		_callback?.Dispose();
		if (_module is not null)
		{
			await _module.DisposeAsync();
		}
	}

	private async Task<IJSObjectReference> ModuleAsync() =>
		_module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/fsaccess.js");

	/// <summary>Target of <c>callback.invokeMethodAsync('Changed', visible)</c>.</summary>
	[ExcludeFromCodeCoverage]
	public sealed class VisibilityCallback(Action<bool> changed)
	{
		[JSInvokable]
		public void Changed(bool visible) => changed(visible);
	}
}
