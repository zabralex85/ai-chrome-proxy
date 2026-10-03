namespace AiChromeProxy.Domain.Sync;

/// <summary>Paging shared by the browser and the server so every message stays under the size limits.</summary>
public static class SyncPages
{
	/// <summary>Splits into pages of at most <see cref="SyncLimits.MaxPageEntries"/> items and <see cref="SyncLimits.MaxPageBytes"/> bytes (an item bigger than that gets a page of its own).</summary>
	public static IEnumerable<IReadOnlyList<T>> Split<T>(IEnumerable<T> items, Func<T, int> size)
	{
		var page = new List<T>();
		var bytes = 0;
		foreach (var item in items)
		{
			var itemSize = size(item);
			if (page.Count == SyncLimits.MaxPageEntries || (page.Count > 0 && bytes + itemSize > SyncLimits.MaxPageBytes))
			{
				yield return page;
				page = [];
				bytes = 0;
			}

			page.Add(item);
			bytes += itemSize;
		}

		if (page.Count > 0)
		{
			yield return page;
		}
	}
}
