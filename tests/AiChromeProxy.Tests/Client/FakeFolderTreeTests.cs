using AiChromeProxy.Client.Sync;
using Microsoft.JSInterop;

namespace AiChromeProxy.Tests.Client;

/// <summary>The folder operations of <see cref="FakeFolder"/> (create, rename, delete, count), which stand in for fsaccess.ts in the engine tests.</summary>
public sealed class FakeFolderTreeTests
{
	[Fact]
	public async Task CreateFile_MakesAnEmptyFile_InAnExistingFolder()
	{
		var folder = Folder();

		await folder.CreateFileAsync("src/new.cs");

		Assert.Empty(folder.Files["src/new.cs"]);
		Assert.Equal(["src/new.cs"], folder.Writes);
	}

	[Theory]
	[InlineData("a.cs")]
	[InlineData("A.CS")]
	[InlineData("src")]
	[InlineData("missing/x.cs")]
	[InlineData("../x.cs")]
	[InlineData("")]
	public async Task CreateFile_RefusesATakenNameAMissingFolderAndInvalidPaths(string path)
	{
		var folder = Folder();

		await Assert.ThrowsAsync<JSException>(() => folder.CreateFileAsync(path));

		Assert.Empty(folder.Writes);
	}

	[Fact]
	public async Task CreateFolder_AppearsInTheScanWhileEmpty_AndIsNotAFile()
	{
		var folder = Folder();

		await folder.CreateFolderAsync("src/Empty");
		var scan = await folder.ScanAsync([], 1000);

		Assert.Contains("src/Empty", scan.Directories!);
		Assert.DoesNotContain(scan.Files, f => f.Path.StartsWith("src/Empty", StringComparison.Ordinal));
		await Assert.ThrowsAsync<JSException>(() => folder.CreateFolderAsync("src/empty"));
	}

	[Fact]
	public async Task Operations_NeedWriteAccess()
	{
		var folder = Folder();
		folder.WriteAccess = false;

		await Assert.ThrowsAsync<JSException>(() => folder.CreateFileAsync("n.cs"));
		await Assert.ThrowsAsync<JSException>(() => folder.CreateFolderAsync("n"));
		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync("a.cs", "b.cs"));
		await Assert.ThrowsAsync<JSException>(() => folder.DeleteAsync("src", true));

		Assert.Empty(folder.Writes);
	}

	[Fact]
	public async Task Operations_FailWhenTheFailureKnobIsSet()
	{
		var folder = Folder();
		folder.OperationFailure = new JSException("NotAllowedError: refused.");

		await Assert.ThrowsAsync<JSException>(() => folder.CreateFileAsync("n.cs"));
		await Assert.ThrowsAsync<JSException>(() => folder.CreateFolderAsync("n"));
		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync("a.cs", "b.cs"));
		await Assert.ThrowsAsync<JSException>(() => folder.DeleteAsync("a.cs", false));

		Assert.Empty(folder.Writes);
		Assert.True(folder.Files.ContainsKey("a.cs"));
	}

	[Fact]
	public async Task Scan_ListsDirectories_ImplicitOnesIncluded_WithoutTheSkippedOnes()
	{
		var folder = Folder();
		folder.Write("node_modules/x/y.js", "y");
		folder.Directories.Add("empty");
		folder.Directories.Add("bin");

		var scan = await folder.ScanAsync(["node_modules", "bin"], 1000);

		Assert.Equal(["empty", "src", "src/App"], scan.Directories!.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task Rename_MovesAFile_AndKeepsItsContent()
	{
		var folder = Folder();

		await folder.RenameAsync("src/b.cs", "z.cs");

		Assert.False(folder.Files.ContainsKey("src/b.cs"));
		Assert.Equal("b", System.Text.Encoding.UTF8.GetString(folder.Files["src/z.cs"]));
		Assert.Equal(["src/b.cs", "src/z.cs"], folder.Writes);
	}

	[Theory]
	[InlineData("a.cs", "SRC")]
	[InlineData("src/b.cs", "APP")]
	[InlineData("src/App", "B.cs")]
	[InlineData("a.cs", "x/y")]
	[InlineData("a.cs", "..")]
	[InlineData("missing.cs", "x.cs")]
	[InlineData("", "x")]
	public async Task Rename_RefusesATakenNameIgnoringCase_ASeparatorAMissingSourceAndTheRoot(string path, string newName)
	{
		var folder = Folder();

		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync(path, newName));

		Assert.Empty(folder.Writes);
	}

	[Fact]
	public async Task Rename_ACaseOnlyChangeWorks_ForFilesAndFolders()
	{
		var folder = Folder();

		await folder.RenameAsync("a.cs", "A.cs");
		await folder.RenameAsync("src", "Src");

		Assert.True(folder.Files.ContainsKey("A.cs"));
		Assert.False(folder.Files.ContainsKey("a.cs"));
		Assert.True(folder.Files.ContainsKey("Src/App/c.cs"));
		Assert.Equal(3, folder.Files.Count);
	}

	[Fact]
	public async Task Rename_ToTheSameName_ChangesNothing()
	{
		var folder = Folder();

		await folder.RenameAsync("a.cs", "a.cs");

		Assert.Empty(folder.Writes);
	}

	[Fact]
	public async Task Rename_AFolderMovesEverythingInside_EmptyFoldersToo()
	{
		var folder = Folder();
		folder.Directories.Add("src/Empty");
		folder.SizeOnly["src/big.bin"] = 5;

		await folder.RenameAsync("src", "lib");
		var scan = await folder.ScanAsync([], 1000);

		Assert.Equal(["a.cs", "lib/App/c.cs", "lib/b.cs", "lib/big.bin"], scan.Files.Select(f => f.Path).Order(StringComparer.Ordinal));
		Assert.Equal(["lib", "lib/App", "lib/Empty"], scan.Directories!.Order(StringComparer.Ordinal));
	}

	[Fact]
	public async Task Rename_AFolderNeedsBrowserSupport()
	{
		var folder = Folder();
		folder.FolderMove = false;

		Assert.False(await folder.CanRenameFoldersAsync());
		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync("src", "lib"));

		Assert.True(folder.Files.ContainsKey("src/b.cs"));
	}

	[Fact]
	public async Task Rename_AFileWithoutMove_IsCopiedThenTheOldOneRemoved()
	{
		var folder = Folder();
		folder.FileMove = false;

		await folder.RenameAsync("a.cs", "n.cs");

		Assert.False(folder.Files.ContainsKey("a.cs"));
		Assert.Equal("a", System.Text.Encoding.UTF8.GetString(folder.Files["n.cs"]));
		Assert.True(await folder.CanRenameFoldersAsync());
	}

	[Fact]
	public async Task Rename_AFileWithoutMove_RefusesAboveTheSyncLimit_AndChangesNothing()
	{
		var folder = Folder();
		folder.FileMove = false;
		folder.SizeOnly["big.bin"] = AiChromeProxy.Domain.Sync.SyncLimits.MaxFileSize + 1;

		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync("big.bin", "other.bin"));

		Assert.True(folder.SizeOnly.ContainsKey("big.bin"));
		Assert.False(folder.SizeOnly.ContainsKey("other.bin"));
	}

	[Fact]
	public async Task Rename_AFileWithoutMove_ARefusedFileAtTheLimitStillCopies()
	{
		var folder = Folder();
		folder.FileMove = false;
		folder.SizeOnly["limit.bin"] = AiChromeProxy.Domain.Sync.SyncLimits.MaxFileSize;

		await folder.RenameAsync("limit.bin", "other.bin");

		Assert.True(folder.SizeOnly.ContainsKey("other.bin"));
	}

	[Fact]
	public async Task Rename_AFileWithoutMove_AFailedCopyLeavesNoPartialFile_AndKeepsTheOldOne()
	{
		var folder = Folder();
		folder.FileMove = false;
		folder.CopyFailure = true;

		await Assert.ThrowsAsync<JSException>(() => folder.RenameAsync("a.cs", "n.cs"));

		Assert.True(folder.Files.ContainsKey("a.cs"));
		Assert.False(folder.Files.ContainsKey("n.cs"));
	}

	[Fact]
	public async Task Delete_AFileInTheFolder_RemovesIt_AndAMissingOneIsANoOp()
	{
		var folder = Folder();

		await folder.DeleteAsync("a.cs");
		await folder.DeleteAsync("missing.cs");

		Assert.False(folder.Files.ContainsKey("a.cs"));
		Assert.Equal(["a.cs"], folder.Writes);
	}

	[Fact]
	public async Task Delete_AFolderNeedsRecursive()
	{
		var folder = Folder();

		await Assert.ThrowsAsync<JSException>(() => folder.DeleteAsync("src"));
		await Assert.ThrowsAsync<JSException>(() => folder.DeleteAsync("src", false));

		Assert.Equal(3, folder.Files.Count);
	}

	[Fact]
	public async Task Delete_AFolderRecursively_RemovesFilesAndFolders()
	{
		var folder = Folder();
		folder.Directories.Add("src/Empty");

		await folder.DeleteAsync("src", true);
		var scan = await folder.ScanAsync([], 1000);

		Assert.Equal(["a.cs"], scan.Files.Select(f => f.Path));
		Assert.Empty(scan.Directories!);
		Assert.Equal(["src"], folder.Writes);
	}

	[Fact]
	public async Task Delete_TheRootIsRefused()
	{
		var folder = Folder();

		await Assert.ThrowsAsync<JSException>(() => folder.DeleteAsync(string.Empty, true));

		Assert.Equal(3, folder.Files.Count);
	}

	[Fact]
	public async Task CountFiles_CountsEveryFileUnderTheFolder_ExcludedOnesToo()
	{
		var folder = Folder();
		folder.Write("src/bin/x.dll", "x");
		folder.Write(".env", "k");
		folder.SizeOnly["src/big.bin"] = 99;

		Assert.Equal(new FileCount(4, false), await folder.CountFilesAsync("src", 1000));
		Assert.Equal(new FileCount(1, false), await folder.CountFilesAsync("src/App", 1000));
		await Assert.ThrowsAsync<JSException>(() => folder.CountFilesAsync("missing", 1000));
		await Assert.ThrowsAsync<JSException>(() => folder.CountFilesAsync(string.Empty, 1000));
	}

	[Fact]
	public async Task CountFiles_StopsAtTheEntryLimit_AndSaysSo()
	{
		var folder = Folder();

		Assert.Equal(new FileCount(1, true), await folder.CountFilesAsync("src", 1));
		Assert.Equal(new FileCount(2, false), await folder.CountFilesAsync("src", 2));
	}

	[Fact]
	public async Task Scan_LeavesOutFoldersWhoseListingFailed()
	{
		var folder = Folder();
		folder.UnlistedDirectories.Add("src/App");
		folder.PartlyListedDirectories.Add("src");

		var scan = await folder.ScanAsync([], 1000);

		Assert.Empty(scan.Directories!);
		Assert.Contains("src/App/", scan.Skipped!);
	}

	private static FakeFolder Folder()
	{
		var folder = new FakeFolder { WriteAccess = true };
		folder.Write("a.cs", "a");
		folder.Write("src/b.cs", "b");
		folder.Write("src/App/c.cs", "c");
		return folder;
	}
}
