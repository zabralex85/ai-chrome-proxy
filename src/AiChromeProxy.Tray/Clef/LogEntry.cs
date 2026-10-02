using System.Globalization;

namespace AiChromeProxy.Tray.Clef;

public sealed record LogEntry(DateTimeOffset Timestamp, ClefLevel Level, string Message, string? Exception)
{
	public string Time => Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

	/// <summary>Logs window filter: at least <paramref name="minimum"/>, and <paramref name="text"/> (if any) in the message or exception, case-insensitive.</summary>
	public bool Matches(ClefLevel minimum, string? text) =>
		Level >= minimum
		&& (string.IsNullOrWhiteSpace(text)
			|| Message.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase)
			|| (Exception?.Contains(text.Trim(), StringComparison.OrdinalIgnoreCase) ?? false));
}
