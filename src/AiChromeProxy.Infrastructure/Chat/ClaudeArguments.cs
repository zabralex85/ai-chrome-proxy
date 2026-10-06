using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>Pure decisions of the Claude runner: the argument list, the command lookup and the <c>.cmd</c> command line.</summary>
public static class ClaudeArguments
{
	/// <summary>Appended to the system prompt so the UI can render diagrams and link code.</summary>
	public const string Convention = "You are used through a web UI. Draw diagrams as ```mermaid fenced blocks. Refer to code as `path:line` or `path#Symbol` relative to the repository root.";

	private const string ApprovalTool = "mcp__aicp__approve";

	/// <summary>
	/// The arguments for one turn. <c>--allowedTools</c> is variadic, so it comes right before <c>--append-system-prompt</c>, which ends its list;
	/// <c>--settings</c> (the project's MCP server and plugin switches) comes before every variadic flag (<c>--mcp-config</c> too), so none swallows it;
	/// <see cref="AgentOptions.Args"/> follow last.
	/// </summary>
	public static IReadOnlyList<string> Build(AgentRun run, AgentOptions options)
	{
		var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
		if (!string.IsNullOrEmpty(run.ResumeId))
		{
			args.AddRange(["--resume", run.ResumeId]);
		}

		if (Settings(run) is { } settings)
		{
			args.AddRange(["--settings", settings]);
		}

		switch (run.Permissions)
		{
			case "all":
				args.AddRange(["--permission-mode", "bypassPermissions"]);
				break;
			case "settings":
				args.AddRange(["--permission-mode", "dontAsk"]);
				break;
			default:
				args.AddRange(["--permission-mode", "acceptEdits"]);
				if (!string.IsNullOrEmpty(run.ApprovalUrl))
				{
					args.AddRange(["--permission-prompts", "host", "--permission-prompt-tool", ApprovalTool, "--mcp-config", McpConfig(run.ApprovalUrl, run.ApprovalToken ?? string.Empty)]);
				}

				break;
		}

		if (!string.IsNullOrWhiteSpace(run.Model))
		{
			args.AddRange(["--model", run.Model]);
		}

		// Empty rules and rules that look like a flag would end the variadic list early or be read as an option.
		var rules = (run.AllowedTools ?? []).Where(r => !string.IsNullOrWhiteSpace(r) && !r.StartsWith('-')).ToList();
		if (rules.Count > 0)
		{
			args.Add("--allowedTools");
			args.AddRange(rules);
		}

		args.AddRange(["--append-system-prompt", Convention]);
		args.AddRange(options.Args);
		return args;
	}

	/// <summary>
	/// The file to start for <paramref name="command"/>: a rooted or directory-qualified command is kept as it is when it exists; a bare name is searched in
	/// <paramref name="pathDirs"/> as <c>.exe</c> in every directory first, then as <c>.cmd</c> (the npm shim); null when nothing exists.
	/// </summary>
	public static string? ResolveCommand(string command, IEnumerable<string> pathDirs, Func<string, bool> fileExists)
	{
		if (Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/'))
		{
			return fileExists(command) ? command : null;
		}

		var dirs = pathDirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
		var names = Path.HasExtension(command) ? new[] { command } : [command + ".exe", command + ".cmd"];
		foreach (var name in names)
		{
			foreach (var dir in dirs)
			{
				var candidate = Path.Combine(dir, name);
				if (fileExists(candidate))
				{
					return candidate;
				}
			}
		}

		return null;
	}

	/// <summary>
	/// True when an argument holds a character <c>cmd.exe</c> would interpret even inside quotes (<c>&amp; | &lt; &gt; ^ % !</c>, line breaks):
	/// such a run must not go through a <c>.cmd</c> shim.
	/// </summary>
	public static bool UnsafeForCmd(IEnumerable<string> args) => args.Any(a => a.AsSpan().IndexOfAny("&|<>^%!\r\n") >= 0);

	/// <summary>
	/// The command line for <c>cmd.exe /d /s /c "..."</c>: every item quoted (CommandLineToArgvW rules), the whole wrapped in one more pair of quotes
	/// that <c>/s</c> strips, so a path with spaces and quotes inside arguments survive.
	/// </summary>
	public static string CmdLine(string file, IEnumerable<string> args)
	{
		var line = new StringBuilder("\"");
		Quote(line, file);
		foreach (var arg in args)
		{
			line.Append(' ');
			Quote(line, arg);
		}

		return line.Append('"').ToString();
	}

	/// <summary>
	/// The <c>--settings</c> JSON (compact) from the project's lists, each cleaned by <see cref="ClaudeToolEntries.Clean"/>: <c>enabledPlugins</c> (each
	/// disabled id → false), <c>deniedMcpServers</c> (never <see cref="ClaudeToolEntries.ApprovalServer"/>), <c>enabledMcpjsonServers</c>; null when all are empty.
	/// </summary>
	private static string? Settings(AgentRun run)
	{
		var json = new JsonObject();
		var plugins = ClaudeToolEntries.Clean(run.DisabledPlugins);
		if (plugins.Count > 0)
		{
			json["enabledPlugins"] = new JsonObject(plugins.Select(id => KeyValuePair.Create(id, (JsonNode?)false)));
		}

		var denied = ClaudeToolEntries.Clean(run.DisabledMcpServers).Where(n => n != ClaudeToolEntries.ApprovalServer).ToList();
		if (denied.Count > 0)
		{
			json["deniedMcpServers"] = new JsonArray([.. denied.Select(n => (JsonNode?)new JsonObject { ["serverName"] = n })]);
		}

		var approved = ClaudeToolEntries.Clean(run.ApprovedMcpServers);
		if (approved.Count > 0)
		{
			json["enabledMcpjsonServers"] = new JsonArray([.. approved.Select(n => (JsonNode?)n)]);
		}

		return json.Count == 0 ? null : json.ToJsonString();
	}

	private static string McpConfig(string url, string token) => JsonSerializer.Serialize(new
	{
		mcpServers = new
		{
			aicp = new { type = "http", url, headers = new { Authorization = "Bearer " + token } },
		},
	});

	private static void Quote(StringBuilder sb, string arg)
	{
		sb.Append('"');
		var backslashes = 0;
		foreach (var c in arg)
		{
			if (c == '\\')
			{
				backslashes++;
				continue;
			}

			if (c == '"')
			{
				sb.Append('\\', (backslashes * 2) + 1);
			}
			else
			{
				sb.Append('\\', backslashes);
			}

			backslashes = 0;
			sb.Append(c);
		}

		sb.Append('\\', backslashes * 2).Append('"');
	}
}
