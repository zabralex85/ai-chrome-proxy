namespace AiChromeProxy.Client.Packages;

/// <summary>One row of the Packages tab; <paramref name="Text"/> is what the row says (exact texts of the spec).</summary>
public sealed record PackageRow(string Id, string Version, PackageState State, string Text);
