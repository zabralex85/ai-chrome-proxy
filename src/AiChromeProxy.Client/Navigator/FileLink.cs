namespace AiChromeProxy.Client.Navigator;

/// <summary>A <c>path:line</c> or <c>path#Symbol</c> link from the chat: the file's viewer reveals it once.</summary>
/// <param name="Path">The repo-relative path.</param>
/// <param name="Line">The 1-based line; 0 when the link names a symbol.</param>
/// <param name="Symbol">The symbol, or null.</param>
public sealed record FileLink(string Path, int Line, string? Symbol)
{
	/// <summary>Gets or sets whether the viewer revealed the target already (a viewer created later must not jump back to an old link).</summary>
	public bool Applied { get; set; }

	/// <summary>The line to reveal in <paramref name="text"/>: the link's line, or the symbol's; null when there is none (the symbol is not in the text).</summary>
	/// <param name="text">The file's text.</param>
	/// <returns>The line, or null.</returns>
	public int? LineIn(string text) => Symbol is null ? (Line > 0 ? Line : null) : SymbolFinder.Find(text, Symbol);
}
