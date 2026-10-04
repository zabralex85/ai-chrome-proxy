using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Tree;

/// <summary>Rules for the name typed into the tree when creating or renaming an item.</summary>
public static class TreeNames
{
	/// <summary>Checks a new name for an item inside <paramref name="folder"/>.</summary>
	/// <param name="folder">Folder path, <c>/</c>-separated; empty for the picked folder.</param>
	/// <param name="name">What the user typed.</param>
	/// <param name="siblings">Names already in the folder; when renaming, without the item itself (so a change of case only is allowed).</param>
	/// <param name="ignore">Sync rules, to flag names sync would leave out; null flags nothing.</param>
	/// <param name="isFolder">Whether the new item is a folder (an excluded folder hides everything inside).</param>
	public static NameCheck Check(string folder, string name, IEnumerable<string> siblings, IgnoreRules? ignore = null, bool isFolder = false)
	{
		if (string.IsNullOrWhiteSpace(name))
		{
			return new NameCheck("Enter a name.", false);
		}

		if (name.Contains('/') || name.Contains('\\'))
		{
			return new NameCheck("A name cannot contain / or \\.", false);
		}

		if (siblings.Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase)))
		{
			return new NameCheck($"'{name}' already exists here.", false);
		}

		var path = folder.Length == 0 ? name : folder + "/" + name;
		var error = SyncPath.GetError(path);
		if (error is not null)
		{
			return new NameCheck(error, false);
		}

		var excluded = ignore is not null && (ignore.IsIgnored(path) || (isFolder && ignore.IsIgnored(path + "/x")));
		return new NameCheck(null, excluded);
	}
}
