using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>Reads <c>claude plugin list --json</c>: <c>[{ id, version, enabled, ... }]</c>; other fields are ignored.</summary>
public static class PluginListParser
{
	/// <summary>The plugins in the order listed (entries without an id are skipped; a missing <c>enabled</c> counts as true).</summary>
	/// <exception cref="FormatException">The output is not a JSON array.</exception>
	public static IReadOnlyList<ClaudePlugin> Parse(string json)
	{
		JsonNode? root;
		try
		{
			root = JsonNode.Parse(json);
		}
		catch (JsonException ex)
		{
			throw new FormatException("`claude plugin list --json` did not print JSON.", ex);
		}

		if (root is not JsonArray items)
		{
			throw new FormatException("`claude plugin list --json` did not print a list.");
		}

		return [.. items.OfType<JsonObject>()
			.Where(p => Str(p, "id") is { Length: > 0 })
			.Select(p => new ClaudePlugin(Str(p, "id")!, NameOf(Str(p, "id")!), Str(p, "version"), p["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var on) || on))];
	}

	/// <summary>The plugin name of an id: the part before <c>@</c>.</summary>
	public static string NameOf(string id)
	{
		var at = id.IndexOf('@', StringComparison.Ordinal);
		return at > 0 ? id[..at] : id;
	}

	private static string? Str(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
