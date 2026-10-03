using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Tray.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Edits <c>&lt;DataDir&gt;\appsettings.json</c> with the Server's own validation rules; other keys in the file are kept.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	private readonly DataDirectory _dataDir;
	private readonly IAutoStart _autoStart;
	private readonly IServiceControl _service;

	public SettingsViewModel(DataDirectory dataDir, IAutoStart autoStart, IServiceControl service)
	{
		_dataDir = dataDir;
		_autoStart = autoStart;
		_service = service;

		var settings = new JsonObject();
		try
		{
			settings = Load(dataDir);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [$"Could not read {dataDir.SettingsFile}: {ex.Message} Saving replaces the file."];
		}

		TeamDomain = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.TeamDomain)] ?? string.Empty;
		Audience = (string?)settings[CloudflareAccessOptions.Section]?[nameof(CloudflareAccessOptions.Audience)] ?? string.Empty;
		PublicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)] ?? string.Empty;
		Port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		StartWithWindows = autoStart.IsEnabled;

		var overriding = OverridingVariables(Environment.GetEnvironmentVariables());
		EnvironmentWarning = overriding.Count == 0
			? null
			: $"Environment variables override these settings if the service sees them: {string.Join(", ", overriding)}. Remove them.";
	}

	[ObservableProperty]
	public partial string TeamDomain { get; set; }

	[ObservableProperty]
	public partial string Audience { get; set; }

	[ObservableProperty]
	public partial string PublicHost { get; set; }

	[ObservableProperty]
	public partial string Port { get; set; }

	[ObservableProperty]
	public partial bool StartWithWindows { get; set; }

	/// <summary>One line naming the variables from <see cref="OverridingVariables"/>; null when there are none.</summary>
	public string? EnvironmentWarning { get; }

	[ObservableProperty]
	public partial IReadOnlyList<string> Errors { get; private set; } = [];

	[ObservableProperty]
	public partial string? Status { get; private set; }

	[ObservableProperty]
	[NotifyCanExecuteChangedFor(nameof(RestartServiceCommand))]
	public partial bool IsRestartOffered { get; private set; }

	/// <summary>The settings file as a JSON object (empty when it does not exist yet).</summary>
	public static JsonObject Load(DataDirectory dataDir) =>
		File.Exists(dataDir.SettingsFile) ? JsonNode.Parse(File.ReadAllText(dataDir.SettingsFile))?.AsObject() ?? [] : [];

	/// <summary>
	/// Variables of <paramref name="environment"/> that win over <c>appsettings.json</c> (env vars come later in the Server's configuration),
	/// typically left over from running the Server from source.
	/// </summary>
	public static IReadOnlyList<string> OverridingVariables(IDictionary environment) =>
		environment.Keys.Cast<string>()
			.Where(name => name.StartsWith("CloudflareAccess__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Server__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Serilog__", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("AICP_DATA_DIR", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <summary>"Open UI": through the tunnel when a public host is set (local requests carry no Access token), else loopback.</summary>
	public static Uri UiAddress(DataDirectory dataDir)
	{
		var settings = LoadOrEmpty(dataDir);
		var publicHost = (string?)settings[ServerOptions.Section]?[nameof(ServerOptions.PublicHost)];
		var port = settings[ServerOptions.Section]?[nameof(ServerOptions.Port)]?.ToString() ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
		return string.IsNullOrWhiteSpace(publicHost) ? new Uri($"http://127.0.0.1:{port}/") : new Uri($"https://{publicHost}/");
	}

	/// <returns>The messages the Server would fail with for these values (empty when valid).</returns>
	public IReadOnlyList<string> Validate()
	{
		var errors = new List<string>();
		if (ParsePort() is null)
		{
			errors.Add("Server:Port must be a number from 1 to 65535.");
		}

		if (new ServerOptions { PublicHost = PublicHost.Trim() }.GetError(isDevelopment: false) is { } serverError)
		{
			errors.Add(serverError);
		}

		if (new CloudflareAccessOptions { TeamDomain = TeamDomain.Trim(), Audience = Audience.Trim() }.GetError(isDevelopment: false) is { } accessError)
		{
			errors.Add(accessError);
		}

		return errors;
	}

	/// <summary>A broken file counts as empty: the user was told on open that saving replaces it.</summary>
	private static JsonObject LoadOrEmpty(DataDirectory dataDir)
	{
		try
		{
			return Load(dataDir);
		}
		catch (JsonException)
		{
			return [];
		}
	}

	private static JsonObject Section(JsonObject settings, string name)
	{
		if (settings[name] is JsonObject section)
		{
			return section;
		}

		section = [];
		settings[name] = section;
		return section;
	}

	[RelayCommand]
	private void Save()
	{
		Status = null;
		IsRestartOffered = false;
		Errors = Validate();
		if (Errors.Count > 0)
		{
			return;
		}

		try
		{
			var settings = LoadOrEmpty(_dataDir);
			var access = Section(settings, CloudflareAccessOptions.Section);
			access[nameof(CloudflareAccessOptions.TeamDomain)] = TeamDomain.Trim();
			access[nameof(CloudflareAccessOptions.Audience)] = Audience.Trim();
			var server = Section(settings, ServerOptions.Section);
			server[nameof(ServerOptions.Port)] = ParsePort();
			server[nameof(ServerOptions.PublicHost)] = PublicHost.Trim();

			Directory.CreateDirectory(_dataDir.Root);
			var tmpPath = _dataDir.SettingsFile + ".tmp";
			try
			{
				File.WriteAllText(tmpPath, settings.ToJsonString(Indented));
				File.Move(tmpPath, _dataDir.SettingsFile, overwrite: true);
			}
			catch (Exception ex)
			{
				try
				{
					File.Delete(tmpPath);
				}
				catch
				{
					// Best effort cleanup; let the original exception be thrown
				}

				if (ex is IOException or UnauthorizedAccessException)
				{
					throw new IOException($"Could not write {_dataDir.SettingsFile}: {ex.Message}", ex);
				}

				throw;
			}

			_autoStart.IsEnabled = StartWithWindows;
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [ex.Message];
			return;
		}

		IsRestartOffered = _service.GetState() is ServiceState.Running or ServiceState.Starting;
		Status = IsRestartOffered ? "Saved. Restart the service to apply." : "Saved. Applied when the service starts.";
	}

	[RelayCommand(CanExecute = nameof(IsRestartOffered))]
	private async Task RestartServiceAsync()
	{
		try
		{
			await _service.StopAsync(CancellationToken.None);
			await _service.StartAsync(CancellationToken.None);
			IsRestartOffered = false;
			Status = "Service restarted.";
		}
		catch (Exception ex)
		{
			Errors = [ex.Message];
		}
	}

	private int? ParsePort() =>
		int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535 ? port : null;
}
