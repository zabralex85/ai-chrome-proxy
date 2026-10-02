using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Services;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class SettingsAtomicWriteTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));
	private readonly FakeAutoStart _autoStart = new();
	private readonly FakeServiceControl _service = new(ServiceState.Running);

	[Fact]
	public void Save_StaleTemporaryFileFromPreviousCrash_RemovedAfterSuccessfulSave()
	{
		Directory.CreateDirectory(_dataDir.Root);
		var tmpPath = _dataDir.SettingsFile + ".tmp";
		File.WriteAllText(tmpPath, "{ stale tmp from crash }");

		var vm = Valid(Create());
		vm.SaveCommand.Execute(null);

		Assert.False(File.Exists(tmpPath), "Stale .tmp file should be deleted after successful save");
		Assert.Empty(vm.Errors);
		Assert.Equal("code.example.com", (string?)ReadFile()["Server"]?["PublicHost"]);
	}

	[Fact]
	public void Save_MultipleSuccessiveSaves_NoTemporaryFilesLeftBehind()
	{
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);
		var tmpPath = _dataDir.SettingsFile + ".tmp";
		Assert.False(File.Exists(tmpPath), "No .tmp file after first save");

		vm.Port = "6001";
		vm.SaveCommand.Execute(null);
		Assert.False(File.Exists(tmpPath), "No .tmp file after second save");

		var settings = ReadFile();
		Assert.Equal(6001, (int?)settings["Server"]?["Port"]);
	}

	[Fact]
	public void Save_FileAtomicity_WritesCompleteContent()
	{
		var vm = Valid(Create());

		vm.SaveCommand.Execute(null);

		Assert.Empty(vm.Errors);
		var content = File.ReadAllText(_dataDir.SettingsFile);
		Assert.Contains("CloudflareAccess", content);
		Assert.Contains("Server", content);
		Assert.Contains("PublicHost", content);
		Assert.Contains("Port", content);
		Assert.Contains("TeamDomain", content);
		Assert.Contains("Audience", content);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static SettingsViewModel Valid(SettingsViewModel vm)
	{
		vm.TeamDomain = " team.cloudflareaccess.com ";
		vm.Audience = "aud";
		vm.PublicHost = "code.example.com";
		return vm;
	}

	private SettingsViewModel Create() => new(_dataDir, _autoStart, _service);

	private JsonNode ReadFile() => JsonNode.Parse(File.ReadAllText(_dataDir.SettingsFile))!;

	private sealed class FakeAutoStart : IAutoStart
	{
		public bool IsEnabled { get; set; }
	}
}
