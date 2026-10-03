using AiChromeProxy.Client.Shell;
using AiChromeProxy.Client.Sync;
using AiChromeProxy.Client.Transport;

namespace AiChromeProxy.Tests.Client;

public sealed class ShellTests
{
	private static readonly IReadOnlyList<TreeNode> Tree = FileTree.Build(["b.txt", "src/z.cs", "src/App/a.cs", "A.md", "docs/readme.md", "src/b.cs"]);

	[Fact]
	public void Build_FoldersFirstThenFiles_SortedIgnoringCase()
	{
		Assert.Equal(["docs", "src", "A.md", "b.txt"], Tree.Select(n => n.Name));
		var src = Tree[1];
		Assert.True(src.IsFolder);
		Assert.Equal("src", src.Path);
		Assert.Equal(["App", "b.cs", "z.cs"], src.Children.Select(n => n.Name));
		Assert.Equal("src/App/a.cs", src.Children[0].Children[0].Path);
		Assert.False(src.Children[0].Children[0].IsFolder);
	}

	[Fact]
	public void Rows_OnlyExpandedFoldersShowChildren()
	{
		var rows = FileTree.Rows(Tree, new HashSet<string> { "src" });

		Assert.Equal(["docs", "src", "src/App", "src/b.cs", "src/z.cs", "A.md", "b.txt"], rows.Select(r => r.Node.Path));
		Assert.Equal([0, 0, 1, 1, 1, 0, 0], rows.Select(r => r.Depth));
	}

	[Theory]
	[InlineData("ArrowDown", "src", "src/App", null)]
	[InlineData("ArrowDown", "b.txt", "b.txt", null)]
	[InlineData("ArrowUp", "src/App", "src", null)]
	[InlineData("ArrowUp", "docs", "docs", null)]
	[InlineData("Home", "b.txt", "docs", null)]
	[InlineData("End", "docs", "b.txt", null)]
	[InlineData("ArrowLeft", "src/b.cs", "src", null)]
	[InlineData("ArrowLeft", "A.md", "A.md", null)]
	[InlineData("Enter", "src/b.cs", "src/b.cs", "src/b.cs")]
	[InlineData(" ", "A.md", "A.md", "A.md")]
	[InlineData("x", "A.md", "A.md", null)]
	[InlineData("ArrowDown", null, "src", null)]
	public void OnKey_MovesAndOpens(string key, string? active, string expectedActive, string? expectedOpen)
	{
		var expanded = new HashSet<string> { "src" };

		var (newActive, open) = FileTree.OnKey(key, FileTree.Rows(Tree, expanded), active, expanded);

		Assert.Equal(expectedActive, newActive);
		Assert.Equal(expectedOpen, open);
	}

	[Fact]
	public void OnKey_RightExpandsThenEnters_LeftCollapses_EnterToggles()
	{
		var expanded = new HashSet<string>();

		Assert.Equal(("docs", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.Contains("docs", expanded);
		Assert.Equal(("docs/readme.md", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.Equal(("docs", null), FileTree.OnKey("ArrowLeft", FileTree.Rows(Tree, expanded), "docs", expanded));
		Assert.DoesNotContain("docs", expanded);
		Assert.Equal(("src", null), FileTree.OnKey("Enter", FileTree.Rows(Tree, expanded), "src", expanded));
		Assert.Contains("src", expanded);
		Assert.Equal(("src", null), FileTree.OnKey(" ", FileTree.Rows(Tree, expanded), "src", expanded));
		Assert.DoesNotContain("src", expanded);
		Assert.Equal(("A.md", null), FileTree.OnKey("ArrowRight", FileTree.Rows(Tree, expanded), "A.md", expanded));
	}

	[Fact]
	public void OnKey_EmptyTree_Nothing()
	{
		Assert.Equal((null, null), FileTree.OnKey("ArrowDown", [], null, new HashSet<string>()));
	}

	[Theory]
	[InlineData(0, "0")]
	[InlineData(999, "999")]
	[InlineData(1234, "1 234")]
	[InlineData(20000, "20 000")]
	public void Count_GroupedWithNoBreakSpace(long value, string expected)
	{
		Assert.Equal(expected, Format.Count(value));
	}

	[Theory]
	[InlineData(0, "0 B")]
	[InlineData(1023, "1023 B")]
	[InlineData(1536, "1.5 KB")]
	[InlineData(5 * 1024 * 1024, "5 MB")]
	[InlineData(3L * 1024 * 1024 * 1024, "3 GB")]
	public void Bytes_HumanReadable(long bytes, string expected)
	{
		Assert.Equal(expected, Format.Bytes(bytes));
	}

	[Theory]
	[InlineData(TransportState.Connected, "Connected")]
	[InlineData(TransportState.Connecting, "Connecting…")]
	[InlineData(TransportState.Reconnecting, "Reconnecting…")]
	[InlineData(TransportState.Disconnected, "Offline")]
	public void Connection_Text(TransportState state, string expected)
	{
		Assert.Equal(expected, Format.Connection(state));
	}

	[Fact]
	public void Ago_Text()
	{
		var now = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

		Assert.Equal("never", Format.Ago(null, now));
		Assert.Equal("just now", Format.Ago(now.AddSeconds(-2), now));
		Assert.Equal("42 s ago", Format.Ago(now.AddSeconds(-42), now));
		Assert.Equal("5 min ago", Format.Ago(now.AddMinutes(-5), now));
		Assert.Equal(now.AddHours(-2).ToLocalTime().ToString("HH:mm"), Format.Ago(now.AddHours(-2), now));
	}

	[Theory]
	[InlineData(FolderStatus.None, SyncPhase.Idle, "No folder open")]
	[InlineData(FolderStatus.NeedsPermission, SyncPhase.Synced, "Access needed")]
	[InlineData(FolderStatus.Ready, SyncPhase.Idle, "Waiting for the server…")]
	[InlineData(FolderStatus.Ready, SyncPhase.Scanning, "Scanning…")]
	[InlineData(FolderStatus.Ready, SyncPhase.Uploading, "Uploading 12/80")]
	[InlineData(FolderStatus.Ready, SyncPhase.Failed, "Sync failed")]
	public void SyncStatus_ByState(FolderStatus folder, SyncPhase phase, string expected)
	{
		Assert.Equal(expected, Format.SyncStatus(folder, phase, 1234, 12, 80, null));
	}

	[Fact]
	public void SyncStatus_Synced_WithCountdown()
	{
		Assert.Equal("Synced 1 234 files", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 1234, 0, 0, null));
		Assert.Equal("Synced 1 234 files · Rescan in 7 s", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 1234, 0, 0, TimeSpan.FromSeconds(6.2)));
		Assert.Equal("Synced 3 files · Rescan in 0 s", Format.SyncStatus(FolderStatus.Ready, SyncPhase.Synced, 3, 0, 0, TimeSpan.FromSeconds(-1)));
	}

	[Theory]
	[InlineData("Program.cs", "code")]
	[InlineData("app.TS", "code")]
	[InlineData("index.html", "web")]
	[InlineData("appsettings.json", "data")]
	[InlineData("README.md", "doc")]
	[InlineData("logo.png", "image")]
	[InlineData("LICENSE", "file")]
	public void FileKind_ByExtension(string name, string kind)
	{
		Assert.Equal(kind, Format.FileKind(name));
	}

	[Fact]
	public void Tabs_StartWithWelcome_ShowOpensOrFocuses()
	{
		var tabs = new TabSet();

		Assert.Equal([TabSet.Welcome], tabs.Open);
		Assert.Equal(TabSet.Welcome, tabs.Active);

		tabs.Show(Id("a.cs"));
		tabs.Show(Id("b.cs"));
		tabs.Show(Id("a.cs"));

		Assert.Equal([TabSet.Welcome, Id("a.cs"), Id("b.cs")], tabs.Open);
		Assert.Equal(Id("a.cs"), tabs.Active);
	}

	[Fact]
	public void Tabs_CloseActive_NextOrPreviousBecomesActive_WelcomeStays()
	{
		var tabs = new TabSet();
		tabs.Show(Id("a.cs"));
		tabs.Show(TabSet.Errors);
		tabs.Show(Id("b.cs"));

		tabs.Close(Id("b.cs"));
		Assert.Equal(TabSet.Errors, tabs.Active);

		tabs.Show(Id("a.cs"));
		tabs.Close(Id("a.cs"));
		Assert.Equal(TabSet.Errors, tabs.Active);

		tabs.Close(TabSet.Welcome);
		tabs.Close(Id("missing.cs"));
		Assert.Equal([TabSet.Welcome, TabSet.Errors], tabs.Open);
		Assert.Equal(TabSet.Errors, tabs.Active);

		tabs.Show(TabSet.Welcome);
		tabs.Close(TabSet.Errors);
		Assert.Equal([TabSet.Welcome], tabs.Open);
		Assert.Equal(TabSet.Welcome, tabs.Active);
	}

	[Theory]
	[InlineData("ArrowRight", "a.cs", "b.cs")]
	[InlineData("ArrowRight", "b.cs", TabSet.Welcome)]
	[InlineData("ArrowLeft", TabSet.Welcome, "b.cs")]
	[InlineData("ArrowLeft", "b.cs", "a.cs")]
	[InlineData("Home", "b.cs", TabSet.Welcome)]
	[InlineData("End", TabSet.Welcome, "b.cs")]
	[InlineData("x", "a.cs", "a.cs")]
	public void Tabs_OnKey_MovesWithWrap(string key, string active, string expected)
	{
		var tabs = new TabSet();
		tabs.Show(Id("a.cs"));
		tabs.Show(Id("b.cs"));
		tabs.Show(Id(active));

		tabs.OnKey(key);

		Assert.Equal(Id(expected), tabs.Active);
	}

	[Theory]
	[InlineData(TabSet.Welcome, false)]
	[InlineData(TabSet.Errors, false)]
	[InlineData("src/app.cs", false)]
	[InlineData("file:src/app.cs", true)]
	public void Tabs_IsFile(string id, bool isFile)
	{
		Assert.Equal(isFile, TabSet.IsFile(id));
		Assert.Equal(isFile ? "src/app.cs" : null, TabSet.PathOf(id));
	}

	[Theory]
	[InlineData(":errors")]
	[InlineData(":welcome")]
	public void Tabs_FileNamedLikeABuiltInTab_GetsItsOwnTab(string path)
	{
		var tabs = new TabSet();
		tabs.Show(TabSet.Errors);

		tabs.Show(TabSet.FileTab(path));

		Assert.Equal([TabSet.Welcome, TabSet.Errors, TabSet.FileTab(path)], tabs.Open);
		Assert.True(TabSet.IsFile(tabs.Active));
		Assert.Equal(path, TabSet.PathOf(tabs.Active));
	}

	[Theory]
	[InlineData(null, "docs")]
	[InlineData("src/b.cs", "src/b.cs")]
	[InlineData("src/App/a.cs", "docs")]
	public void TabStop_ActiveRowWhileVisible_ElseTheFirst(string? active, string expected)
	{
		var rows = FileTree.Rows(Tree, new HashSet<string> { "src" });

		Assert.Equal(expected, FileTree.TabStop(rows, active));
		Assert.Null(FileTree.TabStop([], active));
	}

	[Theory]
	[InlineData(FolderStatus.NeedsPermission, 0, "Access needed")]
	[InlineData(FolderStatus.NeedsPermission, 3, "Access needed")]
	[InlineData(FolderStatus.Ready, 0, null)]
	[InlineData(FolderStatus.Ready, 1, "1 error")]
	[InlineData(FolderStatus.Ready, 1234, "1 234 errors")]
	public void Attention_AccessFirstThenErrors(FolderStatus folder, int errors, string? expected)
	{
		Assert.Equal(expected, Format.Attention(folder, errors));
	}

	[Theory]
	[InlineData(280, 300, 1440, 280)]
	[InlineData(100, 300, 1440, PanelWidth.Min)]
	[InlineData(900, 300, 1440, PanelWidth.Max)]
	[InlineData(600, 300, 1200, 540)]
	[InlineData(500, 600, 1024, PanelWidth.Min)]
	[InlineData(500, 0, 1024, 500)]
	[InlineData(500, 300, 800, 364)]
	public void PanelWidth_KeepsTheCentreAtLeast360(int width, int other, int viewport, int expected)
	{
		Assert.Equal(expected, PanelWidth.Clamp(width, other, viewport));
	}

	[Theory]
	[InlineData("<img src=x onerror=alert(1)>.cs")]
	[InlineData("\"><script>alert(1)<\\script>.html")]
	[InlineData("<svg onload=alert(1)>")]
	public void HostileNames_StayPlainData_NeverMarkup(string name)
	{
		var node = Assert.Single(Assert.Single(FileTree.Build([$"dir/{name}"])).Children);

		Assert.Equal(name, node.Name);
		Assert.Equal($"dir/{name}", node.Path);
		Assert.Equal($"dir/{name}", TabSet.PathOf(TabSet.FileTab(node.Path)));
		Assert.Contains(Format.FileKind(name), new[] { "code", "web", "data", "doc", "image", "file" });
		Assert.DoesNotContain('<', Format.Attention(FolderStatus.Ready, 2)!);
	}

	private static string Id(string tab) => tab.StartsWith(':') ? tab : TabSet.FileTab(tab);
}
