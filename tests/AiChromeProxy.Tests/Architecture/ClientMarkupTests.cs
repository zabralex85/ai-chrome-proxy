namespace AiChromeProxy.Tests.Architecture;

/// <summary>File names, paths and server messages are untrusted: the client renders them only through Razor's encoding, never as raw markup.</summary>
public sealed class ClientMarkupTests
{
	[Fact]
	public void Client_NeverRendersRawMarkup()
	{
		var root = FindRepoRoot();
		var client = Path.Combine(root, "src", "AiChromeProxy.Client");
		var sources = Directory.EnumerateFiles(client, "*", SearchOption.AllDirectories)
			.Where(f => f.EndsWith(".razor", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
			.Where(f => !Path.GetRelativePath(client, f).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "bin" or "obj"))
			.ToList();
		var offenders = sources
			.Where(f => File.ReadAllText(f) is var text && (text.Contains("MarkupString", StringComparison.Ordinal) || text.Contains("AddMarkupContent", StringComparison.Ordinal)))
			.Select(f => Path.GetRelativePath(root, f))
			.ToList();

		Assert.Contains(sources, f => f.EndsWith("Home.razor", StringComparison.Ordinal));
		Assert.True(offenders.Count == 0, "Raw markup in the client (render text through Razor instead):\n" + string.Join('\n', offenders));
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
