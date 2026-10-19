using System.Text.Json;
using AiChromeProxy.Domain.Packages;

namespace AiChromeProxy.Client.Packages;

/// <summary>Reads the packages a project restored from its <c>obj/project.assets.json</c>.</summary>
public static class ProjectAssets
{
	/// <summary>
	/// The <c>type: "package"</c> entries of <c>libraries</c> (key <c>Id/Version</c>), lower-cased and distinct; project references are ignored,
	/// and so are keys that are not a valid NuGet id and version (NuGet cannot have restored them).
	/// </summary>
	/// <exception cref="FormatException">Not a <c>project.assets.json</c>.</exception>
	public static IReadOnlyList<PackageRef> Parse(string json)
	{
		try
		{
			using (var document = JsonDocument.Parse(json))
			{
				var root = document.RootElement;
				if (root.ValueKind != JsonValueKind.Object)
				{
					throw new FormatException("project.assets.json is not an object.");
				}

				if (!root.TryGetProperty("libraries", out var libraries))
				{
					return [];
				}

				if (libraries.ValueKind != JsonValueKind.Object)
				{
					throw new FormatException("project.assets.json libraries is not an object.");
				}

				return [.. libraries.EnumerateObject()
					.Where(l => l.Value.ValueKind == JsonValueKind.Object && l.Value.TryGetProperty("type", out var type) && type.ValueEquals("package"))
					.Select(l => l.Name.ToLowerInvariant().Split('/'))
					.Where(parts => parts is [var id, var version] && NuGetNames.IsValidId(id) && NuGetNames.IsValidVersion(version))
					.Select(parts => new PackageRef(parts[0], parts[1]))
					.Distinct()];
			}
		}
		catch (JsonException ex)
		{
			throw new FormatException("project.assets.json is not valid JSON.", ex);
		}
	}
}
