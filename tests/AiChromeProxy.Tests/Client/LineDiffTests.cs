using AiChromeProxy.Client.Navigator;

namespace AiChromeProxy.Tests.Client;

public sealed class LineDiffTests
{
	[Theory]
	[InlineData("a\nb\nc", "a\nb\nc", new int[0])]
	[InlineData("", "", new int[0])]
	[InlineData("a\nb", "a\nX\nb", new[] { 2 })]
	[InlineData("a\nb\nc", "a\nc", new int[0])]
	[InlineData("a\nb\nc", "a\nB\nc", new[] { 2 })]
	[InlineData("", "a\nb", new[] { 1, 2 })]
	[InlineData("a\nb", "", new int[0])]
	[InlineData("a\nb\nc\nd", "X\nb\nY\nd\nZ", new[] { 1, 3, 5 })]
	[InlineData("a\r\nb\r\n", "a\nb\n", new int[0])]
	[InlineData("a\nb\n", "a\nb\nc\n", new[] { 3 })]
	[InlineData("a\nb\nc\nd", "d\nc\nb\na", new[] { 2, 3, 4 })]
	public void ChangedLines_AreTheInsertedOrChangedOnesOfTheNewText(string oldText, string newText, int[] expected) =>
		Assert.Equal(expected, LineDiff.ChangedLines(oldText, newText));

	[Fact]
	public void ChangedLines_OverTheCap_MarkOnlyTheFirstAndLastDifferingLines()
	{
		var oldLines = Enumerable.Range(0, LineDiff.MaxLines + 1).Select(i => $"line {i}").ToArray();
		var newLines = (string[])oldLines.Clone();
		newLines[10] = "changed";
		newLines[500] = "changed too";
		newLines[LineDiff.MaxLines - 5] = "and last";

		var changed = LineDiff.ChangedLines(string.Join('\n', oldLines), string.Join('\n', newLines));

		Assert.Equal([11, LineDiff.MaxLines - 4], changed);
	}

	[Fact]
	public void ChangedLines_OverTheCap_WithOneDifference_MarksIt()
	{
		var oldLines = Enumerable.Range(0, LineDiff.MaxLines + 1).Select(i => $"line {i}").ToArray();
		var newLines = (string[])oldLines.Clone();
		newLines[7] = "changed";

		Assert.Equal([8], LineDiff.ChangedLines(string.Join('\n', oldLines), string.Join('\n', newLines)));
	}

	[Fact]
	public void ChangedLines_OverTheCap_PureDeletion_MarksNothing()
	{
		var oldLines = Enumerable.Range(0, LineDiff.MaxLines + 2).Select(i => $"line {i}").ToList();
		var newLines = oldLines.ToList();
		newLines.RemoveAt(100);

		Assert.Empty(LineDiff.ChangedLines(string.Join('\n', oldLines), string.Join('\n', newLines)));
	}

	[Fact]
	public void ChangedLines_LargeDifferingMiddle_FallsBackInsteadOfAllocatingATable()
	{
		var oldText = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"old {i}"));
		var newText = string.Join('\n', Enumerable.Range(0, 5000).Select(i => $"new {i}"));

		Assert.Equal([1, 5000], LineDiff.ChangedLines(oldText, newText));
	}
}
