using System.Diagnostics.CodeAnalysis;
using AiChromeProxy.Client.Chat;
using Microsoft.JSInterop;

namespace AiChromeProxy.Client.Shell;

/// <summary>Target of the diagram toolbar's calls (<c>Scripts/diagrams.ts</c>): a Save menu item and the name of a download.</summary>
[ExcludeFromCodeCoverage]
public sealed class DiagramCallback(Func<DiagramFileKind, string, Task> save, Func<string, string> name)
{
	/// <summary>A Save menu item was clicked.</summary>
	/// <param name="kind">A <see cref="DiagramFileKind"/> name.</param>
	/// <param name="source">The mermaid source.</param>
	[JSInvokable]
	public Task Save(string kind, string source) => save(Enum.Parse<DiagramFileKind>(kind), source);

	/// <summary>The file name of a download, without folder and extension.</summary>
	/// <param name="source">The mermaid source.</param>
	/// <returns>The name.</returns>
	[JSInvokable]
	public string Name(string source) => name(source);
}
