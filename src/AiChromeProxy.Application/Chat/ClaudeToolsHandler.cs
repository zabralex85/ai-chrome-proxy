using System.Text.Json;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Chat;
using AiChromeProxy.Domain.Sync;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// <c>agent.tools.get</c> and <c>agent.tools.check</c> (registered once per type, see <see cref="Types"/>); both reply <c>agent.tools</c>: the stored
/// snapshot merged with the project's switches (<see cref="ClaudeToolsMerge"/>). A check runs the <see cref="IClaudeToolsProbe"/> off the hub invocation
/// (it may take a minute, and the connection's other messages must not wait) and sends the reply with the request's correlation id when done; a successful
/// check replaces the snapshot, a failed one keeps it and sets <c>error</c>. One check per repo at a time: a check asked while one runs gets that one's result.
/// </summary>
public sealed class ClaudeToolsHandler(string type, IProjectStore projects, IMirrorStore mirror, IClaudeToolsProbe probe, TimeProvider time, ILogger<ClaudeToolsHandler> logger) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.AgentToolsGet, MessageTypes.AgentToolsCheck];

	// Only the check instance uses it (one instance per type).
	private readonly Dictionary<string, Task<ClaudeToolsPayload>> _checks = new(StringComparer.OrdinalIgnoreCase);

	public string Type => type;

	public Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var repo = RepoName.Sanitize(Read(request).Repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs the folder name in 'repo'.");
		if (type == MessageTypes.AgentToolsGet)
		{
			return Task.FromResult<Envelope?>(Envelope.Create(MessageTypes.AgentTools, Merge(repo, projects.GetToolsSnapshot(repo), null), request.CorrelationId));
		}

		var folder = mirror.RepoFolder(repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, "Open the folder and let it sync first.");
		Task<ClaudeToolsPayload> check;
		lock (_checks)
		{
			if (!_checks.TryGetValue(repo, out check!))
			{
				// Started on the pool: its end removes it under the lock, so never before it was added.
				check = Task.Run(() => CheckAsync(repo, folder));
				_checks[repo] = check;
			}
		}

		_ = ReplyAsync(check, request, context);
		return Task.FromResult<Envelope?>(null);
	}

	private static ClaudeToolsRequest Read(Envelope request)
	{
		try
		{
			return request.Payload.Deserialize<ClaudeToolsRequest>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid {request.Type} payload.");
		}
	}

	private ClaudeToolsPayload Merge(string repo, ClaudeToolsSnapshot? snapshot, string? error) =>
		ClaudeToolsMerge.Merge(repo, snapshot, projects.GetSettings(repo), error);

	private async Task<ClaudeToolsPayload> CheckAsync(string repo, string folder)
	{
		try
		{
			var (snapshot, error) = await probe.CheckAsync(folder);
			if (snapshot is null)
			{
				return Merge(repo, projects.GetToolsSnapshot(repo), error);
			}

			snapshot = snapshot with { CheckedAt = time.GetUtcNow() };
			projects.SaveToolsSnapshot(repo, snapshot);
			return Merge(repo, snapshot, null);
		}
		finally
		{
			lock (_checks)
			{
				_checks.Remove(repo);
			}
		}
	}

	private async Task ReplyAsync(Task<ClaudeToolsPayload> check, Envelope request, EnvelopeContext context)
	{
		Envelope reply;
		try
		{
			reply = Envelope.Create(MessageTypes.AgentTools, await check, request.CorrelationId);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Claude tools check failed");
			reply = EnvelopeRouter.Error(request, new ErrorPayload(ErrorCodes.Internal));
		}

		try
		{
			await context.SendAsync(reply, CancellationToken.None);
		}
		catch (Exception ex)
		{
			// The connection is gone: nothing to tell.
			logger.LogDebug(ex, "Claude tools reply to {ConnectionId} failed", context.ConnectionId);
		}
	}
}
