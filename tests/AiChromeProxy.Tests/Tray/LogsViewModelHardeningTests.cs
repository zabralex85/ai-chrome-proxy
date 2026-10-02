using System.Security.AccessControl;
using System.Security.Principal;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class LogsViewModelHardeningTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void SelectOlderFile_TurnsFollowOff_PollKeepsSelection()
	{
		Write("server-20261001.clef", "old");
		Write("server-20261002.clef", "new");
		var vm = new LogsViewModel(_dataDir);

		vm.SelectedFile = "server-20261001.clef";
		vm.Poll();

		Assert.False(vm.Follow);
		Assert.Equal("server-20261001.clef", vm.SelectedFile);
		Assert.Equal(["old"], Messages(vm));
	}

	[Fact]
	public void FollowBackOn_JumpsToNewest()
	{
		Write("server-20261001.clef", "old");
		Write("server-20261002.clef", "new");
		var vm = new LogsViewModel(_dataDir) { SelectedFile = "server-20261001.clef" };

		vm.Follow = true;

		Assert.Equal("server-20261002.clef", vm.SelectedFile);
		Assert.True(vm.Follow);
	}

	[Fact]
	public void InaccessibleLogsFolder_ShowsError_ThenClearsWhenValid()
	{
		Write("server-20261002.clef", "one");
		var vm = new LogsViewModel(_dataDir);
		var dir = new DirectoryInfo(_dataDir.Logs);
		var rule = Deny(dir, FileSystemRights.ListDirectory);
		try
		{
			vm.Poll();
			Assert.NotNull(vm.Error);
		}
		finally
		{
			Allow(dir, rule);
		}

		vm.Poll();
		Assert.Null(vm.Error);
	}

	[Fact]
	public void InaccessibleLogsFolder_InConstructor_DoesNotThrow()
	{
		Write("server-20261002.clef", "one");
		var dir = new DirectoryInfo(_dataDir.Logs);
		var rule = Deny(dir, FileSystemRights.ListDirectory);
		try
		{
			var vm = new LogsViewModel(_dataDir);
			Assert.NotNull(vm.Error);
		}
		finally
		{
			Allow(dir, rule);
		}
	}

	[Fact]
	public void UnreadableFile_ShowsError_ThenClearsAfterSuccessfulRead()
	{
		Write("server-20261002.clef", "one");
		var vm = new LogsViewModel(_dataDir);
		var file = new FileInfo(Path.Combine(_dataDir.Logs, "server-20261002.clef"));
		var rule = Deny(file, FileSystemRights.ReadData);
		try
		{
			Append("server-20261002.clef", "two");
			vm.Poll();
			Assert.NotNull(vm.Error);
		}
		finally
		{
			Allow(file, rule);
		}

		vm.Poll();
		Assert.Null(vm.Error);
		Assert.Equal(["one", "two"], Messages(vm));
	}

	[Fact]
	public void HugeFile_LoadsOnlyTheTailWindow_SkippingPartialFirstLine()
	{
		Write("server-20261002.clef", Enumerable.Range(1, 200).Select(i => $"m{i:000}").ToArray());
		var lineLength = new FileInfo(Path.Combine(_dataDir.Logs, "server-20261002.clef")).Length / 200;

		var vm = new LogsViewModel(_dataDir, readWindowBytes: (lineLength * 10) + 5);

		Assert.InRange(vm.Entries.Count, 9, 10);
		Assert.Equal("m200", vm.Entries[^1].Message);
		Assert.All(vm.Entries, e => Assert.StartsWith("m", e.Message));
	}

	[Fact]
	public void Follow_CapsEntries_KeepsNewest()
	{
		Write("server-20261002.clef", "m1", "m2", "m3");
		var vm = new LogsViewModel(_dataDir, maxEntries: 5);

		Append("server-20261002.clef", "m4", "m5", "m6", "m7");
		vm.Poll();

		Assert.Equal(["m3", "m4", "m5", "m6", "m7"], Messages(vm));
	}

	[Fact]
	public void ShrunkFile_ReadsFromStartAgain()
	{
		Write("server-20261002.clef", "aaaaaaaaaa", "bbbbbbbbbb");
		var vm = new LogsViewModel(_dataDir);

		Write("server-20261002.clef", "c");
		vm.Poll();

		Assert.Equal(["aaaaaaaaaa", "bbbbbbbbbb", "c"], Messages(vm));
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static List<string> Messages(LogsViewModel vm) => [.. vm.Entries.Select(e => e.Message)];

	private static FileSystemAccessRule Deny(DirectoryInfo dir, FileSystemRights rights)
	{
		var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
		var acl = dir.GetAccessControl();
		acl.AddAccessRule(rule);
		dir.SetAccessControl(acl);
		return rule;
	}

	private static FileSystemAccessRule Deny(FileInfo file, FileSystemRights rights)
	{
		var rule = new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, rights, AccessControlType.Deny);
		var acl = file.GetAccessControl();
		acl.AddAccessRule(rule);
		file.SetAccessControl(acl);
		return rule;
	}

	private static void Allow(DirectoryInfo dir, FileSystemAccessRule rule)
	{
		var acl = dir.GetAccessControl();
		acl.RemoveAccessRule(rule);
		dir.SetAccessControl(acl);
	}

	private static void Allow(FileInfo file, FileSystemAccessRule rule)
	{
		var acl = file.GetAccessControl();
		acl.RemoveAccessRule(rule);
		file.SetAccessControl(acl);
	}

	private void Write(string file, params string[] messages)
	{
		Directory.CreateDirectory(_dataDir.Logs);
		File.WriteAllText(Path.Combine(_dataDir.Logs, file), string.Empty);
		Append(file, messages);
	}

	private void Append(string file, params string[] messages)
	{
		var lines = messages.Select(m => $$"""{"@t":"2026-10-02T10:00:00Z","@l":"Information","@m":"{{m}}"}""" + "\n");
		File.AppendAllText(Path.Combine(_dataDir.Logs, file), string.Concat(lines));
	}
}
