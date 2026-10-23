using System.Text.Json;
using AiChromeProxy.Domain;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Tests.Domain;

public sealed class ProjectsContractTests
{
	private static readonly DateTimeOffset At = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

	[Fact]
	public void Payloads_AreCamelCase()
	{
		Assert.Equal("""{"repos":[{"name":"r","lastActivity":"2026-10-23T12:00:00+00:00","running":true,"open":false}],"truncated":false}""", Json(new ReposPayload([new RepoInfo("r", At, true, false)], false)));
		Assert.Equal("""{"name":"r","lastActivity":null,"running":false,"open":true}""", Json(new RepoInfo("r", null, false, true)));
		Assert.Equal("""{"repo":"r","after":null}""", Json(new MirrorListPayload("r")));
		Assert.Equal("""{"repo":"r","files":[{"path":"a/b.cs","size":3,"modified":"2026-10-23T12:00:00+00:00"}],"next":"a/b.cs"}""", Json(new MirrorFilesPayload("r", [new MirrorFile("a/b.cs", 3, At)], "a/b.cs")));
		Assert.Equal("""{"repo":"r","path":"a.txt","offset":16384}""", Json(new MirrorReadPayload("r", "a.txt", 16384)));
	}

	[Fact]
	public void Types()
	{
		Assert.Equal("repos.list", MessageTypes.ReposList);
		Assert.Equal("repos", MessageTypes.Repos);
		Assert.Equal("mirror.list", MessageTypes.MirrorList);
		Assert.Equal("mirror.files", MessageTypes.MirrorFiles);
		Assert.Equal("mirror.read", MessageTypes.MirrorRead);
		Assert.Equal("mirror.data", MessageTypes.MirrorData);
	}

	private static string Json<T>(T value) => JsonSerializer.Serialize(value, JsonSerializerOptions.Web);
}
