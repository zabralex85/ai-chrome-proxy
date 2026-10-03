using System.Text.Json;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Tray.Services;

/// <summary><c>&lt;DataDir&gt;\appsettings.json</c> as a JSON object: the Settings window and the remote access wizard read and write it the same way, keeping keys they do not know.</summary>
public static class SettingsFile
{
	private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

	/// <summary>The settings file as a JSON object (empty when it does not exist yet).</summary>
	/// <exception cref="JsonException">The file is not a JSON object.</exception>
	public static JsonObject Load(DataDirectory dataDir)
	{
		if (!File.Exists(dataDir.SettingsFile))
		{
			return [];
		}

		return JsonNode.Parse(File.ReadAllText(dataDir.SettingsFile)) switch
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

	/// <summary>Reads the file (a broken one counts as empty), lets <paramref name="change"/> set values, then writes it atomically: a temporary file, then a replace.</summary>
	/// <exception cref="IOException">The file could not be written; the message names it. The old file is left as it was.</exception>
	public static void Update(DataDirectory dataDir, Action<JsonObject> change)
	{
		var settings = LoadOrEmpty(dataDir);
		change(settings);

		Directory.CreateDirectory(dataDir.Root);
		var tmpPath = dataDir.SettingsFile + ".tmp";
		try
		{
			File.WriteAllText(tmpPath, settings.ToJsonString(Indented));
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

			throw new IOException($"Could not write {dataDir.SettingsFile}: {ex.Message}", ex);
		}
	}
}
