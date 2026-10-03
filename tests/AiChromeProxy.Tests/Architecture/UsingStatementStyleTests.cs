using System.Text.RegularExpressions;

namespace AiChromeProxy.Tests.Architecture;

public sealed class UsingStatementStyleTests
{
	private static readonly Regex UsingDeclaration = new(
		@"^\s*(await\s+)?using\s+(var\s+\w+|[\w.<>?,\s]+?\s+\w+)\s*=",
		RegexOptions.Compiled);

	private static readonly Regex UsingAlias = new(@"^\s*using\s+\w+\s*=\s*[\w.]+\s*;", RegexOptions.Compiled);

	[Fact]
	public void SourceFiles_UseBlockFormUsingStatements()
	{
		var root = FindRepoRoot();
		var offenders = new List<string>();

		foreach (var dir in new[] { "src", "tests", "benchmarks" })
		{
			var path = Path.Combine(root, dir);
			if (!Directory.Exists(path))
			{
				continue;
			}

			foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
			{
				var relative = Path.GetRelativePath(root, file);
				if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
				{
					continue;
				}

				var lines = File.ReadAllLines(file);
				for (var i = 0; i < lines.Length; i++)
				{
					var line = lines[i];
					if (UsingDeclaration.IsMatch(line) && !UsingAlias.IsMatch(line) && !IsStaticDirective(line))
					{
						offenders.Add($"{relative}:{i + 1}");
					}
				}
			}
		}

		Assert.True(offenders.Count == 0, "Use `using (...) { }` blocks, not using declarations:\n" + string.Join('\n', offenders));
	}

	private static bool IsStaticDirective(string line) => Regex.IsMatch(line, @"^\s*using\s+static\s");

	private static string FindRepoRoot()
	{
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
		{
			dir = dir.Parent;
		}

		return dir?.FullName ?? throw new InvalidOperationException("global.json not found above " + AppContext.BaseDirectory);
	}
}
