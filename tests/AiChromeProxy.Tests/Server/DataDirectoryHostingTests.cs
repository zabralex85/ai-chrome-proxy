using System.Text.Json;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Server.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiChromeProxy.Tests.Server;

public sealed class DataDirectoryHostingTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void Select_NotServiceNoOverride_NoDataDirectory()
	{
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: null));
		Assert.Null(DataDirectoryHosting.Select(isWindowsService: false, overrideValue: " "));
	}

	[Fact]
	public void Select_Service_ProgramData()
	{
		Assert.Equal(DataDirectory.Resolve(null), DataDirectoryHosting.Select(isWindowsService: true, overrideValue: null));
	}

	[Theory]
	[InlineData(false)]
	[InlineData(true)]
	public void Select_Override_WinsInBothModes(bool isService)
	{
		Assert.Equal(@"D:\aicp", DataDirectoryHosting.Select(isService, @"D:\aicp")?.Root);
	}

	[Fact]
	public void PersistentSettings_OverrideAppSettings_EnvVarsOverrideThem()
	{
		var key = "AicpTests_" + Guid.NewGuid().ToString("N");
		WriteSettings(new { Server = new { Port = 6100 }, AicpTests = new Dictionary<string, string> { [key] = "file", [key + "_FileOnly"] = "file" } });
		Environment.SetEnvironmentVariable($"AicpTests__{key}", "env");
		try
		{
			var builder = WebApplication.CreateBuilder();

			builder.Configuration.AddPersistentSettings(_dataDir);

			Assert.Equal("6100", builder.Configuration["Server:Port"]);
			Assert.Equal("env", builder.Configuration[$"AicpTests:{key}"]);
			Assert.Equal("file", builder.Configuration[$"AicpTests:{key}_FileOnly"]);
		}
		finally
		{
			Environment.SetEnvironmentVariable($"AicpTests__{key}", null);
		}
	}

	[Fact]
	public void PersistentSettings_MissingFileAndFolder_Ignored()
	{
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("5180", config["Server:Port"]);
	}

	[Fact]
	public void PersistentSettings_NoEnvironmentSource_Appended()
	{
		WriteSettings(new { Server = new { Port = 6200 } });
		var config = new ConfigurationManager();
		config.AddInMemoryCollection(new Dictionary<string, string?> { ["Server:Port"] = "5180" });

		config.AddPersistentSettings(_dataDir);

		Assert.Equal("6200", config["Server:Port"]);
	}

	[Fact]
	public void Logging_WithDataDirectory_WritesDailyClefFile()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, _dataDir).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("Hello {Name}", "clef");
		}

		var file = Assert.Single(Directory.GetFiles(_dataDir.Logs));
		Assert.Matches(@"server-\d{8}\.clef$", file);
		using (var json = JsonDocument.Parse(File.ReadAllLines(file).Single()))
		{
			Assert.Equal("Hello \"clef\"", json.RootElement.GetProperty("@m").GetString());
			Assert.Equal("Warning", json.RootElement.GetProperty("@l").GetString());
			Assert.Equal("clef", json.RootElement.GetProperty("Name").GetString());

			// Disposing the container closed the file: nothing holds it open, so the folder can be deleted.
			File.Open(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose();
		}
	}

	[Fact]
	public void Logging_WithoutDataDirectory_NoFiles()
	{
		var config = new ConfigurationBuilder().Build();
		using (var provider = new ServiceCollection().AddServerLogging(config, dataDir: null).BuildServiceProvider())
		{
			provider.GetRequiredService<ILoggerFactory>().CreateLogger("Test").LogWarning("console only");
		}

		Assert.False(Directory.Exists(_dataDir.Root));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private void WriteSettings(object settings)
	{
		Directory.CreateDirectory(_dataDir.Root);
		File.WriteAllText(_dataDir.SettingsFile, JsonSerializer.Serialize(settings));
	}
}
