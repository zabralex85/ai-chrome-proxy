namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Machine-wide folder shared by the service and the tray: persistent settings, CLEF logs and the service binaries.</summary>
public sealed record DataDirectory(string Root)
{
	/// <summary>Environment variable overriding the default <c>%ProgramData%\AiChromeProxy</c> (tests and local runs only; the elevated install ignores it).</summary>
	public const string OverrideVariable = "AICP_DATA_DIR";

	public string SettingsFile => Path.Combine(Root, "appsettings.json");

	public string Logs => Path.Combine(Root, "logs");

	/// <summary>Default <c>Mirror:Root</c> when the Server runs with a data directory.</summary>
	public string Mirror => Path.Combine(Root, "mirror");

	/// <summary>The service's copy of the Server (synced from the tray's <c>current\server</c>), outside the app folder so <c>Setup.exe</c> can replace it.</summary>
	public string Server => Path.Combine(Root, "server");

	/// <summary>The default folder, or the override made absolute (a relative value would otherwise depend on the working directory).</summary>
	public static DataDirectory Resolve(string? overrideValue) => new(string.IsNullOrWhiteSpace(overrideValue)
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AiChromeProxy")
		: Path.GetFullPath(overrideValue));

	public static DataDirectory FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));
}
