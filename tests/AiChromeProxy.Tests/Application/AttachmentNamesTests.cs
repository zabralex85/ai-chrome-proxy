using AiChromeProxy.Application.Chat;

namespace AiChromeProxy.Tests.Application;

public sealed class AttachmentNamesTests
{
	[Theory]
	[InlineData("shot.png", "shot.png")]
	[InlineData("C:\\Users\\x\\shot.png", "shot.png")]
	[InlineData("a/b/notes.txt", "notes.txt")]
	[InlineData("bad<>:\"|?*.txt", "bad_______.txt")]
	[InlineData("tab\there.txt", "tab_here.txt")]
	[InlineData("  spaced.txt  ", "spaced.txt")]
	[InlineData("dots...", "dots")]
	[InlineData("..", "file")]
	[InlineData("", "file")]
	[InlineData(null, "file")]
	[InlineData("CON", "_CON")]
	[InlineData("nul.txt", "_nul.txt")]
	[InlineData("com1.tar.gz", "_com1.tar.gz")]
	[InlineData("console.txt", "console.txt")]
	[InlineData("x.part", "x.part_")]
	[InlineData("CON .txt", "_CON .txt")]
	[InlineData("conin$", "_conin$")]
	public void Sanitize(string? name, string expected) => Assert.Equal(expected, AttachmentNames.Sanitize(name));

	[Fact]
	public void Sanitize_CutsLongNames_KeepingTheExtension()
	{
		var name = AttachmentNames.Sanitize(new string('a', 300) + ".png");

		Assert.Equal(AttachmentNames.MaxChars, name.Length);
		Assert.EndsWith("a.png", name);
	}

	[Fact]
	public void Sanitize_NeverCutsASurrogatePair()
	{
		var name = AttachmentNames.Sanitize(new string('a', 95) + "😀😀😀😀😀😀" + ".txt");

		Assert.Equal(new string('a', 95) + ".txt", name);
	}

	[Fact]
	public void Sanitize_PartSuffixLeftByTheCut_StaysWithinTheLimit()
	{
		var name = AttachmentNames.Sanitize(new string('a', 95) + ".part." + new string('b', 30));

		Assert.False(name.EndsWith(".part", StringComparison.OrdinalIgnoreCase));
		Assert.True(name.Length <= AttachmentNames.MaxChars);
	}
}
