using AiChromeProxy.Client.Chat;

namespace AiChromeProxy.Tests.Client;

public sealed class DiagramFilesTests
{
	private static readonly DateTime Now = new(2026, 10, 4, 9, 5, 0);

	[Theory]
	[InlineData("classDiagram\n A --> B", "class-diagram-20261004-0905")]
	[InlineData("flowchart TD\n A --> B", "flowchart-20261004-0905")]
	[InlineData("graph LR\n A --> B", "flowchart-20261004-0905")]
	[InlineData("sequenceDiagram\n A->>B: hi", "sequence-diagram-20261004-0905")]
	[InlineData("stateDiagram-v2\n [*] --> A", "state-diagram-20261004-0905")]
	[InlineData("stateDiagram\n [*] --> A", "state-diagram-20261004-0905")]
	[InlineData("erDiagram\n A ||--o{ B : has", "er-diagram-20261004-0905")]
	[InlineData("gitGraph\n commit", "git-graph-20261004-0905")]
	[InlineData("gantt\n a", "gantt-20261004-0905")]
	[InlineData("pie\n a: 1", "pie-20261004-0905")]
	[InlineData("%% note\r\n\r\nmindmap\r\n root", "mindmap-20261004-0905")]
	[InlineData("---\r\nconfig: y\r\n---\r\njourney\r\n", "journey-20261004-0905")]
	[InlineData("timeline\n a", "timeline-20261004-0905")]
	[InlineData("C4Context\n", "c4context-20261004-0905")]
	[InlineData("", "diagram-20261004-0905")]
	public void DefaultName_ByType(string source, string expected) => Assert.Equal(expected, DiagramFiles.DefaultName(source, Now));

	[Fact]
	public void DefaultName_FrontMatterTitle_Slugified() =>
		Assert.Equal("my-auth-flow", DiagramFiles.DefaultName("---\ntitle: \"My Auth Flow!\"\n---\nflowchart TD\n A-->B", Now));

	[Fact]
	public void DefaultName_TitleLine_Slugified() =>
		Assert.Equal("order-states", DiagramFiles.DefaultName("stateDiagram-v2\n  title Order states\n  [*] --> A", Now));

	[Fact]
	public void DefaultName_TitleColonOutsideFrontMatter_IsNotATitle() =>
		Assert.Equal("flowchart-20261004-0905", DiagramFiles.DefaultName("flowchart TD\n title: nope", Now));

	[Fact]
	public void DefaultName_UnicodeBecomesDashes() =>
		Assert.Equal("a-b-c", DiagramFiles.DefaultName("flowchart\n title aüb  c", Now));

	[Fact]
	public void DefaultName_OnlyUnicodeTitle_IsDiagram() =>
		Assert.Equal("diagram", DiagramFiles.DefaultName("flowchart\n title üö", Now));

	[Fact]
	public void DefaultName_LongTitle_CutAt60_NoTrailingDash() =>
		Assert.Equal(new string('a', 59), DiagramFiles.DefaultName("flowchart\n title " + new string('a', 59) + " bbbb", Now));

	[Fact]
	public void DefaultPath_UsesFolderAndExtension() =>
		Assert.Equal("docs/diagrams/class-diagram-20261004-0905.svg", DiagramFiles.DefaultPath("classDiagram", DiagramFileKind.Svg, Now));

	[Theory]
	[InlineData(DiagramFileKind.Source, ".mmd")]
	[InlineData(DiagramFileKind.Svg, ".svg")]
	[InlineData(DiagramFileKind.Png, ".png")]
	public void Extension_ByKind(DiagramFileKind kind, string expected) => Assert.Equal(expected, DiagramFiles.Extension(kind));

	[Theory]
	[InlineData(" a/b.txt ", "a/b.png")]
	[InlineData("a/b", "a/b.png")]
	[InlineData("a.d/b", "a.d/b.png")]
	[InlineData("a/.hidden", "a/.hidden.png")]
	[InlineData("x.tar.gz", "x.tar.png")]
	[InlineData("n.png", "n.png")]
	public void WithExtension_Enforced(string path, string expected) => Assert.Equal(expected, DiagramFiles.WithExtension(path, DiagramFileKind.Png));
}
