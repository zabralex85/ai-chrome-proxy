using AiChromeProxy.Client.Navigator;

namespace AiChromeProxy.Tests.Client;

public sealed class SymbolFinderTests
{
	private const string Source = "using System;\nvar x = Parse(1);\nnamespace A;\npublic sealed class Parse\n{\n\tpublic static void Main() { Parse(2); }\n\tint Parser;\n}\n";

	[Theory]
	[InlineData("Parse", 4)]
	[InlineData("Main", 6)]
	[InlineData("Parser", 7)]
	[InlineData("x", 2)]
	public void Find_PrefersTheDeclaration(string symbol, int line) => Assert.Equal(line, SymbolFinder.Find(Source, symbol));

	[Fact]
	public void Find_WithoutAKeyword_UsesTheFirstWholeWord() => Assert.Equal(2, SymbolFinder.Find("a\nfoo(bar)\nbar()\n", "bar"));

	[Fact]
	public void Find_IsWholeWordOnly() => Assert.Null(SymbolFinder.Find("class FooBar {}\nvar myFoo = 1;\nFoo2()\n", "Foo"));

	[Fact]
	public void Find_ManyLongLines_ReturnsWithinTheBudget()
	{
		var line = string.Join(' ', Enumerable.Repeat("word", 6000)); // 29 999 chars: over the declaration limit, whole-word search only
		var text = string.Join('\n', Enumerable.Repeat(line, 500));
		var clock = System.Diagnostics.Stopwatch.StartNew();
		Assert.Null(SymbolFinder.Find(text, "Missing"));
		Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), clock.Elapsed.ToString());
	}

	[Fact]
	public void Find_LongLine_StillFindsTheWholeWord() => Assert.Equal(1, SymbolFinder.Find(new string('a', 10_001) + " Target", "Target"));

	[Fact]
	public void Find_NotFound_IsNull() => Assert.Null(SymbolFinder.Find("a\nb", "Missing"));

	[Theory]
	[InlineData("")]
	[InlineData("1abc")]
	[InlineData("a b")]
	public void Find_InvalidSymbol_IsNull(string symbol) => Assert.Null(SymbolFinder.Find("a b 1abc", symbol));

	[Fact]
	public void Find_KeywordMustBeAWholeWord() => Assert.Equal(2, SymbolFinder.Find("typeof(Foo)\nfunc Foo() {}\n", "Foo"));

	[Fact]
	public void Find_DottedSymbol() => Assert.Equal(2, SymbolFinder.Find("x\nclass A.B {}\n", "A.B"));

	[Fact]
	public void Find_PathologicalLine_NeverThrows()
	{
		var line = string.Concat(Enumerable.Repeat("public ", 300_000));

		var found = SymbolFinder.Find(line, "Missing");

		Assert.Null(found);
	}

	[Fact]
	public void Find_HandlesCrLf() => Assert.Equal(3, SymbolFinder.Find("a\r\nFoo\r\nclass Foo\r\n", "Foo"));
}
