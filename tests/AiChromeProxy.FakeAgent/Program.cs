// A stand-in for the agent CLI in tests: ignores its arguments, reads stdin to the end, then prints the lines of a fixture file.
//   FAKE_AGENT_SCRIPT       path of the file whose lines are printed to stdout; a line `#approve <tool> <json-input>` instead calls the
//                           approval tool from the `--mcp-config` argument (MCP Streamable HTTP: initialize, tools/call) and prints
//                           an assistant message "approve: <behavior>" (allow, deny, or error: <reason>)
//   FAKE_AGENT_SCRIPT_DIR   when set, the script is `<dir>/<first word of the stdin text>.jsonl` (one fixture per message, so one host can serve
//                           several scenarios); overrides FAKE_AGENT_SCRIPT; a first word with other characters than letters, digits, - and _ runs nothing
//   FAKE_AGENT_DELAY_MS     pause before each line (default 0)
//   FAKE_AGENT_STDIN_FILE   when set, the stdin text is written to this file
//   FAKE_AGENT_STDERR       when set, this text is written to stderr first
//   FAKE_AGENT_ARGS_FILE    when set, the received arguments are written to this file, one per line (UTF-8)
//   FAKE_AGENT_EXIT_CODE    exit code (default 0)
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var argsFile = Environment.GetEnvironmentVariable("FAKE_AGENT_ARGS_FILE");
if (!string.IsNullOrEmpty(argsFile))
{
	await File.WriteAllLinesAsync(argsFile, args);
}

string stdin;
using (var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)))
{
	stdin = await reader.ReadToEndAsync();
}

var stdinFile = Environment.GetEnvironmentVariable("FAKE_AGENT_STDIN_FILE");
if (!string.IsNullOrEmpty(stdinFile))
{
	await File.WriteAllTextAsync(stdinFile, stdin);
}

var stderr = Environment.GetEnvironmentVariable("FAKE_AGENT_STDERR");
if (!string.IsNullOrEmpty(stderr))
{
	await Console.Error.WriteLineAsync(stderr);
}

var script = Environment.GetEnvironmentVariable("FAKE_AGENT_SCRIPT");
var scriptDir = Environment.GetEnvironmentVariable("FAKE_AGENT_SCRIPT_DIR");
if (!string.IsNullOrEmpty(scriptDir))
{
	var word = stdin.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
	script = word.Length > 0 && word.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ? Path.Combine(scriptDir, word + ".jsonl") : null;
}

var delay = int.TryParse(Environment.GetEnvironmentVariable("FAKE_AGENT_DELAY_MS"), out var ms) ? ms : 0;
if (!string.IsNullOrEmpty(script) && File.Exists(script))
{
	foreach (var line in await File.ReadAllLinesAsync(script))
	{
		if (delay > 0)
		{
			await Task.Delay(delay);
		}

		var output = line.StartsWith("#approve ", StringComparison.Ordinal) ? await ApproveAsync(args, line["#approve ".Length..]) : line;
		Console.Out.WriteLine(output);
		Console.Out.Flush();
	}
}

return int.TryParse(Environment.GetEnvironmentVariable("FAKE_AGENT_EXIT_CODE"), out var code) ? code : 0;

// Calls mcp__aicp__approve like Claude Code would and returns a stream-json assistant line with the answer.
static async Task<string> ApproveAsync(string[] args, string step)
{
	string behavior;
	try
	{
		var space = step.IndexOf(' ', StringComparison.Ordinal);
		var tool = step[..space];
		var input = JsonNode.Parse(step[(space + 1)..]);
		var index = Array.IndexOf(args, "--mcp-config");
		var server = JsonNode.Parse(args[index + 1])!["mcpServers"]!["aicp"]!;
		var url = (string)server["url"]!;
		var authorization = (string)server["headers"]!["Authorization"]!;
		using (var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan })
		{
			await RpcAsync(http, url, authorization, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize", ["params"] = new JsonObject { ["protocolVersion"] = "2025-06-18", ["capabilities"] = new JsonObject(), ["clientInfo"] = new JsonObject { ["name"] = "fake-agent", ["version"] = "1" } } });
			await RpcAsync(http, url, authorization, new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "notifications/initialized" });
			var result = await RpcAsync(http, url, authorization, new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = "approve", ["arguments"] = new JsonObject { ["tool_name"] = tool, ["input"] = input } } });
			var text = (string)result!["result"]!["content"]![0]!["text"]!;
			behavior = (string)JsonNode.Parse(text)!["behavior"]!;
		}
	}
	catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or NullReferenceException or ArgumentException)
	{
		behavior = $"error: {ex.Message}";
	}

	var message = new JsonObject { ["type"] = "text", ["text"] = $"approve: {behavior}" };
	return new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["content"] = new JsonArray(message) } }.ToJsonString();
}

// One JSON-RPC message over Streamable HTTP; returns the response (plain JSON or the last SSE data event), or null for a notification.
static async Task<JsonNode?> RpcAsync(HttpClient http, string url, string authorization, JsonObject message)
{
	using (var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(message.ToJsonString(), Encoding.UTF8, "application/json") })
	{
		request.Headers.TryAddWithoutValidation("Authorization", authorization);
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
		request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
		request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
		using (var response = await http.SendAsync(request))
		{
			response.EnsureSuccessStatusCode();
			var body = await response.Content.ReadAsStringAsync();
			if (message["id"] is null || body.Length == 0)
			{
				return null;
			}

			if (response.Content.Headers.ContentType?.MediaType != "text/event-stream")
			{
				return JsonNode.Parse(body);
			}

			var data = body.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.StartsWith("data:", StringComparison.Ordinal)).Select(l => l["data:".Length..].Trim()).Where(d => d.Length > 0);
			return JsonNode.Parse(data.Last());
		}
	}
}
