using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Domain.Chat;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Turns the lines of Claude Code's <c>--output-format stream-json --include-partial-messages</c> into chat events, one parser per run.
/// The events come back as <see cref="ChatEvent"/>s with empty <c>SessionId</c>/<c>RunId</c> and <c>Seq</c> 0 for the caller to fill.
/// Unknown types and malformed lines are skipped.
/// </summary>
public sealed class StreamJsonParser
{
	private static readonly string[] PathTools = ["Edit", "Write", "Read", "MultiEdit", "NotebookEdit"];

	private readonly ILogger<StreamJsonParser>? _logger;
	private string? _cwd;

	/// <summary>Initializes a new instance of the <see cref="StreamJsonParser"/> class.</summary>
	/// <param name="logger">Receives a warning for each malformed line.</param>
	public StreamJsonParser(ILogger<StreamJsonParser>? logger = null)
	{
		_logger = logger;
	}

	/// <summary>Gets Claude Code's session id, known after the first line that carries one (<c>system/init</c>).</summary>
	public string? ClaudeSessionId { get; private set; }

	/// <summary>Parses one output line.</summary>
	/// <param name="line">A line without its terminator (a trailing <c>\r</c> is ignored).</param>
	/// <returns>The events the line produced, usually none or one.</returns>
	public IReadOnlyList<ChatEvent> Feed(string line)
	{
		if (string.IsNullOrWhiteSpace(line))
		{
			return [];
		}

		JsonObject? root;
		try
		{
			root = JsonNode.Parse(line.Trim()) as JsonObject;
		}
		catch (JsonException ex)
		{
			_logger?.LogWarning(ex, "Skipping a malformed stream-json line.");
			return [];
		}

		if (root is null)
		{
			return [];
		}

		ClaudeSessionId ??= Str(root, "session_id");
		return Str(root, "type") switch
		{
			"system" => Init(root),
			"stream_event" => Delta(root),
			"assistant" => Assistant(root),
			"user" => User(root),
			"result" => Result(root),
			_ => [],
		};
	}

	private static string? Str(JsonNode? node, string name) => node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

	private static bool Flag(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

	private static ChatEvent New(string kind, string? text = null, string? toolId = null, string? name = null, string? summary = null, bool? isError = null)
	{
		// The Domain splitter truncates every summary (UTF-8 bytes, ellipsis, surrogate-safe).
		var e = new ChatEvent(string.Empty, string.Empty, 0, kind, text, toolId, name, summary, isError);
		return ChatEventSplitter.Split(e)[0];
	}

	private static string Cap(string text) => New(ChatEventKinds.Result, summary: text).Summary!;

	private static string Summarize(string? tool, JsonNode? input, string? cwd)
	{
		var path = Str(input, "file_path") ?? Str(input, "notebook_path");
		return tool switch
		{
			"Bash" when Str(input, "command") is { } command => command,
			not null when path is not null && Array.IndexOf(PathTools, tool) >= 0 => Relative(path, cwd),
			"Glob" or "Grep" when Str(input, "pattern") is { } pattern => pattern,
			_ => input?.ToJsonString() ?? string.Empty,
		};
	}

	private static string Relative(string path, string? cwd)
	{
		if (string.IsNullOrEmpty(cwd) || path.Length <= cwd.Length + 1 || !path.StartsWith(cwd, StringComparison.OrdinalIgnoreCase) || path[cwd.Length] is not ('/' or '\\'))
		{
			return path;
		}

		return path[(cwd.Length + 1)..];
	}

	private static string ResultText(JsonNode? content) => content switch
	{
		JsonValue v when v.TryGetValue<string>(out var s) => s,
		JsonArray a => string.Join("\n", a.Where(x => Str(x, "type") == "text").Select(x => Str(x, "text"))),
		_ => string.Empty,
	};

	private static IEnumerable<JsonObject> Blocks(JsonObject root) =>
		root["message"]?["content"] is JsonArray content ? content.OfType<JsonObject>() : [];

	private IReadOnlyList<ChatEvent> Init(JsonObject root)
	{
		if (Str(root, "subtype") == "init")
		{
			_cwd = Str(root, "cwd") ?? _cwd;
		}

		return [];
	}

	private IReadOnlyList<ChatEvent> Delta(JsonObject root)
	{
		var e = root["event"];
		return Str(e, "type") == "content_block_delta" && e!["delta"] is JsonObject d && Str(d, "type") == "text_delta" && Str(d, "text") is { Length: > 0 } text
			? [New(ChatEventKinds.Text, text)]
			: [];
	}

	private IReadOnlyList<ChatEvent> Assistant(JsonObject root)
	{
		var events = new List<ChatEvent>();
		foreach (var block in Blocks(root))
		{
			switch (Str(block, "type"))
			{
				case "text" when Str(block, "text") is { Length: > 0 } text:
					events.Add(New(ChatEventKinds.Message, text));
					break;
				case "tool_use":
					events.Add(New(ChatEventKinds.Tool, toolId: Str(block, "id"), name: Str(block, "name"), summary: Summarize(Str(block, "name"), block["input"], _cwd)));
					break;
			}
		}

		return events;
	}

	private IReadOnlyList<ChatEvent> User(JsonObject root)
	{
		var events = new List<ChatEvent>();
		foreach (var block in Blocks(root))
		{
			if (Str(block, "type") == "tool_result")
			{
				events.Add(New(ChatEventKinds.ToolResult, toolId: Str(block, "tool_use_id"), summary: ResultText(block["content"]), isError: Flag(block, "is_error")));
			}
		}

		return events;
	}

	private IReadOnlyList<ChatEvent> Result(JsonObject root)
	{
		var subtype = Str(root, "subtype");
		var ok = !Flag(root, "is_error") && subtype is null or "success";
		decimal? cost = root["total_cost_usd"] is JsonValue c && c.TryGetValue<decimal>(out var d) ? d : null;
		long? duration = root["duration_ms"] is JsonValue m && m.TryGetValue<long>(out var l) ? l : null;
		var error = ok ? null : Cap(Str(root, "result") is { Length: > 0 } text ? text : subtype ?? "error");
		return [new ChatEvent(string.Empty, string.Empty, 0, ChatEventKinds.Result, Ok: ok, CostUsd: cost, DurationMs: duration, Error: error)];
	}
}
