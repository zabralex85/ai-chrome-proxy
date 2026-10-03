using System.Globalization;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;

namespace AiChromeProxy.Client.Shell;

/// <summary>Texts the shell shows; culture-independent so the UI reads the same everywhere.</summary>
public static class Format
{
	/// <summary>Thousands separated by a no-break space: "1 234".</summary>
	private static readonly NumberFormatInfo Grouped = new() { NumberGroupSeparator = " ", NumberDecimalSeparator = "." };

	public static string Count(long value) => value.ToString("#,0", Grouped);

	public static string Bytes(long bytes) => bytes switch
	{
		< 1024 => $"{bytes} B",
		< 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.#} KB"),
		< 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MB"),
		_ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024 * 1024):0.#} GB"),
	};

	public static string Connection(TransportState state) => state switch
	{
		TransportState.Connected => "Connected",
		TransportState.Connecting => "Connecting…",
		TransportState.Reconnecting => "Reconnecting…",
		_ => "Offline",
	};

	/// <summary>"just now", "42 s ago", "5 min ago", or the time of day for anything older than an hour.</summary>
	public static string Ago(DateTimeOffset? at, DateTimeOffset now)
	{
		if (at is not { } when)
		{
			return "never";
		}

		var age = now - when;
		return age.TotalSeconds switch
		{
			< 5 => "just now",
			< 60 => $"{(int)age.TotalSeconds} s ago",
			< 3600 => $"{(int)age.TotalMinutes} min ago",
			_ => when.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
		};
	}

	/// <summary>The sync status box text, e.g. "Synced 1 234 files · Rescan in 7 s" or "Uploading 12/80".</summary>
	public static string SyncStatus(FolderStatus folder, SyncPhase phase, int synced, int uploadDone, int uploadTotal, TimeSpan? untilNextScan)
	{
		if (folder == FolderStatus.None)
		{
			return "No folder open";
		}

		if (folder == FolderStatus.NeedsPermission)
		{
			return "Access needed";
		}

		return phase switch
		{
			SyncPhase.Scanning => "Scanning…",
			SyncPhase.Uploading => $"Uploading {uploadDone}/{uploadTotal}",
			SyncPhase.Failed => "Sync failed",
			SyncPhase.Synced when untilNextScan is { } wait => $"Synced {Count(synced)} files · Rescan in {Math.Max(0, (int)Math.Ceiling(wait.TotalSeconds))} s",
			SyncPhase.Synced => $"Synced {Count(synced)} files",
			_ => "Waiting for the server…",
		};
	}

	/// <summary>What the collapsed file tree hides that needs the user: "Access needed", "3 errors", or null when nothing does.</summary>
	public static string? Attention(FolderStatus folder, int errors) => folder == FolderStatus.NeedsPermission
		? "Access needed"
		: errors switch
		{
			0 => null,
			1 => "1 error",
			_ => $"{Count(errors)} errors",
		};

	/// <summary>Icon kind for a file name (drives the icon colour): code, web, data, doc, image or file.</summary>
	public static string FileKind(string name) => Path.GetExtension(name).ToLowerInvariant() switch
	{
		".cs" or ".razor" or ".fs" or ".vb" or ".js" or ".mjs" or ".ts" or ".tsx" or ".jsx" or ".py" or ".go" or ".rs" or ".java" or ".kt" or ".dart" or ".c" or ".cpp" or ".h" or ".ps1" or ".sh" or ".sql" => "code",
		".html" or ".htm" or ".css" or ".scss" or ".xaml" or ".axaml" or ".svg" => "web",
		".json" or ".xml" or ".yml" or ".yaml" or ".toml" or ".csproj" or ".props" or ".targets" or ".slnx" or ".sln" or ".config" or ".ini" => "data",
		".md" or ".txt" or ".rst" or ".pdf" => "doc",
		".png" or ".jpg" or ".jpeg" or ".gif" or ".ico" or ".webp" or ".bmp" => "image",
		_ => "file",
	};
}
