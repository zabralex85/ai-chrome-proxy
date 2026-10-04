using System.Globalization;
using System.Text;

namespace AiChromeProxy.Client.Chat;

/// <summary>Default names and extensions for saved or downloaded diagrams.</summary>
public static class DiagramFiles
{
	/// <summary>The folder a save starts in.</summary>
	public const string DefaultFolder = "docs/diagrams/";

	private const int MaxSlug = 60;
	private const int TitleLines = 20;

	/// <summary>The extension (with the dot) of a kind.</summary>
	public static string Extension(DiagramFileKind kind) => kind switch
	{
		DiagramFileKind.Source => ".mmd",
		DiagramFileKind.Svg => ".svg",
		_ => ".png",
	};

	/// <summary>The file name without folder and extension: the slugified title, else the type slug and the local time.</summary>
	public static string DefaultName(string source, DateTime localNow)
	{
		var lines = (source ?? string.Empty).Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
		var title = FindTitle(lines);
		return title is not null
			? Slug(title)
			: Slug(TypeSlug(FindType(lines)) + "-" + localNow.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture));
	}

	/// <summary>The default path in <see cref="DefaultFolder"/>.</summary>
	public static string DefaultPath(string source, DiagramFileKind kind, DateTime localNow) => DefaultFolder + DefaultName(source, localNow) + Extension(kind);

	/// <summary>The path with the extension of <paramref name="kind"/>: another extension in the last segment is replaced, a missing one added.</summary>
	public static string WithExtension(string path, DiagramFileKind kind)
	{
		var trimmed = path.Trim();
		var dot = trimmed.LastIndexOf('.');
		var name = trimmed.LastIndexOf('/') + 1;
		return (dot > name ? trimmed[..dot] : trimmed) + Extension(kind);
	}

	private static string? FindTitle(string[] lines)
	{
		var front = lines.Length > 0 && lines[0].Trim() == "---";
		for (var i = front ? 1 : 0; i < lines.Length && i < TitleLines; i++)
		{
			var line = lines[i].Trim();
			if (front && line == "---")
			{
				front = false;
				continue;
			}

			var prefix = front ? "title:" : "title ";
			if (line.StartsWith(prefix, StringComparison.Ordinal))
			{
				var value = line[prefix.Length..].Trim().Trim('"', '\'').Trim();
				if (value.Length > 0)
				{
					return value;
				}
			}
		}

		return null;
	}

	private static string FindType(string[] lines)
	{
		var front = lines.Length > 0 && lines[0].Trim() == "---";
		foreach (var raw in lines.Skip(front ? 1 : 0))
		{
			var line = raw.Trim();
			if (front)
			{
				front = line != "---";
				continue;
			}

			if (line.Length > 0 && !line.StartsWith("%%", StringComparison.Ordinal))
			{
				return line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[0];
			}
		}

		return string.Empty;
	}

	private static string TypeSlug(string keyword) => keyword switch
	{
		"classDiagram" => "class-diagram",
		"flowchart" or "graph" => "flowchart",
		"sequenceDiagram" => "sequence-diagram",
		"stateDiagram" or "stateDiagram-v2" => "state-diagram",
		"erDiagram" => "er-diagram",
		"gitGraph" => "git-graph",
		_ => keyword,
	};

	private static string Slug(string text)
	{
		var sb = new StringBuilder();
		foreach (var c in text.ToLowerInvariant())
		{
			if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
			{
				sb.Append(c);
			}
			else if (sb.Length > 0 && sb[^1] != '-')
			{
				sb.Append('-');
			}
		}

		var slug = sb.ToString().Trim('-');
		if (slug.Length > MaxSlug)
		{
			slug = slug[..MaxSlug].TrimEnd('-');
		}

		return slug.Length == 0 ? "diagram" : slug;
	}
}
