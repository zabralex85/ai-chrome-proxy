using AiChromeProxy.Client.Navigator;
using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Tests.Client;

/// <summary>The pure parts of the file tabs: the viewer pool, the refresh gate, link targets and the text-or-note decision.</summary>
public sealed class FileViewLogicTests
{
	[Fact]
	public void Pool_KeepsTheTenMostRecentlyShown_AndEvictsTheOldestFirst()
	{
		var pool = new ViewerPool();
		for (var i = 0; i < ViewerPool.Capacity; i++)
		{
			Assert.Empty(pool.Touch($"f{i}"));
		}

		Assert.Empty(pool.Touch("f0"));
		var evicted = pool.Touch("new");

		Assert.Equal(["f1"], evicted);
		Assert.False(pool.Contains("f1"));
		Assert.True(pool.Contains("f0"));
		Assert.Equal(ViewerPool.Capacity, pool.Live.Count);
		Assert.Equal("new", pool.Live[^1]);
	}

	[Fact]
	public void Pool_TouchingTheSamePathAgain_EvictsNothing()
	{
		var pool = new ViewerPool(1);

		Assert.Empty(pool.Touch("a"));
		Assert.Empty(pool.Touch("a"));
		Assert.Equal(["a"], pool.Touch("b"));
	}

	[Fact]
	public void Pool_KeepForgetsClosedTabs_ClearForgetsAll()
	{
		var pool = new ViewerPool();
		pool.Touch("a");
		pool.Touch("b");

		pool.Keep(["b"]);

		Assert.Equal(["b"], pool.Live);
		pool.Clear();
		Assert.Empty(pool.Live);
	}

	[Fact]
	public void RefreshGate_TriggersDuringARead_AskForExactlyOneMoreRead()
	{
		var gate = new RefreshGate();

		Assert.True(gate.TryStart());
		Assert.False(gate.TryStart());
		Assert.False(gate.TryStart());
		Assert.True(gate.Finish());
		Assert.False(gate.Finish());
		Assert.True(gate.TryStart());
	}

	[Fact]
	public void RefreshGate_WithoutTriggersMeanwhile_FreesTheGate()
	{
		var gate = new RefreshGate();

		Assert.True(gate.TryStart());
		Assert.False(gate.Finish());
		Assert.True(gate.TryStart());
	}

	[Theory]
	[InlineData("a\nb\nc", 2, null, 2)]
	[InlineData("a\nb\nc", 0, null, null)]
	[InlineData("a\nclass Foo\nc", 0, "Foo", 2)]
	[InlineData("a\nb\nc", 0, "Foo", null)]
	public void FileLink_LineIn_IsTheLineOrTheSymbolsLine(string text, int line, string? symbol, int? expected) =>
		Assert.Equal(expected, new FileLink("a.cs", line, symbol).LineIn(text));

	[Fact]
	public void Decide_TextBinaryTooLargeAndMissing()
	{
		Assert.Equal(("hi", null), FileContent.Decide(new FileBytes([104, 105], 2)));
		Assert.Equal((null, "Binary file — not shown"), FileContent.Decide(new FileBytes([1, 0, 2], 3)));
		Assert.Equal((null, "Too large to show (6.0 MB)"), FileContent.Decide(new FileBytes(new byte[FileText.MaxBytes + 1], 6L * 1024 * 1024)));
		Assert.Equal((null, "This file is no longer in the folder"), FileContent.Decide(null));
	}
}
