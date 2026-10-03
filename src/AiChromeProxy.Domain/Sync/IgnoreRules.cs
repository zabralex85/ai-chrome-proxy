using System.Text;
using System.Text.RegularExpressions;

namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// Which files never leave the machine: the built-in excludes (secrets, VCS and build folders) plus the root <c>.gitignore</c>
/// (common subset: <c>#</c> comments, blank lines, <c>*</c>, <c>**</c>, <c>?</c>, trailing <c>/</c>, leading <c>/</c>, <c>!</c>;
/// <c>[...]</c> classes and backslash escapes are not supported).
/// Matching ignores case (the source machine is usually Windows). A <c>.gitignore</c> negation cannot re-include a built-in exclude.
/// </summary>
public sealed class IgnoreRules
{
	/// <summary>Directory names the folder walk skips without descending (cheap pre-filter; <see cref="IsIgnored"/> covers them too).</summary>
	public static readonly IReadOnlyList<string> BuiltInDirectories = [".git", "node_modules", "bin", "obj", ".vs", ".idea"];

	public static readonly IReadOnlyList<string> BuiltInPatterns =
		[".git", "node_modules/", "bin/", "obj/", ".vs/", ".idea/", ".env", ".env.*", "*.pfx", "*.key", "*.pem", "id_rsa*"];

	private static readonly IReadOnlyList<Rule> BuiltIn = Parse(BuiltInPatterns);

	private readonly IReadOnlyList<Rule> _gitignore;

	// Verdict per directory prefix: an excluded directory short-circuits its children. Not thread-safe (one scan, one thread).
	private readonly Dictionary<string, bool> _directories = new(StringComparer.OrdinalIgnoreCase);

	private IgnoreRules(IReadOnlyList<Rule> gitignore)
	{
		_gitignore = gitignore;
		SkipDirectories = [.. BuiltInDirectories, .. SkippableDirectories(gitignore)];
	}

	/// <summary>
	/// Directories the folder walk does not enter: a name skips that directory at any depth, <c>/a/b</c> only that path. The built-in ones,
	/// plus the <c>.gitignore</c> rules without wildcards (<c>name</c>, <c>name/</c>, <c>/path/</c>, <c>**/name/</c>) that no later
	/// <c>!</c> rule could re-include; nothing under an excluded directory can be re-included (git semantics), so skipping it changes nothing
	/// but the walk's cost. Other rules (wildcards) are still applied to every file by <see cref="IsIgnored"/>.
	/// </summary>
	public IReadOnlyList<string> SkipDirectories { get; }

	/// <param name="gitignore">Content of the root <c>.gitignore</c>, or null when there is none.</param>
	/// <param name="excludes">Extra patterns in <c>.gitignore</c> syntax, applied after the <c>.gitignore</c> (a <c>!</c> there cannot re-include a built-in), or null.</param>
	public static IgnoreRules Create(string? gitignore, string? excludes = null) =>
		new([.. ParseText(gitignore), .. ParseText(excludes)]);

	/// <param name="path">A file path relative to the picked folder, <c>/</c>-separated (<c>\</c> is accepted too).</param>
	/// <returns>True when the file or one of its parent directories is excluded.</returns>
	public bool IsIgnored(string path)
	{
		if (path.Contains('\\'))
		{
			path = path.Replace('\\', '/');
		}

		var lookup = _directories.GetAlternateLookup<ReadOnlySpan<char>>();
		for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
		{
			if (slash == 0)
			{
				continue;
			}

			var prefix = path.AsSpan(0, slash);
			if (!lookup.TryGetValue(prefix, out var ignored))
			{
				var text = prefix.ToString();
				ignored = Matches(BuiltIn, text, true) || Matches(_gitignore, text, true);
				_directories[text] = ignored;
			}

			if (ignored)
			{
				return true;
			}
		}

		return Matches(BuiltIn, path, false) || Matches(_gitignore, path, false);
	}

	/// <summary>Git semantics: the last matching rule decides; a <c>!</c> rule re-includes.</summary>
	private static bool Matches(IReadOnlyList<Rule> rules, string path, bool isDirectory)
	{
		var ignored = false;
		foreach (var rule in rules)
		{
			if ((!rule.DirectoryOnly || isDirectory) && rule.IsMatch(path))
			{
				ignored = !rule.Negate;
			}
		}

		return ignored;
	}

	/// <summary>Literal exclude rules that no later negation could override for a directory (a literal rule only matches names equal to its last segment).</summary>
	private static IEnumerable<string> SkippableDirectories(IReadOnlyList<Rule> rules)
	{
		static string LastSegment(string literal) => literal[(literal.LastIndexOf('/') + 1)..];

		for (var i = 0; i < rules.Count; i++)
		{
			if (rules[i] is not { Negate: false, Pattern: null, Suffix: false, Literal: { } literal } rule)
			{
				continue;
			}

			var name = LastSegment(literal);
			var overridden = rules.Skip(i + 1).Any(n => n.Negate && (n.Literal is null || n.Suffix || LastSegment(n.Literal).Equals(name, StringComparison.OrdinalIgnoreCase)));
			if (!overridden)
			{
				yield return rule.Anchored ? "/" + literal : literal;
			}
		}
	}

	private static List<Rule> ParseText(string? text) =>
		text is null ? [] : Parse(text.TrimStart((char)0xFEFF).Split('\n'));

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

			// "**/name" is "name" at any depth.
			if (line.StartsWith("**/", StringComparison.Ordinal) && line.AsSpan(3).IndexOfAny('*', '?', '/') < 0)
			{
				line = line[3..];
			}

			// A slash at the start or in the middle anchors the pattern to the root; otherwise it matches at any depth.
			var anchored = line.Contains('/');
			line = line.TrimStart('/');
			if (line.Length == 0)
			{
				continue;
			}

			if (line.AsSpan().IndexOfAny('*', '?') < 0)
			{
				rules.Add(new Rule(null, line, false, anchored, negate, directoryOnly));
			}
			else if (!anchored && line[0] == '*' && line.AsSpan(1).IndexOfAny('*', '?') < 0)
			{
				// "*.ext": the name ends with the literal.
				rules.Add(new Rule(null, line[1..], true, false, negate, directoryOnly));
			}
			else
			{
				// NonBacktracking: linear time whatever the pattern (a .gitignore is untrusted input).
				var regex = (anchored ? "^" : "^(?:.*/)?") + GlobToRegex(line) + "$";
				var options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
				rules.Add(new Rule(new Regex(regex, options), null, false, anchored, negate, directoryOnly));
			}
		}

		return rules;
	}

	private static string GlobToRegex(string glob)
	{
		while (glob.Contains("**/**/", StringComparison.Ordinal))
		{
			glob = glob.Replace("**/**/", "**/", StringComparison.Ordinal);
		}

		var sb = new StringBuilder();
		for (var i = 0; i < glob.Length; i++)
		{
			var c = glob[i];
			if (c == '*')
			{
				var end = i;
				while (end + 1 < glob.Length && glob[end + 1] == '*')
				{
					end++;
				}

				var atStart = i == 0 || glob[i - 1] == '/';
				if (end > i && atStart && end + 1 == glob.Length)
				{
					// Trailing "**": everything below.
					sb.Append(".*");
				}
				else if (end > i && atStart && glob[end + 1] == '/')
				{
					// "**/": zero or more directories.
					sb.Append("(?:.*/)?");
					end++;
				}
				else
				{
					// A run of stars collapses to one.
					sb.Append("[^/]*");
				}

				i = end;
				continue;
			}

			sb.Append(c == '?' ? "[^/]" : Regex.Escape(c.ToString()));
		}

		return sb.ToString();
	}

	private sealed record Rule(Regex? Pattern, string? Literal, bool Suffix, bool Anchored, bool Negate, bool DirectoryOnly)
	{
		public bool IsMatch(string path)
		{
			if (Pattern is not null)
			{
				return Pattern.IsMatch(path);
			}

			var literal = Literal!;
			if (Suffix)
			{
				return path.EndsWith(literal, StringComparison.OrdinalIgnoreCase);
			}

			if (path.Equals(literal, StringComparison.OrdinalIgnoreCase))
			{
				return true;
			}

			return !Anchored
				&& path.Length > literal.Length
				&& path[path.Length - literal.Length - 1] == '/'
				&& path.EndsWith(literal, StringComparison.OrdinalIgnoreCase);
		}
	}
}
