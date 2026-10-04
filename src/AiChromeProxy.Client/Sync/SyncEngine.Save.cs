using AiChromeProxy.Client.Tree;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Sync;

public sealed partial class SyncEngine
{
	public const string SavedExcludedNote = "Saved; excluded from sync";

	/// <summary>
	/// Saves a new file into the picked folder (missing folders are created); never overwrites. The name and existence checks come first
	/// (no prompt); <paramref name="content"/> runs inside the gated action, after write access was asked. Call it straight from the click.
	/// </summary>
	public async Task<TreeActionResult> SaveFileAsync(string path, Func<Task<byte[]>> content)
	{
		var parent = ParentOf(path);
		var name = NameOf(path);
		var check = TreeNames.Check(parent, name, Siblings(parent), _rules);
		if (check.Error is not null)
		{
			return new TreeActionResult(false, check.Error);
		}

		try
		{
			// The target may be excluded or not scanned yet, so the scan's siblings are not enough.
			if (await folder.HashNowAsync(path) is not null)
			{
				return new TreeActionResult(false, $"'{name}' already exists here.");
			}
		}
		catch (JSException ex)
		{
			return new TreeActionResult(false, ex.Message.Split('\n')[0].Trim());
		}

		return await ActAsync(
			$"save '{path}'",
			[path],
			async () => await folder.WriteAsync(path, await content()),
			SyncActivityKind.TreeAction,
			check.Excluded ? $"Saved '{path}'; excluded from sync." : $"Saved '{path}'.",
			check.Excluded ? SavedExcludedNote : null);
	}
}
