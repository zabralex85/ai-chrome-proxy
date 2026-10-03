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

	/// <summary>The allow-always rule for a request: <c>Bash(&lt;command&gt;)</c> for a shell command, the tool name otherwise; null for a shell call without a command.</summary>
	public static string? Rule(string tool, JsonNode? input)
	{
		if (tool != "Bash")
		{
			return tool;
		}

		return input is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var command) ? $"Bash({command})" : null;
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
		if (rule is not null && Allowed(run.Repo).Contains(rule, StringComparer.Ordinal))
		{
			return true;
		}

		var requestId = Guid.NewGuid().ToString("N");
		var pending = new Pending(rule);
		lock (run)
		{
			if (run.Closed)
			{
				return false;
			}

			run.Requests[requestId] = pending;
		}

		var summary = StreamJsonParser.Summarize(tool, input, run.Folder);
		if (!Publish(run, new ChatEvent(string.Empty, run.Id, 0, ChatEventKinds.Permission, Name: tool, Summary: summary, RequestId: requestId)))
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

		if (decision == ChatDecisions.AllowAlways && pending.Rule is not null)
		{
			AddRule(run, pending.Rule);
		}

		Publish(run, new ChatEvent(string.Empty, run.Id, 0, ChatEventKinds.PermissionResolved, RequestId: requestId, Decision: decision));
		pending.Decision.TrySetResult(decision);
		return true;
	}

	private void AddRule(RunState run, string rule)
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
		}
		catch (Exception ex)
		{
			// Allowed this time anyway; the user can add the rule in the settings.
			_logger.LogError(ex, "Chat run {RunId}: saving an allow-always rule failed", run.Id);
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
