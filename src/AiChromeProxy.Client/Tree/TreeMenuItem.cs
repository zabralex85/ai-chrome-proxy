namespace AiChromeProxy.Client.Tree;

/// <summary>One entry of the tree's context menu.</summary>
/// <param name="Id">What it does: <see cref="TreeMenu.NewFile"/>, <see cref="TreeMenu.NewFolder"/>, <see cref="TreeMenu.Rename"/> or <see cref="TreeMenu.Delete"/>.</param>
/// <param name="Label">Its text.</param>
/// <param name="Icon">Icon name.</param>
/// <param name="Shortcut">The key that does the same without the menu, or null.</param>
/// <param name="State">Whether it can be used and, when not, why.</param>
public sealed record TreeMenuItem(string Id, string Label, string Icon, string? Shortcut, MenuState State);
