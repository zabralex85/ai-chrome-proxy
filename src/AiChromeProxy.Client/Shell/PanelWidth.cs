namespace AiChromeProxy.Client.Shell;

/// <summary>Widths of the resizable side panels.</summary>
public static class PanelWidth
{
	public const int Min = 180;
	public const int Max = 600;

	/// <summary>The centre column (tabs, main area, chat) never gets narrower than this.</summary>
	public const int MinCenter = 360;

	/// <summary>The shell's CSS <c>min-width</c>: below it the page scrolls instead of shrinking.</summary>
	public const int MinShell = 1024;

	/// <summary><see cref="Min"/>–<see cref="Max"/>, and no wider than leaves <see cref="MinCenter"/> next to the other panel (0 when collapsed); <see cref="Min"/> wins.</summary>
	public static int Clamp(int width, int other, int viewport) =>
		Math.Clamp(width, Min, Math.Clamp(Math.Max(viewport, MinShell) - other - MinCenter, Min, Max));
}
