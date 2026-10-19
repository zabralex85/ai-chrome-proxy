namespace AiChromeProxy.Client.Packages;

/// <summary>A project of the repo and why it contributes no packages, if so ("No restore yet", "Could not read obj/project.assets.json").</summary>
public sealed record ProjectRow(string Path, string? Problem);
