namespace AiChromeProxy.Client.Chat;

/// <summary>What a diagram is saved as.</summary>
public enum DiagramFileKind
{
	/// <summary>The mermaid source (<c>.mmd</c>).</summary>
	Source,

	/// <summary>The drawn diagram as SVG.</summary>
	Svg,

	/// <summary>The drawn diagram as PNG.</summary>
	Png,
}
