using System.Text.Json;

namespace AiChromeProxy.Tray.Clef;

/// <summary>Parses one CLEF line (compact JSON, as written by the Server's Serilog file sink).</summary>
public static class ClefParser
{
	/// <returns>The entry, or null for a blank, malformed or timestamp-less line.</returns>
	public static LogEntry? Parse(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return null;
		}

		try
		{
			using (var json = JsonDocument.Parse(line))
			{
				var root = json.RootElement;
				if (root.ValueKind != JsonValueKind.Object
					|| !root.TryGetProperty("@t", out var t)
					|| t.ValueKind != JsonValueKind.String
					|| !t.TryGetDateTimeOffset(out var timestamp))
				{
					return null;
				}

				var level = String(root, "@l") is { } name && Enum.TryParse<ClefLevel>(name, ignoreCase: true, out var parsed) ? parsed : ClefLevel.Information;

				// @m: rendered message (RenderedCompactJsonFormatter); @mt: template only (CompactJsonFormatter).
				return new LogEntry(timestamp, level, String(root, "@m") ?? String(root, "@mt") ?? string.Empty, String(root, "@x"));
			}
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private static string? String(JsonElement root, string name) =>
		root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
