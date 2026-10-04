using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Client.Tree;

/// <summary>Which scanned folders the tree shows.</summary>
public static class TreeDirectories
{
	/// <summary>
	/// The folders sync does not exclude. The scan only skips the built-in and plain <c>.gitignore</c> directory names, so a folder hidden by
	/// a wildcard rule or the extra excludes still arrives: the tree must pass <c>FolderScan.Directories</c> through here before <c>FileTree.Build</c>.
	/// </summary>
	public static IEnumerable<string> Visible(IEnumerable<string> directories, IgnoreRules? rules) =>
		rules is null ? directories : directories.Where(d => !rules.IsIgnored(d) && !rules.IsIgnored(d + "/x"));
}
