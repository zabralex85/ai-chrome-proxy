using System.Diagnostics;
using System.Text;

namespace AiChromeProxy.Tests.Architecture;

/// <summary>Text files are UTF-8 without BOM (the compiler silently reads a file with one stray byte in the system code page, which turned "…" into mojibake once).</summary>
public sealed class Utf8SourceTests
{
	private static readonly string[] Extensions = [".cs", ".razor", ".ts", ".md", ".json", ".css"];

	[Fact]
	public void TrackedTextFiles_AreStrictUtf8WithoutBom()
	{
		var root = FindRepoRoot();
		var strict = new UTF8Encoding(false, true);
		var offenders = new List<string>();

		foreach (var file in Tracked(root).Where(f => Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && !f.Contains("/wwwroot/lib/", StringComparison.Ordinal)))
		{
			var path = Path.Combine(root, file);
			if (!File.Exists(path))
			{
				continue;
			}

			var bytes = File.ReadAllBytes(path);
			if (bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble))
			{
				offenders.Add(file + " (BOM)");
				continue;
			}

			try
			{
				strict.GetString(bytes);
			}
			catch (DecoderFallbackException)
			{
				offenders.Add(file + " (not UTF-8)");
			}
		}

		Assert.True(offenders.Count == 0, "Files that are not UTF-8 without BOM:\n" + string.Join('\n', offenders));
	}

	private static List<string> Tracked(string root)
	{
		var info = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, UseShellExecute = false };
		info.ArgumentList.Add("ls-files");
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
