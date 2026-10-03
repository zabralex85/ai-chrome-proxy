using System.Text;
using System.Text.RegularExpressions;

namespace AiChromeProxy.Client.Sync;

/// <summary>
/// Which files never leave the machine: the built-in excludes (secrets, VCS and build folders) plus the root <c>.gitignore</c>
/// (common subset: <c>#</c> comments, blank lines, <c>*</c>, <c>**</c>, <c>?</c>, trailing <c>/</c>, leading <c>/</c>, <c>!</c>).
/// Matching ignores case (the source machine is usually Windows). A <c>.gitignore</c> negation cannot re-include a built-in exclude.
/// </summary>
public sealed class IgnoreRules
{
	/// <summary>Directory names the folder walk skips without descending (cheap pre-filter; <see cref="IsIgnored"/> covers them too).</summary>
	public static readonly IReadOnlyList<string> BuiltInDirectories = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

	public static readonly IReadOnlyList<string> BuiltInPatterns =
		[".git/", "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".env", ".env.*", "*.pfx", "*.key", "*.pem", "id_rsa*"];

	private static readonly IReadOnlyList<Rule> BuiltIn = Parse(BuiltInPatterns);

	private readonly IReadOnlyList<Rule> _gitignore;

	private IgnoreRules(IReadOnlyList<Rule> gitignore) => _gitignore = gitignore;

	/// <param name="gitignore">Content of the root <c>.gitignore</c>, or null when there is none.</param>
	public static IgnoreRules Create(string? gitignore) =>
		new(gitignore is null ? [] : Parse(gitignore.Split('\n')));

	/// <param name="path">A file path relative to the picked folder, <c>/</c>-separated.</param>
	/// <returns>True when the file or one of its parent directories is excluded.</returns>
	public bool IsIgnored(string path)
	{
		var segments = path.Split('/');
		for (var i = 1; i <= segments.Length; i++)
		{
			var prefix = string.Join('/', segments, 0, i);
			var isDirectory = i < segments.Length;
			if (Matches(BuiltIn, prefix, isDirectory) || Matches(_gitignore, prefix, isDirectory))
			{
				return true;
			}
		}

		return false;
	}

	/// <summary>Git semantics: the last matching rule decides; a <c>!</c> rule re-includes.</summary>
	private static bool Matches(IReadOnlyList<Rule> rules, string path, bool isDirectory)
	{
		var ignored = false;
		foreach (var rule in rules)
		{
			if ((!rule.DirectoryOnly || isDirectory) && rule.Pattern.IsMatch(path))
			{
				ignored = !rule.Negate;
			}
		}

		return ignored;
	}

	private static List<Rule> Parse(IEnumerable<string> lines)
	{
		var rules = new List<Rule>();
		foreach (var raw in lines)
		{
			var line = raw.TrimEnd('\r', ' ', '\t');
			if (line.Length == 0 || line.StartsWith('#'))
			{
				continue;
			}

			var negate = line.StartsWith('!');
			if (negate)
			{
				line = line[1..];
			}

			var directoryOnly = line.EndsWith('/');
			line = line.TrimEnd('/');

			// A slash at the start or in the middle anchors the pattern to the root; otherwise it matches at any depth.
			var anchored = line.Contains('/');
			line = line.TrimStart('/');
			if (line.Length == 0)
			{
				continue;
			}

			var regex = (anchored ? "^" : "^(?:.*/)?") + GlobToRegex(line) + "$";
			rules.Add(new Rule(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), negate, directoryOnly));
		}

		return rules;
	}

	private static string GlobToRegex(string glob)
	{
		var sb = new StringBuilder();
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			if (c == '*' && i + 1 < glob.Length && glob[i + 1] == '*' && (i == 0 || glob[i - 1] == '/'))
			{
				if (i + 2 == glob.Length)
				{
					// Trailing "**": everything below.
					sb.Append(".*");
					i++;
					continue;
				}

				if (glob[i + 2] == '/')
				{
					// "**/": zero or more directories.
					sb.Append("(?:.*/)?");
					i += 2;
					continue;
				}
			}

			sb.Append(c switch
			{
				'*' => "[^/]*",
				'?' => "[^/]",
				_ => Regex.Escape(c.ToString()),
			});
		}

		return sb.ToString();
	}

	private sealed record Rule(Regex Pattern, bool Negate, bool DirectoryOnly);
}
