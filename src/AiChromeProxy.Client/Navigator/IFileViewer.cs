using Microsoft.AspNetCore.Components;

namespace AiChromeProxy.Client.Navigator;

/// <summary>The read-only code viewer (Monaco) inside a host element; <see cref="JsFileViewer"/> in the app. Lines are 1-based.</summary>
public interface IFileViewer
{
	/// <summary>Shows <paramref name="text"/> in <paramref name="host"/> (replacing a viewer already there), highlighted by the language of <paramref name="path"/>'s extension.</summary>
	/// <param name="host">The element the viewer fills.</param>
	/// <param name="path">The file's path, for its language.</param>
	/// <param name="text">The file's text.</param>
	/// <param name="line">The line to reveal in the centre and highlight (a symbol's line from <see cref="SymbolFinder"/>), or null for the top.</param>
	Task OpenAsync(ElementReference host, string path, string text, int? line);

	/// <summary>Replaces the text, keeping the scroll position, and marks <paramref name="changedLines"/> (from <see cref="LineDiff"/>) for two seconds.</summary>
	/// <param name="host">The viewer's element.</param>
	/// <param name="text">The new text.</param>
	/// <param name="changedLines">The inserted or changed lines in the new text.</param>
	Task UpdateAsync(ElementReference host, string text, IReadOnlyList<int> changedLines);

	/// <summary>Reveals and highlights <paramref name="line"/>; null goes to the top.</summary>
	/// <param name="host">The viewer's element.</param>
	/// <param name="line">The line, or null.</param>
	Task RevealAsync(ElementReference host, int? line);

	/// <summary>Dark or light for every viewer; null follows the shell's theme (the system's when none is picked).</summary>
	/// <param name="dark">Whether dark, or null.</param>
	Task SetThemeAsync(bool? dark);

	/// <summary>Disposes the viewer in <paramref name="host"/>.</summary>
	/// <param name="host">The viewer's element.</param>
	Task DisposeAsync(ElementReference host);
}
