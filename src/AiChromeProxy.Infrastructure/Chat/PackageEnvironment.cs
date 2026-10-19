namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>The environment that lets a run restore private NuGet packages from the server's packages folder without touching the project's files.</summary>
public static class PackageEnvironment
{
	public const string FallbackVariable = "NUGET_FALLBACK_PACKAGES";

	/// <summary>
	/// <paramref name="configured"/> (<c>Agent:Env</c>) plus, when <paramref name="nugetFolder"/> is set: the folder appended to <see cref="FallbackVariable"/>
	/// (configured value, else <paramref name="inheritedFallback"/>), <c>RestoreIgnoreFailedSources=true</c>, <c>NuGetAudit=false</c> and
	/// <c>WarningsNotAsErrors=NU1801</c>, each unless configured. Names compare ignoring case, like Windows does.
	/// </summary>
	public static IReadOnlyDictionary<string, string> With(IReadOnlyDictionary<string, string> configured, string? inheritedFallback, string? nugetFolder)
	{
		if (nugetFolder is null)
		{
			return configured;
		}

		var env = new Dictionary<string, string>(configured, StringComparer.OrdinalIgnoreCase);
		var current = env.TryGetValue(FallbackVariable, out var set) ? set : inheritedFallback;
		env[FallbackVariable] = string.IsNullOrWhiteSpace(current) ? nugetFolder : current.TrimEnd(';') + ";" + nugetFolder;
		env.TryAdd("RestoreIgnoreFailedSources", "true");
		env.TryAdd("NuGetAudit", "false");
		env.TryAdd("WarningsNotAsErrors", "NU1801");
		return env;
	}
}
