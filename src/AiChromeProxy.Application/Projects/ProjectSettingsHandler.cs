using System.Text.Json;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Projects;

/// <summary>
/// <c>project.settings.get</c> and <c>project.settings.set</c> (registered once per type, see <see cref="Types"/>); both reply
/// <c>project.settings</c>. A set makes every session with the repo re-check the mirror, since the excludes decide which files are pushed.
/// </summary>
public sealed class ProjectSettingsHandler(string type, IProjectStore projects, SyncSessions sessions) : IEnvelopeHandler
{
	/// <summary>The longest <see cref="ProjectSettings.Excludes"/> accepted, in characters.</summary>
	public const int MaxExcludesLength = 16_000;

	public static readonly IReadOnlyList<string> Types = [MessageTypes.ProjectSettingsGet, MessageTypes.ProjectSettingsSet];

	public string Type => type;

	public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var payload = Read(request);
		var repo = RepoName.Sanitize(payload.Repo) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs the folder name in 'repo'.");
		ProjectSettings settings;
		if (type == MessageTypes.ProjectSettingsSet)
		{
			var requested = payload.Settings ?? throw new EnvelopeException(ErrorCodes.BadRequest, "project.settings.set needs 'settings'.");
			if (requested.Excludes is { Length: > MaxExcludesLength })
			{
				throw new EnvelopeException(ErrorCodes.TooLarge, $"'excludes' may have at most {MaxExcludesLength} characters.");
			}

			settings = projects.SaveSettings(repo, requested);
			await sessions.RecheckAsync(repo);
		}
		else
		{
			settings = projects.GetSettings(repo);
		}

		return Envelope.Create(MessageTypes.ProjectSettings, new ProjectSettingsPayload(repo, settings), request.CorrelationId);
	}

	private static ProjectSettingsPayload Read(Envelope request)
	{
		try
		{
			return request.Payload.Deserialize<ProjectSettingsPayload>(JsonSerializerOptions.Web) ?? throw new EnvelopeException(ErrorCodes.BadRequest, $"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw new EnvelopeException(ErrorCodes.BadRequest, $"Invalid {request.Type} payload.");
		}
	}
}
