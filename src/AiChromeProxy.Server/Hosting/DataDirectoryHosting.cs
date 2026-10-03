using AiChromeProxy.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Serilog;
using Serilog.Formatting.Compact;

namespace AiChromeProxy.Server.Hosting;

/// <summary>Wires the machine data directory into the host: persistent settings and CLEF log files.</summary>
public static class DataDirectoryHosting
{
	public const string LogFilePattern = "server-.clef";
	public const int RetainedLogFiles = 14;

	/// <summary>
	/// The data directory is used only by the Windows service or when <c>AICP_DATA_DIR</c> is set,
	/// so a developer's machine config never leaks into <c>dotnet run</c> or tests.
	/// </summary>
	/// <returns>The directory, or null when the Server runs without one.</returns>
	public static DataDirectory? Select(bool isWindowsService, string? overrideValue) =>
		isWindowsService || !string.IsNullOrWhiteSpace(overrideValue) ? DataDirectory.Resolve(overrideValue) : null;

	/// <summary>Adds the optional <c>&lt;DataDir&gt;\appsettings.json</c> just below environment variables: env vars and the command line still win.</summary>
	public static void AddPersistentSettings(this IConfigurationBuilder config, DataDirectory dataDir)
	{
		var source = new JsonConfigurationSource { Path = dataDir.SettingsFile, Optional = true };
		source.ResolveFileProvider();
		var envIndex = config.Sources.ToList().FindLastIndex(s => s is EnvironmentVariablesConfigurationSource);
		config.Sources.Insert(envIndex < 0 ? config.Sources.Count : envIndex, source);
	}

	/// <summary>Serilog (levels from the <c>Serilog</c> section): console always; daily CLEF files (rendered @m, so the tray needs no template renderer) in <c>&lt;DataDir&gt;\logs</c> when a data directory is in use.</summary>
	public static IServiceCollection AddServerLogging(this IServiceCollection services, IConfiguration configuration, DataDirectory? dataDir)
	{
		var log = new LoggerConfiguration().ReadFrom.Configuration(configuration).Enrich.FromLogContext().WriteTo.Console();
		if (dataDir is not null)
		{
			log.WriteTo.File(
				new RenderedCompactJsonFormatter(),
				Path.Combine(dataDir.Logs, LogFilePattern),
				rollingInterval: RollingInterval.Day,
				retainedFileCountLimit: RetainedLogFiles);
		}

		// Owned by the container (dispose: true) and never assigned to the static Log.Logger: parallel test hosts stay isolated.
		return services.AddSerilog(log.CreateLogger(), dispose: true);
	}
}
