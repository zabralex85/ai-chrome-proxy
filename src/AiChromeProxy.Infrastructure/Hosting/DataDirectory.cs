namespace AiChromeProxy.Infrastructure.Hosting;

/// <summary>Machine-wide folder shared by the service and the tray: persistent settings and CLEF logs.</summary>
public sealed record DataDirectory(string Root)
{
	/// <summary>Environment variable overriding the default <c>%ProgramData%\AiChromeProxy</c> (tests, local runs).</summary>
	public const string OverrideVariable = "AICP_DATA_DIR";

	public string SettingsFile => Path.Combine(Root, "appsettings.json");

	public string Logs => Path.Combine(Root, "logs");

	public static DataDirectory Resolve(string? overrideValue) => new(string.IsNullOrWhiteSpace(overrideValue)
		? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AiChromeProxy")
		: overrideValue);

	public static DataDirectory FromEnvironment() => Resolve(Environment.GetEnvironmentVariable(OverrideVariable));
}
