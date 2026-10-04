using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Chat;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AiChromeProxy.Server.Chat;

/// <summary>
/// The approval MCP endpoint (Streamable HTTP, stateless) at <see cref="Path"/>: Claude Code's <c>--permission-prompt-tool mcp__aicp__approve</c>.
/// Only loopback requests that carry a running run's bearer token reach it; anything else under <c>/mcp</c> gets 404, so it does not
/// advertise itself. Its one tool, <c>approve {tool_name, input}</c>, asks the chat through the <see cref="PermissionBroker"/>.
/// </summary>
public static class ApprovalMcp
{
	public const string Path = "/mcp/approve";

	public const string ToolName = "approve";

	private const string Deny = """{"behavior":"deny","message":"Denied in the chat."}""";

	private const string RunClaim = "aicp.approval.run";

	private static readonly JsonElement InputSchema = JsonDocument.Parse("""
		{
			"type": "object",
			"properties": {
				"tool_name": { "type": "string", "description": "The tool Claude wants to use." },
				"input": { "type": "object", "description": "The tool's input." }
			},
			"required": ["tool_name", "input"]
		}
		""").RootElement.Clone();

	/// <summary>Registers the endpoint's MCP server and the <see cref="IApprovalEndpoint"/> (<c>http://127.0.0.1:&lt;port&gt;/mcp/approve</c>).</summary>
	public static IServiceCollection AddApprovalMcp(this IServiceCollection services, int port)
	{
		services.AddSingleton<IApprovalEndpoint>(new ApprovalEndpoint(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{Path}")));
		services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "aicp", Version = "1" })
			.WithHttpTransport(o => o.Stateless = true)
			.WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
			{
				Tools = [new Tool { Name = ToolName, Description = "Asks the user in the web chat whether Claude may use a tool.", InputSchema = InputSchema }],
			}))
			.WithCallToolHandler(CallAsync);
		return services;
	}

	/// <summary>Maps the endpoint behind its gate; call after the Access middleware.</summary>
	public static void MapApprovalMcp(this WebApplication app)
	{
		app.Use(GateAsync);
		app.MapMcp(Path);
	}

	/// <summary>
	/// A request from this machine to the approval URL: loopback, addressed to <c>127.0.0.1</c> (as the URL is; the tunnel's requests carry the
	/// public host) and not forwarded by the tunnel (<c>cloudflared</c> connects over loopback too, and Cloudflare adds <c>Cf-Connecting-IP</c>
	/// to every request it forwards).
	/// </summary>
	public static bool IsLocal(HttpContext context) =>
		context.Connection.RemoteIpAddress is { } ip
		&& IPAddress.IsLoopback(ip)
		&& context.Request.Host.Host == "127.0.0.1"
		&& !context.Request.Headers.ContainsKey("Cf-Connecting-IP");

	/// <summary>Whether the Access check is skipped: exactly <see cref="Path"/> from this machine (the endpoint checks its own token).</summary>
	public static bool SkipsAccess(HttpContext context) => context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase) && IsLocal(context);

	private static async Task GateAsync(HttpContext context, RequestDelegate next)
	{
		if (!context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase))
		{
			await next(context);
			return;
		}

		var runId = IsLocal(context) ? context.RequestServices.GetRequiredService<PermissionBroker>().FindRun(BearerToken(context)) : null;
		if (runId is null)
		{
			context.Response.StatusCode = StatusCodes.Status404NotFound;
			return;
		}

		context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(RunClaim, runId)], "aicp-approval"));
		await next(context);
	}

	private static string? BearerToken(HttpContext context)
	{
		const string scheme = "Bearer ";
		string? header = context.Request.Headers.Authorization;
		return header is not null && header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
	}

	private static async ValueTask<CallToolResult> CallAsync(RequestContext<CallToolRequestParams> request, CancellationToken ct)
	{
		if (request.Params?.Name != ToolName)
		{
			throw new McpProtocolException($"Unknown tool '{request.Params?.Name}'.", McpErrorCode.InvalidParams);
		}

		var arguments = request.Params.Arguments;
		var tool = arguments?.TryGetValue("tool_name", out var name) == true && name.ValueKind == JsonValueKind.String ? name.GetString()! : string.Empty;
		var input = arguments?.TryGetValue("input", out var value) == true ? JsonNode.Parse(value.GetRawText()) : null;
		input ??= new JsonObject();
		var runId = request.User?.FindFirst(RunClaim)?.Value;
		var services = request.Services!;
		var broker = services.GetRequiredService<PermissionBroker>();
		bool allowed;

		// The server stopping denies at once (the host would otherwise wait for this request until its shutdown timeout).
		using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping))
		{
			allowed = runId is not null && tool.Length > 0 && await broker.RequestAsync(runId, tool, input, stop.Token);
		}

		var text = allowed ? new JsonObject { ["behavior"] = "allow", ["updatedInput"] = input }.ToJsonString() : Deny;
		return new CallToolResult { Content = [new TextContentBlock { Text = text }] };
	}
}
