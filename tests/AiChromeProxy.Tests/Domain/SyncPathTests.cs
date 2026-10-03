using AiChromeProxy.Domain.Sync;

namespace AiChromeProxy.Tests.Domain;

public sealed class SyncPathTests
{
	[Theory]
	[InlineData("a.txt")]
	[InlineData("src/App/Program.cs")]
	[InlineData(".gitignore")]
	[InlineData(".github/workflows/ci.yml")]
	[InlineData("docs/My File (1).md")]
	[InlineData("con-fig/x.txt")]
	[InlineData("CONSOLE.md")]
	[InlineData("a/b..c/d")]
	[InlineData("ünïcødé/файл.txt")]
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
	public void Invalid(string? path)
	{
		Assert.NotNull(SyncPath.GetError(path));
		Assert.False(SyncPath.IsValid(path));
	}

	[Fact]
	public void LongerThan260_Invalid_260_Valid()
	{
		Assert.True(SyncPath.IsValid(new string('a', 260)));
		Assert.False(SyncPath.IsValid(new string('a', 261)));
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
