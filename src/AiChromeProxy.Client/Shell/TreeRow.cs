namespace AiChromeProxy.Client.Shell;

/// <summary>A visible tree row: the node and its depth (0 at the root).</summary>
public sealed record TreeRow(TreeNode Node, int Depth);
