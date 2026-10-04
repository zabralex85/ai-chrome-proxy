using Microsoft.AspNetCore.Components;

namespace AiChromeProxy.Client.Navigator;

/// <summary>The read-only code viewer (Monaco) inside a host element; <see cref="JsFileViewer"/> in the app. Lines are 1-based.</summary>
public interface IFileViewer
{
	/// <summary>Shows <paramref name="text"/> in <paramref name="host"/> as the viewer <paramref name="id"/> (replacing the one with that id), highlighted by the language of <paramref name="path"/>'s extension.</summary>
	/// <param name="host">The element the viewer fills.</param>
	/// <param name="id">A stable id of the viewer: later calls and the disposal use it, so they work when the element is gone already.</param>
	/// <param name="path">The file's path, for its language.</param>
	/// <param name="text">The file's text.</param>
	/// <param name="line">The line to reveal in the centre and highlight (a symbol's line from <see cref="SymbolFinder"/>), or null for the top.</param>
	Task OpenAsync(ElementReference host, string id, string path, string text, int? line);

	/// <summary>Replaces the text, keeping the scroll position, and marks <paramref name="changedLines"/> (from <see cref="LineDiff"/>) for two seconds.</summary>
	/// <param name="id">The viewer's id.</param>
	/// <param name="text">The new text.</param>
	/// <param name="changedLines">The inserted or changed lines in the new text.</param>
	Task UpdateAsync(string id, string text, IReadOnlyList<int> changedLines);

	/// <summary>Reveals and highlights <paramref name="line"/>; null goes to the top.</summary>
	/// <param name="id">The viewer's id.</param>
	/// <param name="line">The line, or null.</param>
	Task RevealAsync(string id, int? line);

	/// <summary>Dark or light for every viewer; null follows the shell's theme (the system's when none is picked).</summary>
	/// <param name="dark">Whether dark, or null.</param>
	Task SetThemeAsync(bool? dark);

	/// <summary>Disposes the viewer <paramref name="id"/> and its model.</summary>
	/// <param name="id">The viewer's id.</param>
	Task DisposeAsync(string id);
}
