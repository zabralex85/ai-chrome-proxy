namespace AiChromeProxy.Client.Shell;

/// <summary>A folder or file of the explorer tree (structure only: sync states are looked up by <see cref="Path"/>).</summary>
public sealed record TreeNode(string Name, string Path, bool IsFolder, IReadOnlyList<TreeNode> Children);
