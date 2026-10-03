using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Domain;

public sealed class SyncPathTests
{
	[Theory]
	[InlineData("a.txt")]
	[InlineData("src/App/Program.cs")]
	[InlineData(".gitignore")]
	[InlineData(".github/workflows/ci.yml")]
	[InlineData(".gitattributes")]
	[InlineData("src/x.git/a")]
	[InlineData("docs/My File (1).md")]
	[InlineData("con-fig/x.txt")]
	[InlineData("CONSOLE.md")]
	[InlineData("a/b..c/d")]
	[InlineData("ünïcødé/файл.txt")]
	[InlineData("a~b")]
	[InlineData("x~")]
	[InlineData("~x1")]
	[InlineData("COM10")]
	[InlineData("emoji\U0001F600.txt")]
	public void Valid(string path)
	{
		Assert.Null(SyncPath.GetError(path));
		Assert.True(SyncPath.IsValid(path));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("/etc/passwd")]
	[InlineData("a//b")]
	[InlineData("a/")]
	[InlineData("./a")]
	[InlineData("a/./b")]
	[InlineData("..")]
	[InlineData("../a")]
	[InlineData("a/../../b")]
	[InlineData("a\\b")]
	[InlineData("..\\a")]
	[InlineData("C:/Windows/win.ini")]
	[InlineData("C:")]
	[InlineData("file.txt:secret")]
	[InlineData("a*b")]
	[InlineData("a?b")]
	[InlineData("a\"b")]
	[InlineData("a<b")]
	[InlineData("a>b")]
	[InlineData("a|b")]
	[InlineData("a\tb")]
	[InlineData("a\0b")]
	[InlineData("CON")]
	[InlineData("con")]
	[InlineData("dir/NUL.txt")]
	[InlineData("aux.tar.gz")]
	[InlineData("COM1")]
	[InlineData("lpt9.log")]
	[InlineData("trailing.")]
	[InlineData("dir./a")]
	[InlineData("trailing ")]
	[InlineData("dir /a")]
	[InlineData("x.cs.aicp-tmp")]
	[InlineData("X.CS.AICP-TMP")]
	[InlineData("d.aicp-tmp/x")]
	[InlineData("GIT~1/hooks/pre-commit")]
	[InlineData("PROGRA~1/x")]
	[InlineData("ATXT~1.AIC")]
	[InlineData("COM\u00B9")]
	[InlineData("com\u00B2.txt")]
	[InlineData("COM\u00B3")]
	[InlineData("LPT\u00B9")]
	[InlineData("dir/LPT\u00B2.txt")]
	[InlineData("LPT\u00B3")]
	[InlineData("COM0")]
	[InlineData("lpt0.log")]
	[InlineData("CONIN$")]
	[InlineData("conout$.txt")]
	[InlineData("CLOCK$")]
	[InlineData("evil\u202Etxt.exe")]
	[InlineData("zero\u200Bwidth")]
	[InlineData("\uFEFFbom.txt")]
	[InlineData("a\uFF0Fb")]
	[InlineData("a\uFF3Cb")]
	[InlineData("a\u2215b")]
	[InlineData("a\uFF0E\uFF0E/b")]
	[InlineData("C\uFF1Ax")]
	[InlineData("a\u2044b")]
	[InlineData(".git/config")]
	[InlineData(".GIT/HEAD")]
	[InlineData("sub/.git")]
	[InlineData("a/.Git/hooks/x")]
	public void Invalid(string? path)
	{
		Assert.NotNull(SyncPath.GetError(path));
		Assert.False(SyncPath.IsValid(path));
	}

	[Fact]
	public void UnpairedSurrogate_Invalid()
	{
		// Not in [InlineData]: theory data serialization turns a lone surrogate into U+FFFD.
		Assert.False(SyncPath.IsValid("a\uD800b"));
		Assert.False(SyncPath.IsValid("a\uDC00b"));
		Assert.False(SyncPath.IsValid("end\uD800"));
		Assert.False(SyncPath.IsValid("\uDC00\uD800"));
	}

	[Fact]
	public void LongerThan260_Invalid_260_Valid()
	{
		Assert.True(SyncPath.IsValid(new string('a', 200) + "/" + new string('b', 59)));
		Assert.False(SyncPath.IsValid(new string('a', 200) + "/" + new string('b', 60)));
	}

	[Fact]
	public void SegmentLongerThan246_Invalid_SoTheTempFileNameFits()
	{
		Assert.Equal(SyncPath.MaxSegmentLength, 255 - SyncPath.TempSuffix.Length);
		Assert.True(SyncPath.IsValid(new string('a', 246)));
		Assert.False(SyncPath.IsValid(new string('a', 247)));
		Assert.False(SyncPath.IsValid("dir/" + new string('a', 247)));
	}

	[Theory]
	[InlineData("ai-chrome-proxy", "ai-chrome-proxy")]
	[InlineData("My Repo (2)", "My_Repo__2_")]
	[InlineData("  spaced  ", "spaced")]
	[InlineData("репо", "____")]
	[InlineData(".config", ".config")]
	[InlineData("..", "__")]
	[InlineData(".", "_")]
	[InlineData("name.", "name_")]
	[InlineData("CON", "_CON")]
	[InlineData("nul.txt", "_nul_txt")]
	[InlineData("x.aicp-tmp", "_x_aicp-tmp")]
	[InlineData("GIT~1", "GIT_1")]
	[InlineData("PROGRA~1", "PROGRA_1")]
	[InlineData("COM0", "_COM0")]
	[InlineData("lpt0.log", "_lpt0_log")]
	[InlineData("LPT\u00B2", "LPT_")]
	[InlineData("CONIN$", "CONIN_")]
	[InlineData(".git", "__git")]
	public void RepoName_Sanitized(string folder, string expected)
	{
		var repo = RepoName.Sanitize(folder);

		Assert.Equal(expected, repo);
		Assert.True(RepoName.IsValid(repo));
		Assert.True(SyncPath.IsValid(repo));
	}

	[Theory]
	[InlineData(null)]
	[InlineData("")]
	[InlineData("   ")]
	public void RepoName_Empty_Null(string? folder)
	{
		Assert.Null(RepoName.Sanitize(folder));
		Assert.False(RepoName.IsValid(folder));
	}

	[Fact]
	public void RepoName_TruncatedTo64()
	{
		Assert.Equal(new string('r', 64), RepoName.Sanitize(new string('r', 100)));
		Assert.False(RepoName.IsValid(new string('r', 65)));
		Assert.False(RepoName.IsValid("a/b"));
	}
}
