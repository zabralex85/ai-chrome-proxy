using System.Text.Json;
using AiChromeProxy.Application.Projects;
using AiChromeProxy.Application.Sync;
using AiChromeProxy.Application.Transport;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Sync;
using AiChromeProxy.Infrastructure.Sync;
using AiChromeProxy.Tests.Server;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Application;

/// <summary><c>project.settings.get/set</c> and the recheck a set triggers on the sessions of the repo.</summary>
public sealed class ProjectSettingsHandlerTests : IDisposable
{
	private readonly string _root = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));
	private readonly MemoryProjectStore _projects = new();
	private readonly List<Envelope> _pushed = [];
	private readonly SyncSessions _sessions;
	private readonly ProjectSettingsHandler _get;
	private readonly ProjectSettingsHandler _set;

	public ProjectSettingsHandlerTests()
	{
		Directory.CreateDirectory(Path.Combine(_root, "My_Repo"));
		_sessions = new SyncSessions(
			new FileSystemMirrorStore(Options.Create(new MirrorOptions { Root = _root })),
			_projects,
			new ListLogger<SyncSession>(),
			TimeProvider.System,
			new FakeMirrorWatcher());
		_get = new ProjectSettingsHandler(MessageTypes.ProjectSettingsGet, _projects, _sessions);
		_set = new ProjectSettingsHandler(MessageTypes.ProjectSettingsSet, _projects, _sessions);
	}

	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	private EnvelopeContext Context => new("c1", null, (e, _) =>
	{
		_pushed.Add(e);
		return Task.CompletedTask;
	});

	public void Dispose()
	{
		_sessions.Close("c1");
		Directory.Delete(_root, recursive: true);
	}

	[Fact]
	public void Types_AreGetAndSet()
	{
		Assert.Equal(MessageTypes.ProjectSettingsGet, _get.Type);
		Assert.Equal(MessageTypes.ProjectSettingsSet, _set.Type);
	}

	[Fact]
	public async Task Get_UnknownRepo_Defaults()
	{
		var reply = await _get.HandleAsync(Envelope.Create(MessageTypes.ProjectSettingsGet, new ProjectSettingsPayload("nobody"), "c9"), Context, Ct);

		Assert.NotNull(reply);
		Assert.Equal(MessageTypes.ProjectSettings, reply.Type);
		Assert.Equal("c9", reply.CorrelationId);
		var payload = Read(reply);
		Assert.Equal("nobody", payload.Repo);
		Assert.Null(payload.Settings!.Excludes);
		Assert.True(payload.Settings.ApplyServerChangesOrDefault);
	}

	[Fact]
	public async Task Set_ThenGet_RoundTrip_SanitizedRepo()
	{
		var settings = new ProjectSettings { Excludes = "tmp/", ApplyServerChanges = false };

		var set = await _set.HandleAsync(Envelope.Create(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload("My Repo", settings)), Context, Ct);
		var get = await _get.HandleAsync(Envelope.Create(MessageTypes.ProjectSettingsGet, new ProjectSettingsPayload("My Repo")), Context, Ct);

		Assert.Equal("My_Repo", Read(set!).Repo);
		Assert.Equal(MessageTypes.ProjectSettings, set!.Type);
		Assert.Equal(settings, Read(get!).Settings);
		Assert.Equal(settings, _projects.GetSettings("My_Repo"));
	}

	[Fact]
	public async Task Set_WithoutSettings_BadRequest()
	{
		await AssertError(_set, ErrorCodes.BadRequest, Envelope.Create(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload("repo")));
	}

	[Theory]
	[InlineData("null")]
	[InlineData("\"text\"")]
	[InlineData("{}")]
	[InlineData("{\"repo\":\"  \"}")]
	public async Task MalformedPayloadOrNoRepo_BadRequest(string json)
	{
		await AssertError(_get, ErrorCodes.BadRequest, new Envelope(MessageTypes.ProjectSettingsGet, JsonDocument.Parse(json).RootElement.Clone()));
	}

	[Fact]
	public async Task Set_HugeExcludes_TooLarge()
	{
		var settings = new ProjectSettings { Excludes = new string('a', 16_001) };

		await AssertError(_set, ErrorCodes.TooLarge, Envelope.Create(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload("repo", settings)));

		Assert.Equal(ProjectSettings.Default, _projects.GetSettings("repo"));
	}

	[Fact]
	public async Task Set_RechecksSessionsOfTheRepo_PushesNowUnexcludedFile()
	{
		_projects.SetBaselined("My_Repo");
		_projects.SaveSettings("My_Repo", new ProjectSettings { Excludes = "tmp/" });
		Directory.CreateDirectory(Path.Combine(_root, "My_Repo", "tmp"));
		await File.WriteAllTextAsync(Path.Combine(_root, "My_Repo", "tmp", "t.txt"), "server", Ct);
		await _sessions.Get("c1").HandleAsync(Envelope.Create(MessageTypes.SyncOpen, new SyncOpenPayload("My_Repo")), Context, Ct);
		await _sessions.RecheckAsync("My_Repo");
		Assert.Empty(_pushed);

		await _set.HandleAsync(Envelope.Create(MessageTypes.ProjectSettingsSet, new ProjectSettingsPayload("My_Repo", new ProjectSettings { Excludes = "other/" })), Context, Ct);

		var push = Assert.Single(_pushed);
		Assert.Equal(MessageTypes.SyncRemote, push.Type);
		Assert.Equal("tmp/t.txt", Assert.Single(push.Payload.Deserialize<SyncRemotePayload>(JsonSerializerOptions.Web)!.Changes).Path);
	}

	private static ProjectSettingsPayload Read(Envelope envelope) => envelope.Payload.Deserialize<ProjectSettingsPayload>(JsonSerializerOptions.Web)!;

	private async Task AssertError(ProjectSettingsHandler handler, string code, Envelope request)
	{
		var ex = await Assert.ThrowsAsync<EnvelopeException>(() => handler.HandleAsync(request, Context, Ct));
		Assert.Equal(code, ex.Code);
	}
}
