using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// The repo's <c>.mcp.json</c> (<c>{ "mcpServers": { "&lt;name&gt;": { ...entry... } } }</c> at the mirror's repo root) and the approvals pinned to its
/// entries: an approval (<see cref="ClaudeToolEntries.Approval"/>) holds for as long as its entry's hash (SHA-256 hex of the entry's JSON with object keys
/// sorted, no whitespace) is unchanged, so a server whose command Claude (or anyone) edits needs approving again.
/// </summary>
public static class McpJson
{
	/// <summary>The file, relative to the repo folder.</summary>
	public const string FileName = ".mcp.json";

	/// <summary>A bigger file is not read (no entries).</summary>
	public const int MaxBytes = 256 * 1024;

	/// <summary>Longest <see cref="McpJsonEntry.Command"/>, in characters.</summary>
	public const int MaxCommandLength = 300;

	/// <summary>The entries of the repo's <c>.mcp.json</c> by server name; none when it is missing, too big, unreadable or not valid.</summary>
	public static async Task<IReadOnlyDictionary<string, McpJsonEntry>> ReadAsync(IMirrorStore mirror, string repo, CancellationToken ct)
	{
		try
		{
			var bytes = await mirror.ReadAsync(repo, FileName, 0, MaxBytes + 1, ct);
			return bytes is null || bytes.Length > MaxBytes ? Empty() : Parse(Encoding.UTF8.GetString(bytes));
		}
		catch (Exception ex) when (ex is not OperationCanceledException)
		{
			// Missing folder, a link, a file being replaced: nothing approved.
			return Empty();
		}
	}

	/// <summary>The entries of a <c>.mcp.json</c> text by server name; none when it is not a JSON object with an <c>mcpServers</c> object.</summary>
	public static IReadOnlyDictionary<string, McpJsonEntry> Parse(string json)
	{
		try
		{
			if (JsonNode.Parse(json) is JsonObject root
				&& root["mcpServers"] is JsonObject servers)
			{
				return servers.ToDictionary(s => s.Key, s => new McpJsonEntry(Command(s.Value), Hash(s.Value)), StringComparer.Ordinal);
			}
		}
		catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
		{
			// Not valid JSON (or a repeated key).
		}

		return Empty();
	}

	/// <summary>SHA-256 (lower-case hex) of the node's canonical JSON: object keys sorted (ordinal) at every level, no whitespace.</summary>
	public static string Hash(JsonNode? node) =>
		Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Canonical(node)?.ToJsonString() ?? "null")));

	/// <summary>
	/// Splits stored approvals into the server names whose <c>.mcp.json</c> entry still has the approved hash (<paramref name="entries"/>) and the names
	/// of the others (changed or removed entries, approvals without a hash); <see cref="ClaudeToolEntries.ApprovalServer"/> is never approved.
	/// </summary>
	public static (IReadOnlyList<string> Approved, IReadOnlyList<string> Stale) Approved(IEnumerable<string>? approvals, IReadOnlyDictionary<string, McpJsonEntry> entries)
	{
		var approved = new List<string>();
		var stale = new List<string>();
		foreach (var approval in ClaudeToolEntries.Clean(approvals, ClaudeToolEntries.MaxApprovalLength))
		{
			var name = ClaudeToolEntries.ApprovalName(approval);
			if (name == ClaudeToolEntries.ApprovalServer)
			{
				continue;
			}

			(entries.TryGetValue(name, out var entry) && approval == ClaudeToolEntries.Approval(name, entry.Hash) ? approved : stale).Add(name);
		}

		return (approved, stale);
	}

	private static Dictionary<string, McpJsonEntry> Empty() => new(StringComparer.Ordinal);

	/// <summary><c>command</c> and its <c>args</c> (stdio), else <c>url</c>; at most <see cref="MaxCommandLength"/> characters.</summary>
	private static string? Command(JsonNode? node)
	{
		if (node is not JsonObject entry)
		{
			return null;
		}

		var text = Str(entry["command"]) is { } command
			? string.Join(' ', [command, .. (entry["args"] as JsonArray ?? []).Select(a => Str(a) ?? a?.ToJsonString() ?? "null")])
			: Str(entry["url"]);
		return text is { Length: > MaxCommandLength } ? text[..(MaxCommandLength - 1)] + "…" : text;
	}

	private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

	private static JsonNode? Canonical(JsonNode? node) => node switch
	{
		JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, Canonical(p.Value)))),
		JsonArray a => new JsonArray([.. a.Select(Canonical)]),
		_ => node?.DeepClone(),
	};
}
