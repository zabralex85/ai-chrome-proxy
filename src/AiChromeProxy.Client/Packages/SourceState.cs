namespace AiChromeProxy.Client.Packages;

/// <summary>The NuGet cache folder of this browser: none picked, readable, waiting for a click to allow it, or gone.</summary>
public enum SourceState
{
	None,
	Ready,
	NeedsPermission,
	NotFound,
}
