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

/// <summary>Edits <c>&lt;DataDir&gt;\appsettings.json</c> with the Server's own validation rules; other keys in the file (e.g. <c>Tunnel:Token</c>) are kept.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
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
			settings = SettingsFile.Load(dataDir);
		}
		catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
		{
			Errors = [$"Could not read {dataDir.SettingsFile}: {ex.Message} Saving replaces the file."];
		}

		TeamDomain = SettingsFile.Read(settings, CloudflareAccessOptions.Section, nameof(CloudflareAccessOptions.TeamDomain)) ?? string.Empty;
		Audience = SettingsFile.Read(settings, CloudflareAccessOptions.Section, nameof(CloudflareAccessOptions.Audience)) ?? string.Empty;
		PublicHost = SettingsFile.Read(settings, ServerOptions.Section, nameof(ServerOptions.PublicHost)) ?? string.Empty;
		Port = SettingsFile.Read(settings, ServerOptions.Section, nameof(ServerOptions.Port)) ?? ServerOptions.DefaultPort.ToString(CultureInfo.InvariantCulture);
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

	/// <summary>
	/// Variables of <paramref name="environment"/> that win over <c>appsettings.json</c> (env vars come later in the Server's configuration),
	/// typically left over from running the Server from source.
	/// </summary>
	public static IReadOnlyList<string> OverridingVariables(IDictionary environment) =>
		environment.Keys.Cast<string>()
			.Where(name => name.StartsWith("CloudflareAccess__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Server__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Serilog__", StringComparison.OrdinalIgnoreCase)
				|| name.StartsWith("Tunnel__", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("AICP_DATA_DIR", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("ASPNETCORE_ENVIRONMENT", StringComparison.OrdinalIgnoreCase)
				|| name.Equals("DOTNET_ENVIRONMENT", StringComparison.OrdinalIgnoreCase))
			.Order(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <summary>"Open UI": through the tunnel when a public host is set (local requests carry no Access token), else loopback.</summary>
	public static Uri UiAddress(DataDirectory dataDir)
	{
		var settings = SettingsFile.LoadOrEmpty(dataDir);
		var publicHost = SettingsFile.Read(settings, ServerOptions.Section, nameof(ServerOptions.PublicHost));
		return string.IsNullOrWhiteSpace(publicHost)
			? new Uri($"http://127.0.0.1:{SettingsFile.ReadPort(settings).ToString(CultureInfo.InvariantCulture)}/")
			: new Uri($"https://{publicHost}/");
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
			SettingsFile.Update(_dataDir, settings =>
			{
				var access = SettingsFile.Section(settings, CloudflareAccessOptions.Section);
				access[nameof(CloudflareAccessOptions.TeamDomain)] = TeamDomain.Trim();
				access[nameof(CloudflareAccessOptions.Audience)] = Audience.Trim();
				var server = SettingsFile.Section(settings, ServerOptions.Section);
				server[nameof(ServerOptions.Port)] = ParsePort();
				server[nameof(ServerOptions.PublicHost)] = PublicHost.Trim();
			});
			_autoStart.IsEnabled = StartWithWindows;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
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
