using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Tests.Client;

public sealed class IgnoreRulesTests
{
	[Theory]
	[InlineData(".git/config")]
	[InlineData("node_modules/react/index.js")]
	[InlineData("web/node_modules/x.js")]
	[InlineData("src/App/bin/Release/App.dll")]
	[InlineData("src/App/obj/project.assets.json")]
	[InlineData(".vs/x/y")]
	[InlineData(".idea/workspace.xml")]
	[InlineData(".env")]
	[InlineData("config/.env")]
	[InlineData(".env.local")]
	[InlineData(".ENV")]
	[InlineData("certs/site.pfx")]
	[InlineData("a.key")]
	[InlineData("tls/server.PEM")]
	[InlineData("id_rsa")]
	[InlineData("home/id_rsa.pub")]
	public void BuiltIn_Ignored(string path)
	{
		Assert.True(IgnoreRules.Create(null).IsIgnored(path));
	}

	[Theory]
	[InlineData("README.md")]
	[InlineData("src/bin.cs")]
	[InlineData("binary/x")]
	[InlineData("src/objects/a.cs")]
	[InlineData(".envrc")]
	[InlineData("environment.ts")]
	[InlineData("keys/readme.md")]
	[InlineData("a.keystore")]
	[InlineData(".gitignore")]
	[InlineData(".github/workflows/ci.yml")]
	public void BuiltIn_NotIgnored(string path)
	{
		Assert.False(IgnoreRules.Create(null).IsIgnored(path));
	}

	[Theory]
	[InlineData("*.log", "app.log", true)]
	[InlineData("*.log", "logs/deep/app.log", true)]
	[InlineData("*.log", "app.log.txt", false)]
	[InlineData("build/", "build/out.js", true)]
	[InlineData("build/", "src/build/out.js", true)]
	[InlineData("build/", "build", false)]
	[InlineData("/dist", "dist/a.js", true)]
	[InlineData("/dist", "src/dist/a.js", false)]
	[InlineData("docs/*.md", "docs/a.md", true)]
	[InlineData("docs/*.md", "docs/sub/a.md", false)]
	[InlineData("docs/*.md", "x/docs/a.md", false)]
	[InlineData("**/temp", "temp/a", true)]
	[InlineData("**/temp", "a/b/temp/c", true)]
	[InlineData("logs/**", "logs/a/b.txt", true)]
	[InlineData("logs/**", "logs", false)]
	[InlineData("a/**/b", "a/b", true)]
	[InlineData("a/**/b", "a/x/y/b", true)]
	[InlineData("a/**/b", "a/x/y/c", false)]
	[InlineData("file?.txt", "file1.txt", true)]
	[InlineData("file?.txt", "file10.txt", false)]
	[InlineData("file?.txt", "dir/file/.txt", false)]
	[InlineData("Thumbs.db", "pics/thumbs.DB", true)]
	[InlineData("a.b", "aXb", false)]
	[InlineData("# comment", "# comment", false)]
	[InlineData("   ", "x", false)]
	public void Gitignore_SinglePattern(string pattern, string path, bool ignored)
	{
		Assert.Equal(ignored, IgnoreRules.Create(pattern).IsIgnored(path));
	}

	[Fact]
	public void Gitignore_NegationAndOrder()
	{
		var rules = IgnoreRules.Create("# logs\r\n*.log\r\n!keep.log\r\n\r\n/out/*\r\n!/out/public\r\n");

		Assert.True(rules.IsIgnored("a.log"));
		Assert.False(rules.IsIgnored("keep.log"));
		Assert.False(rules.IsIgnored("sub/keep.log"));
		Assert.True(rules.IsIgnored("out/private/x.txt"));
		Assert.False(rules.IsIgnored("out/public/index.html"));
	}

	[Fact]
	public void Gitignore_ExcludedParentCannotBeReincluded()
	{
		var rules = IgnoreRules.Create("tmp/\n!tmp/keep.txt\n");

		Assert.True(rules.IsIgnored("tmp/keep.txt"));
	}

	[Fact]
	public void Gitignore_NegationCannotExposeBuiltInSecrets()
	{
		var rules = IgnoreRules.Create("!.env\n!*.pem\n!node_modules/\n");

		Assert.True(rules.IsIgnored(".env"));
		Assert.True(rules.IsIgnored("certs/a.pem"));
		Assert.True(rules.IsIgnored("node_modules/x.js"));
	}

	[Fact]
	public void BuiltInDirectories_AreAllCoveredByPatterns()
	{
		var rules = IgnoreRules.Create(null);

		Assert.All(IgnoreRules.BuiltInDirectories, d => Assert.True(rules.IsIgnored(d + "/x")));
	}

	[Fact]
	public void Create_StripsBom()
	{
		Assert.True(IgnoreRules.Create((char)0xFEFF + "*.log").IsIgnored("a.log"));
	}

	[Fact]
	public void Gitignore_TrailingSpaceIsTrimmed()
	{
		Assert.True(IgnoreRules.Create("foo ").IsIgnored("foo"));
	}

	[Fact]
	public void IsIgnored_AcceptsBackslashes()
	{
		Assert.True(IgnoreRules.Create("build/\n").IsIgnored("src\\build\\out.js"));
		Assert.True(IgnoreRules.Create(null).IsIgnored("a\\b\\.ENV"));
	}

	[Fact]
	public void BuiltIn_DeepCaseInsensitive()
	{
		Assert.True(IgnoreRules.Create(null).IsIgnored("a/b/.ENV"));
	}

	[Fact]
	public void Gitignore_NegationCannotReincludeEnvStar()
	{
		Assert.True(IgnoreRules.Create("!.env.*\n").IsIgnored("x/.env.production"));
	}

	[Fact]
	public void Gitignore_PathologicalPatternsAreLinear()
	{
		var rules = IgnoreRules.Create("*a*a*a*a*a*b\na/**/**/**/**/b\n");
		var name = new string('a', 200);
		var deep = "a/" + string.Join('/', Enumerable.Repeat("x", 100)) + "/c";

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var first = rules.IsIgnored(name);
		var second = rules.IsIgnored(deep);
		watch.Stop();

		Assert.False(first);
		Assert.False(second);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
		Assert.True(rules.IsIgnored("a/q/b"));
	}

	[Fact]
	public void IsIgnored_ManyPathsStayFast()
	{
		var patterns = new List<string>();
		for (var i = 0; i < 20; i++)
		{
			patterns.Add($"*.ext{i}");
			patterns.Add($"/gen{i}/");
		}

		patterns.AddRange(["docs/**/*.tmp", "src/*/cache", "**/out?/", "!keep.ext1", "a*b*c.log", "*.min.*", "?x.dat", "dist/**", "/vendor/*.js", "build-*/"]);
		var rules = IgnoreRules.Create(string.Join('\n', patterns));
		var paths = Enumerable.Range(0, 20_000).Select(i => $"src/m{i % 50}/n{i % 7}/p{i % 13}/q{i % 5}/file{i}.cs").ToList();

		var watch = System.Diagnostics.Stopwatch.StartNew();
		var ignored = paths.Count(rules.IsIgnored);
		watch.Stop();

		Assert.Equal(0, ignored);
		Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed}");
	}
}
