using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AiChromeProxy.Tests.Architecture;

/// <summary>Browser code is strict TypeScript compiled by MSBuild (<c>src/*/Scripts</c> → <c>wwwroot/js</c>); no hand-written JavaScript.</summary>
public sealed class BrowserScriptTests
{
	private static readonly Regex AnyType = new(@":\s*any\b|\bas\s+any\b|<any>", RegexOptions.Compiled);

	[Fact]
	public void Wwwroot_HasNoTrackedJavaScript()
	{
		var tracked = Git(FindRepoRoot(), "ls-files", "--", "src")
			.Where(f => f.Contains("/wwwroot/", StringComparison.Ordinal) && f.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
			.ToList();

		Assert.True(tracked.Count == 0, "Compiled JavaScript is committed (write it in Scripts/*.ts and gitignore the output):\n" + string.Join('\n', tracked));
	}

	[Fact]
	public void Wwwroot_JavaScript_IsCompiledFromTypeScript()
	{
		var root = FindRepoRoot();
		var offenders = new List<string>();

		foreach (var wwwroot in SourceDirectories(root, "wwwroot"))
		{
			var project = Path.GetDirectoryName(wwwroot)!;
			foreach (var js in Directory.EnumerateFiles(wwwroot, "*.js", SearchOption.AllDirectories))
			{
				if (!File.Exists(Path.Combine(project, "Scripts", Path.GetFileNameWithoutExtension(js) + ".ts")))
				{
					offenders.Add(Path.GetRelativePath(root, js));
				}
			}
		}

		Assert.True(File.Exists(Path.Combine(root, "src", "AiChromeProxy.Client", "Scripts", "fsaccess.ts")), "Scripts/fsaccess.ts is missing.");
		Assert.True(offenders.Count == 0, "JavaScript without a Scripts/<name>.ts source:\n" + string.Join('\n', offenders));
	}

	[Fact]
	public void TypeScript_NeverUsesAny()
	{
		var root = FindRepoRoot();
		var offenders = new List<string>();

		foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.ts", SearchOption.AllDirectories).Where(f => !IsBuildOutput(root, f)))
		{
			var lines = File.ReadAllLines(file);
			for (var i = 0; i < lines.Length; i++)
			{
				if (AnyType.IsMatch(lines[i]))
				{
					offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}");
				}
			}
		}

		Assert.True(offenders.Count == 0, "`any` in TypeScript (type it, or use unknown and narrow):\n" + string.Join('\n', offenders));
	}

	[Theory]
	[InlineData("let x: any;", true)]
	[InlineData("const y = z as any;", true)]
	[InlineData("const y = <any>z;", true)]
	[InlineData("let company: string;", false)]
	[InlineData("const many = 1; // as anything", false)]
	public void AnyType_Pattern(string line, bool matches) => Assert.Equal(matches, AnyType.IsMatch(line));

	private static IEnumerable<string> SourceDirectories(string root, string name) =>
		Directory.EnumerateDirectories(Path.Combine(root, "src"), name, SearchOption.AllDirectories).Where(d => !IsBuildOutput(root, d));

	private static bool IsBuildOutput(string root, string path) =>
		Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj" or "node_modules");

	private static List<string> Git(string root, params string[] args)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
		foreach (var arg in args)
		{
			info.ArgumentList.Add(arg);
		}

		using (var process = Process.Start(info)!)
		{
			var output = process.StandardOutput.ReadToEnd();
			process.WaitForExit();
			Assert.Equal(0, process.ExitCode);
			return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
		}
	}

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
