using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Approvals in the chat. Each run that may ask gets a secret token (<see cref="StartRun"/>) that identifies it to the approval endpoint;
/// a request (<see cref="RequestAsync"/>) publishes a <c>permission</c> event and waits for <see cref="Answer"/>. Every resolution publishes
/// <c>permissionResolved</c>; no answer within <see cref="AnswerTimeout"/>, a cancelled request or the run's end (<see cref="CancelRun"/>) deny.
/// <c>allowAlways</c> adds a rule (<see cref="Rule"/>) to the project's <c>agentAllowedTools</c>, so the same request is allowed at once for the
/// rest of the run (and passed as an allowed tool from the next run on).
/// </summary>
public sealed class PermissionBroker
{
	/// <summary>How long a request waits for an answer before it is denied.</summary>
	public static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(10);

	private readonly IProjectStore _projects;
	private readonly TimeProvider _time;
	private readonly ILogger<PermissionBroker> _logger;
	private readonly ConcurrentDictionary<string, RunState> _runs = new(StringComparer.Ordinal);

	// ponytail: one lock for every repo's read-modify-write of the settings; allow-always clicks are rare.
	private readonly Lock _rules = new();

	public PermissionBroker(IProjectStore projects, TimeProvider time, ILogger<PermissionBroker> logger)
	{
		_projects = projects;
		_time = time;
		_logger = logger;
	}

	/// <summary>
	/// The allow-always rule for a request: <c>Tool(&lt;command&gt;)</c> for <c>Bash</c> and <c>PowerShell</c>, the tool name for a tool
	/// without a <c>command</c>. Null (allow once, no rule) whenever the rule could match more than this request: a command with <c>*</c>
	/// (Claude Code reads it, and a trailing <c>:*</c>, as wildcards), a line break, parentheses that close the rule early or leave it open
	/// (the CLI splits <c>--allowedTools</c> on commas and spaces outside parentheses, so <c>Bash(a),Bash,(b)</c> would allow every
	/// command), or no command at all; another tool with a <c>command</c> (its bare name would allow every command); a tool name other than
	/// letters, digits and underscores.
	/// </summary>
	public static string? Rule(string tool, JsonNode? input)
	{
		if (tool.Length == 0 || !tool.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
		{
			return null;
		}

		if (tool is not ("Bash" or "PowerShell"))
		{
			return input is JsonObject i && i.ContainsKey("command") ? null : tool;
		}

		return input is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var command)
			&& command.Length > 0 && command.IndexOfAny(['*', '\n', '\r']) < 0 && StaysInside(command)
			? $"{tool}({command})"
			: null;
	}

	/// <summary>Lets the run ask; returns its token (sent as a bearer token by the agent, never logged).</summary>
	/// <param name="runId">The run.</param>
	/// <param name="repo">The repo whose settings get allow-always rules.</param>
	/// <param name="folder">The run's folder, so that summaries show paths relative to it.</param>
	/// <param name="publish">Stores and pushes an event of the run (<c>permission</c>, <c>permissionResolved</c>).</param>
	public string StartRun(string runId, string repo, string? folder, Action<ChatEvent> publish)
	{
		var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
		_runs[runId] = new RunState(runId, repo, folder, Encoding.UTF8.GetBytes(token), publish);
		return token;
	}

	/// <summary>The run whose token this is (compared in constant time), or null.</summary>
	public string? FindRun(string? token)
	{
		if (string.IsNullOrEmpty(token))
		{
			return null;
		}

		var bytes = Encoding.UTF8.GetBytes(token);
		string? found = null;
		foreach (var run in _runs.Values)
		{
			if (CryptographicOperations.FixedTimeEquals(bytes, run.Token))
			{
				found = run.Id;
			}
		}

		return found;
	}

	/// <summary>Asks the chat whether the run may use <paramref name="tool"/> with <paramref name="input"/>; true when allowed. Never throws.</summary>
	public async Task<bool> RequestAsync(string runId, string tool, JsonNode? input, CancellationToken ct)
	{
		if (!_runs.TryGetValue(runId, out var run))
		{
			return false;
		}

		var rule = Rule(tool, input);
		try
		{
			if (rule is not null && Allowed(run.Repo).Contains(rule, StringComparer.Ordinal))
			{
				return true;
			}
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId}: reading the allowed tools failed; denied", run.Id);
			return false;
		}

		var requestId = Guid.NewGuid().ToString("N");
		var pending = new Pending(rule);
		var summary = StreamJsonParser.Summarize(tool, input, run.Folder);
		bool published;

		// Registered and published under the run's lock: a run that ends meanwhile either never sees the request or denies it after its card.
		lock (run)
		{
			if (run.Closed)
			{
				return false;
			}

			run.Requests[requestId] = pending;
			published = Publish(run, new ChatEvent(string.Empty, run.Id, 0, ChatEventKinds.Permission, Name: tool, Summary: summary, RequestId: requestId));
		}

		if (!published)
		{
			// Nobody can see the question: deny now rather than after the timeout.
			Resolve(run, requestId, ChatDecisions.Deny);
		}

		using (var timeout = new CancellationTokenSource(AnswerTimeout, _time))
		{
			using (var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token))
			{
				using (stop.Token.Register(() => Resolve(run, requestId, ChatDecisions.Deny)))
				{
					return await pending.Decision.Task != ChatDecisions.Deny;
				}
			}
		}
	}

	/// <summary><c>chat.approve</c>: answers a pending request.</summary>
	/// <exception cref="EnvelopeException"><c>bad_request</c> for an unknown decision, <c>not_found</c> for an unknown run or request.</exception>
	public void Answer(string? runId, string? requestId, string? decision)
	{
		if (decision is not (ChatDecisions.Allow or ChatDecisions.AllowAlways or ChatDecisions.Deny))
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, "'decision' must be allow, allowAlways or deny.");
		}

		if (runId is null || requestId is null || !_runs.TryGetValue(runId, out var run) || !Resolve(run, requestId, decision))
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "No such permission request.");
		}
	}

	/// <summary>The run ended: its pending requests are denied (resolved before this returns) and its token stops working.</summary>
	public void CancelRun(string runId)
	{
		if (!_runs.TryRemove(runId, out var run))
		{
			return;
		}

		string[] pending;
		lock (run)
		{
			run.Closed = true;
			pending = [.. run.Requests.Keys];
		}

		foreach (var requestId in pending)
		{
			Resolve(run, requestId, ChatDecisions.Deny);
		}
	}

	/// <summary>Whether <c>Tool(</c> + <paramref name="command"/> + <c>)</c> keeps the rule's parenthesis open until its final <c>)</c>.</summary>
	private static bool StaysInside(string command)
	{
		var depth = 1;
		foreach (var c in command)
		{
			depth += c == '(' ? 1 : c == ')' ? -1 : 0;
			if (depth < 1)
			{
				return false;
			}
		}

		return depth == 1;
	}

	private IReadOnlyList<string> Allowed(string repo) => _projects.GetSettings(repo).AgentAllowedTools ?? [];

	/// <returns>False when the request was already resolved (or never existed).</returns>
	private bool Resolve(RunState run, string requestId, string decision)
	{
		Pending? pending;
		lock (run)
		{
			if (!run.Requests.Remove(requestId, out pending))
			{
				return false;
			}
		}

		// Allow always without a saved rule is reported as what it was: allowed once.
		if (decision == ChatDecisions.AllowAlways && (pending.Rule is null || !AddRule(run, pending.Rule)))
		{
			decision = ChatDecisions.Allow;
		}

		Publish(run, new ChatEvent(string.Empty, run.Id, 0, ChatEventKinds.PermissionResolved, RequestId: requestId, Decision: decision));
		pending.Decision.TrySetResult(decision);
		return true;
	}

	/// <returns>False when saving failed.</returns>
	private bool AddRule(RunState run, string rule)
	{
		try
		{
			lock (_rules)
			{
				var settings = _projects.GetSettings(run.Repo);
				var allowed = settings.AgentAllowedTools ?? [];
				if (!allowed.Contains(rule, StringComparer.Ordinal))
				{
					_projects.SaveSettings(run.Repo, settings with { AgentAllowedTools = [.. allowed, rule] });
				}
			}

			return true;
		}
		catch (Exception ex)
		{
			// Allowed this time anyway; the user can add the rule in the settings.
			_logger.LogError(ex, "Chat run {RunId}: saving an allow-always rule failed", run.Id);
			return false;
		}
	}

	private bool Publish(RunState run, ChatEvent e)
	{
		try
		{
			run.Publish(e);
			return true;
		}
		catch (Exception ex)
		{
			_logger.LogError(ex, "Chat run {RunId}: publishing {Kind} failed", run.Id, e.Kind);
			return false;
		}
	}

	private sealed record RunState(string Id, string Repo, string? Folder, byte[] Token, Action<ChatEvent> Publish)
	{
		public Dictionary<string, Pending> Requests { get; } = new(StringComparer.Ordinal);

		public bool Closed { get; set; }
	}

	private sealed class Pending(string? rule)
	{
		public string? Rule => rule;

		public TaskCompletionSource<string> Decision { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
