using System.Text.Json;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Projects;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Application.Projects;

/// <summary>
/// The read-only view of the server's projects (registered once per type, see <see cref="Types"/>): <c>repos.list</c>, <c>mirror.list</c> and
/// <c>mirror.read</c>. Outside any sync session: nothing here writes the mirror or the database. Excluded paths (<see cref="MirrorRules"/>)
/// are neither listed nor served.
/// </summary>
public sealed class MirrorBrowseHandler(string type, IMirrorStore store, IProjectStore projects, IChatStore chats, ChatService chat, SyncSessions sessions) : IEnvelopeHandler
{
	public static readonly IReadOnlyList<string> Types = [MessageTypes.ReposList, MessageTypes.MirrorList, MessageTypes.MirrorRead];

	public string Type => type;

	/// <summary>Most recent activity first (none last), then by name; as many as fit <see cref="SyncLimits.MaxPageBytes"/>.</summary>
	public static ReposPayload ReposPage(IEnumerable<RepoInfo> repos)
	{
		var page = new List<RepoInfo>();
		var bytes = 0;
		foreach (var repo in repos.OrderByDescending(r => r.LastActivity ?? DateTimeOffset.MinValue).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
		{
			var size = JsonSerializer.SerializeToUtf8Bytes(repo, JsonSerializerOptions.Web).Length + 1;
			if (bytes + size > SyncLimits.MaxPageBytes)
			{
				return new ReposPayload(page, true);
			}

			page.Add(repo);
			bytes += size;
		}

		return new ReposPayload(page, false);
	}

	/// <summary>The files after <paramref name="after"/> in <paramref name="sorted"/> (ordinal by path), bounded by entries and serialized bytes.</summary>
	public static MirrorFilesPayload FilesPage(string repo, IReadOnlyList<MirrorFile> sorted, string? after)
	{
		var page = new List<MirrorFile>();
		var bytes = 0;
		foreach (var file in sorted.Where(f => after is null || string.CompareOrdinal(f.Path, after) > 0))
		{
			var size = JsonSerializer.SerializeToUtf8Bytes(file, JsonSerializerOptions.Web).Length + 1;
			if (page.Count == SyncLimits.MaxPageEntries || bytes + size > SyncLimits.MaxPageBytes)
			{
				return new MirrorFilesPayload(repo, page, page[^1].Path);
			}

			page.Add(file);
			bytes += size;
		}

		return new MirrorFilesPayload(repo, page, null);
	}

	public async Task<Envelope?> HandleAsync(Envelope request, EnvelopeContext context, CancellationToken ct)
	{
		var reply = type switch
		{
			MessageTypes.ReposList => Envelope.Create(MessageTypes.Repos, Repos()),
			MessageTypes.MirrorList => Envelope.Create(MessageTypes.MirrorFiles, await ListAsync(Read<MirrorListPayload>(request), ct)),
			_ => Envelope.Create(MessageTypes.MirrorData, await ReadAsync(Read<MirrorReadPayload>(request), ct)),
		};

		return reply with { CorrelationId = request.CorrelationId };
	}

	private static EnvelopeException BadRequest(string message) => new(ErrorCodes.BadRequest, message);

	private static string RepoOf(string? repo, string type) =>
		RepoName.Sanitize(repo) ?? throw BadRequest($"{type} needs the folder name in 'repo'.");

	private static T Read<T>(Envelope request)
		where T : class
	{
		try
		{
			return request.Payload.Deserialize<T>(JsonSerializerOptions.Web) ?? throw BadRequest($"{request.Type} needs a payload.");
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			throw BadRequest($"Invalid {request.Type} payload.");
		}
	}

	private ReposPayload Repos()
	{
		var activity = chats.LastActivity();
		return ReposPage(store.ListRepos().Select(name => new RepoInfo(
			name,
			activity.TryGetValue(name, out var at) ? at : null,
			chat.IsRunning(name),
			sessions.IsOpen(name))));
	}

	private async Task<MirrorFilesPayload> ListAsync(MirrorListPayload payload, CancellationToken ct)
	{
		var repo = RepoOf(payload.Repo, MessageTypes.MirrorList);
		if (store.RepoFolder(repo) is null)
		{
			throw new EnvelopeException(ErrorCodes.NotFound, "This project is no longer on the server.");
		}

		var rules = await MirrorRules.LoadAsync(store, projects, repo, ct);

		// ponytail: every page enumerates and sorts the whole repo again, O(pages x files) per refresh (every 30 s per viewing tab), and walks
		// ignored folders (node_modules, bin) before filtering them out. Upgrade path: prune IgnoreRules' skipped folders during the enumeration, or
		// cache the sorted list per refresh (keyed by repo, dropped after the last page or a timeout).
		var files = store.ListFileInfos(repo).Where(f => !rules.IsIgnored(f.Path)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
		return FilesPage(repo, files, payload.After);
	}

	/// <summary>Like <c>sync.fetch</c>, without a session.</summary>
	private async Task<SyncDataPayload> ReadAsync(MirrorReadPayload payload, CancellationToken ct)
	{
		var repo = RepoOf(payload.Repo, MessageTypes.MirrorRead);
		if (SyncPath.GetError(payload.Path) is { } error)
		{
			throw BadRequest(error);
		}

		if (payload.Offset < 0)
		{
			throw BadRequest("mirror.read needs an offset of 0 or more.");
		}

		var path = payload.Path;
		if ((await MirrorRules.LoadAsync(store, projects, repo, ct)).IsIgnored(path))
		{
			throw BadRequest($"'{path}' is excluded from sync.");
		}

		var data = await store.ReadAsync(repo, path, payload.Offset, SyncLimits.ChunkSize, ct)
			?? throw new EnvelopeException(ErrorCodes.NotFound, $"'{path}' is not on the server.");

		// A file ending exactly at a chunk boundary gets one more, empty, last chunk.
		var last = data.Length < SyncLimits.ChunkSize;
		var sha256 = last ? await store.GetHashAsync(repo, path, ct) : null;
		return new SyncDataPayload(repo, path, payload.Offset, SyncData.Encode(data), last, sha256);
	}
}
