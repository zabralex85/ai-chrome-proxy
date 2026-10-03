using System.Text.Json;
using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Domain;

public sealed class BackChannelPayloadTests
{
	[Fact]
	public void RemoteChange_IsCamelCase_NullsKept()
	{
		var json = JsonSerializer.Serialize(new SyncRemotePayload("r", [new RemoteChange("a.txt", null, 0, "abc")]), JsonSerializerOptions.Web);

		Assert.Equal("""{"repo":"r","changes":[{"path":"a.txt","sha256":null,"size":0,"base":"abc"}]}""", json);
	}

	[Fact]
	public void ProjectSettings_KeepsUnknownKeys()
	{
		var settings = JsonSerializer.Deserialize<ProjectSettings>("""{"excludes":"x/","model":"opus"}""", JsonSerializerOptions.Web)!;

		Assert.Equal("x/", settings.Excludes);
		Assert.True(settings.ApplyServerChangesOrDefault);
		Assert.Contains("\"model\":\"opus\"", JsonSerializer.Serialize(settings, JsonSerializerOptions.Web), StringComparison.Ordinal);
	}

	[Fact]
	public void SyncOpened_WithoutSettings_StaysCompatible()
	{
		Assert.Equal("""{"repo":"r","settings":null}""", JsonSerializer.Serialize(new SyncOpenPayload("r"), JsonSerializerOptions.Web));
		Assert.Null(JsonSerializer.Deserialize<SyncOpenPayload>("""{"repo":"r"}""", JsonSerializerOptions.Web)!.Settings);
	}

	[Fact]
	public void Split_RespectsEntryAndByteLimits()
	{
		var pages = SyncPages.Split(Enumerable.Range(0, 1_200), _ => 100).ToList();

		Assert.All(pages, p => Assert.True(p.Count <= SyncLimits.MaxPageEntries && p.Count * 100 <= SyncLimits.MaxPageBytes));
		Assert.Equal(1_200, pages.Sum(p => p.Count));
	}
}
