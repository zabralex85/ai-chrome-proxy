using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary><c>&lt;DataDir&gt;\appsettings.json</c> as a JSON object: the Settings window and the remote access wizard read and write it the same way, keeping keys they do not know.</summary>
public static class SettingsFile
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	// Comments and trailing commas as the Server's JSON configuration accepts them; a duplicate key is broken there too.
	private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, AllowDuplicateProperties = false };

	/// <summary>The settings file as a JSON object (empty when it does not exist yet).</summary>
	/// <exception cref="JsonException">The file is not a JSON object, or has a duplicate key.</exception>
	public static JsonObject Load(DataDirectory dataDir)
	{
		if (!File.Exists(dataDir.SettingsFile))
		{
			return [];
		}

		return JsonNode.Parse(File.ReadAllText(dataDir.SettingsFile), documentOptions: Lenient) switch
		{
			JsonObject settings => settings,
			null => [],
			_ => throw new JsonException("The root of the file is not a JSON object."),
		};
	}

	/// <summary>As <see cref="Load"/>, but a broken file counts as empty (saving replaces it).</summary>
	public static JsonObject LoadOrEmpty(DataDirectory dataDir)
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

	/// <summary><paramref name="section"/>:<paramref name="key"/> as text (a number as its digits, like the Server's configuration reads it); null when missing or not a value under an object.</summary>
	public static string? Read(JsonObject settings, string section, string key) =>
		settings[section] is JsonObject values && values[key] is JsonValue value ? value.ToString() : null;

	/// <summary><c>Server:Port</c> when it is a port number (1-65535), else the default.</summary>
	public static int ReadPort(JsonObject settings) =>
		int.TryParse(Read(settings, ServerOptions.Section, nameof(ServerOptions.Port)), NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is >= 1 and <= 65535
			? port
			: ServerOptions.DefaultPort;

	/// <summary>The object under <paramref name="name"/>, created (or replacing a non-object value) when missing.</summary>
	public static JsonObject Section(JsonObject settings, string name)
	{
		if (settings[name] is JsonObject section)
		{
			return section;
		}

		section = [];
		settings[name] = section;
		return section;
	}

	/// <summary>
	/// Reads the file (a broken one counts as empty), lets <paramref name="change"/> set values, then writes it atomically: a temporary file, then a replace.
	/// It holds the tunnel token, so the folder is made safe first (<see cref="ServiceSetup.PrepareSettingsDirectory"/>) and the temporary file is
	/// created with its own protected DACL: the replaced file is never readable by other users, whatever the old one allowed.
	/// </summary>
	/// <exception cref="IOException">The folder is unsafe or the file could not be written; the message names it. The old file is left as it was.</exception>
	public static void Update(DataDirectory dataDir, Action<JsonObject> change)
	{
		var settings = LoadOrEmpty(dataDir);
		change(settings);

		var user = WindowsIdentity.GetCurrent().User!;
		try
		{
			ServiceSetup.PrepareSettingsDirectory(dataDir, user, DataDirectoryGuard.OwnerOf);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
		{
			throw CannotWrite(dataDir, ex);
		}

		var tmpPath = dataDir.SettingsFile + ".tmp";
		try
		{
			// The DACL of an existing file would be kept: start from a new one.
			File.Delete(tmpPath);
			var security = new FileSecurity();
			security.SetSecurityDescriptorSddlForm(ServiceSetup.SettingsFileDacl(user), AccessControlSections.Access);
			using (var writer = new StreamWriter(new FileInfo(tmpPath).Create(FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.None, security)))
			{
				writer.Write(settings.ToJsonString(Indented));
			}

			File.Move(tmpPath, dataDir.SettingsFile, overwrite: true);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			try
			{
				File.Delete(tmpPath);
			}
			catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
			{
				// Best effort; the original failure is what the user needs to see.
			}

			throw CannotWrite(dataDir, ex);
		}
	}

	private static IOException CannotWrite(DataDirectory dataDir, Exception ex) => new($"Could not write {dataDir.SettingsFile}: {ex.Message}", ex);
}
