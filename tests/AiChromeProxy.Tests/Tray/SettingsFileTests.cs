using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsFileTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	private string TmpPath => _dataDir.SettingsFile + ".tmp";

	[Fact]
	public void Load_NoFile_Empty()
	{
		Assert.Empty(SettingsFile.Load(_dataDir));
	}

	[Fact]
	public void Load_BrokenFile_Throws_LoadOrEmpty_Empty()
	{
		WriteFile("{ not json");

		Assert.ThrowsAny<JsonException>(() => SettingsFile.Load(_dataDir));
		Assert.Empty(SettingsFile.LoadOrEmpty(_dataDir));
	}

	[Fact]
	public void Load_DuplicateKeys_ThrowsJsonException_LoadOrEmpty_Empty()
	{
		WriteFile("""{ "Server": {}, "Server": { "Port": 6000 } }""");

		Assert.ThrowsAny<JsonException>(() => SettingsFile.Load(_dataDir));
		Assert.Empty(SettingsFile.LoadOrEmpty(_dataDir));
	}

	[Fact]
	public void Load_CommentsAndTrailingCommas_LikeTheServer()
	{
		WriteFile("""
			{
				// written by hand
				"Server": { "Port": 6000, },
			}
			""");

		Assert.Equal("6000", SettingsFile.Read(SettingsFile.Load(_dataDir), "Server", "Port"));
	}

	[Theory]
	[InlineData("[]")]
	[InlineData("5")]
	[InlineData("\"x\"")]
	public void Load_RootNotAnObject_ThrowsJsonException_LoadOrEmpty_Empty(string json)
	{
		WriteFile(json);

		Assert.ThrowsAny<JsonException>(() => SettingsFile.Load(_dataDir));
		Assert.Empty(SettingsFile.LoadOrEmpty(_dataDir));
	}

	[Theory]
	[InlineData("""{ "Server": { "PublicHost": "code.example.com" } }""", "code.example.com")]
	[InlineData("""{ "Server": { "Port": 6000 } }""", null)]
	[InlineData("""{ "Server": "x" }""", null)]
	[InlineData("""{ "Server": [1] }""", null)]
	[InlineData("""{ "Server": { "PublicHost": 5 } }""", "5")]
	[InlineData("""{ "Server": { "PublicHost": { "a": 1 } } }""", null)]
	public void Read_ValueAsText_NullForMissingOrWrongShape(string json, string? expected)
	{
		Assert.Equal(expected, SettingsFile.Read(JsonNode.Parse(json)!.AsObject(), "Server", "PublicHost"));
	}

	[Fact]
	public void Section_ExistingKept_MissingOrNotAnObjectCreated()
	{
		var settings = JsonNode.Parse("""{ "Server": { "Port": 1 }, "Tunnel": "x" }""")!.AsObject();

		Assert.Equal(1, (int?)SettingsFile.Section(settings, "Server")["Port"]);
		SettingsFile.Section(settings, "Tunnel")["Token"] = "t";
		SettingsFile.Section(settings, "CloudflareAccess")["Audience"] = "a";

		Assert.True(JsonNode.DeepEquals(
			JsonNode.Parse("""{ "Server": { "Port": 1 }, "Tunnel": { "Token": "t" }, "CloudflareAccess": { "Audience": "a" } }"""),
			settings));
	}

	[Fact]
	public void Update_MergesIntoExistingFile_KeepsUnknownKeys()
	{
		WriteFile("""{ "Serilog": { "MinimumLevel": "Debug" }, "Server": { "Port": 6000, "Extra": true } }""");

		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Server")["PublicHost"] = "code.example.com");

		Assert.True(JsonNode.DeepEquals(
			JsonNode.Parse("""{ "Serilog": { "MinimumLevel": "Debug" }, "Server": { "Port": 6000, "Extra": true, "PublicHost": "code.example.com" } }"""),
			ReadFile()));
	}

	[Fact]
	public void Update_NoFolderYet_CreatesItAndAnIndentedFile()
	{
		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Tunnel")["Token"] = "t");

		Assert.Equal("t", (string?)ReadFile()["Tunnel"]?["Token"]);
		Assert.Contains(Environment.NewLine + "  ", File.ReadAllText(_dataDir.SettingsFile), StringComparison.Ordinal);
	}

	[Fact]
	public void Update_BrokenFile_Replaced()
	{
		WriteFile("{ not json");

		SettingsFile.Update(_dataDir, s => SettingsFile.Section(s, "Server")["Port"] = 5180);

		Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{ "Server": { "Port": 5180 } }"""), ReadFile()));
	}

	[Fact]
	public void Update_StaleTemporaryFileFromPreviousCrash_RemovedAfterSuccessfulWrite()
	{
		WriteFile("{}");
		File.WriteAllText(TmpPath, "{ stale tmp from crash }");

		SettingsFile.Update(_dataDir, s => s["A"] = 1);

		Assert.False(File.Exists(TmpPath));
		Assert.Equal(1, (int?)ReadFile()["A"]);
	}

	[Fact]
	public void Update_SuccessiveWrites_NoTemporaryFileLeftBehind()
	{
		SettingsFile.Update(_dataDir, s => s["A"] = 1);
		Assert.False(File.Exists(TmpPath));

		SettingsFile.Update(_dataDir, s => s["A"] = 2);

		Assert.False(File.Exists(TmpPath));
		Assert.Equal(2, (int?)ReadFile()["A"]);
	}

	[Fact]
	public void Update_FileNotWritable_IOExceptionNamesTheFile_NoTemporaryFileLeft()
	{
		Directory.CreateDirectory(_dataDir.SettingsFile);

		var ex = Assert.Throws<IOException>(() => SettingsFile.Update(_dataDir, s => s["A"] = 1));

		Assert.StartsWith($"Could not write {_dataDir.SettingsFile}: ", ex.Message, StringComparison.Ordinal);
		Assert.False(File.Exists(TmpPath));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private void WriteFile(string json)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, json);
	}

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;
}
