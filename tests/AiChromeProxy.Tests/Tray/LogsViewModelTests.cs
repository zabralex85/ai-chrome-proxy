using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Clef;
using AiChromeProxy.Tray.ViewModels;

namespace AiChromeProxy.Tests.Tray;

public sealed class LogsViewModelTests : IDisposable
{
	private readonly DataDirectory _dataDir = new(Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N")));

	[Fact]
	public void NoLogs_ExplainsWhere()
	{
		var vm = new LogsViewModel(_dataDir);

		Assert.Empty(vm.Files);
		Assert.Null(vm.SelectedFile);
		Assert.Equal($"No log files yet in {_dataDir.Logs}.", vm.Error);
	}

	[Fact]
	public void Files_NewestFirst_NewestSelectedAndLoaded()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));

		var vm = new LogsViewModel(_dataDir);

		Assert.Equal(["server-20261002.clef", "server-20261001.clef"], vm.Files);
		Assert.Equal("server-20261002.clef", vm.SelectedFile);
		Assert.Equal(["new"], Messages(vm));
	}

	[Fact]
	public void SelectOlderFile_LoadsIt()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));
		var vm = new LogsViewModel(_dataDir);

		vm.SelectedFile = "server-20261001.clef";

		Assert.Equal(["old"], Messages(vm));
	}

	[Fact]
	public void Filters_LevelAndText()
	{
		Write("server-20261002.clef", ("Debug", "noise"), ("Information", "Now listening"), ("Warning", "Disk low"), ("Error", "Disk failed"));
		var vm = new LogsViewModel(_dataDir);
		Assert.Equal(["Now listening", "Disk low", "Disk failed"], Messages(vm));

		vm.MinimumLevel = ClefLevel.Warning;
		Assert.Equal(["Disk low", "Disk failed"], Messages(vm));

		vm.FilterText = "failed";
		Assert.Equal(["Disk failed"], Messages(vm));

		vm.MinimumLevel = ClefLevel.Verbose;
		vm.FilterText = string.Empty;
		Assert.Equal(["noise", "Now listening", "Disk low", "Disk failed"], Messages(vm));
	}

	[Fact]
	public void Follow_AppendsNewEntries_ThroughTheFilter()
	{
		Write("server-20261002.clef", ("Information", "one"));
		var vm = new LogsViewModel(_dataDir) { MinimumLevel = ClefLevel.Information };

		Append("server-20261002.clef", ("Debug", "hidden"), ("Warning", "two"));
		vm.Poll();

		Assert.Equal(["one", "two"], Messages(vm));
	}

	[Fact]
	public void FollowOff_PollDoesNothing()
	{
		Write("server-20261002.clef", ("Information", "one"));
		var vm = new LogsViewModel(_dataDir) { Follow = false };

		Append("server-20261002.clef", ("Information", "two"));
		vm.Poll();

		Assert.Equal(["one"], Messages(vm));
	}

	[Fact]
	public void Follow_DailyRollOver_SwitchesToNewFile_KeepsList()
	{
		Write("server-20261002.clef", ("Information", "yesterday"));
		var vm = new LogsViewModel(_dataDir);

		Write("server-20261003.clef", ("Information", "today"));
		vm.Poll();

		Assert.Equal("server-20261003.clef", vm.SelectedFile);
		Assert.Equal(["server-20261003.clef", "server-20261002.clef"], vm.Files);
		Assert.Equal(["today"], Messages(vm));
	}

	[Fact]
	public void SelectedFileDeleted_ErrorShown_FileDropsOutOfList()
	{
		Write("server-20261001.clef", ("Information", "old"));
		Write("server-20261002.clef", ("Information", "new"));
		var vm = new LogsViewModel(_dataDir) { SelectedFile = "server-20261001.clef" };

		File.Delete(Path.Combine(_dataDir.Logs, "server-20261001.clef"));
		vm.Follow = false;
		vm.SelectedFile = "server-20261002.clef";
		vm.SelectedFile = "server-20261001.clef";

		Assert.NotNull(vm.Error);
		Assert.Empty(vm.Entries);
		vm.Follow = true;
		vm.Poll();
		Assert.Equal(["server-20261002.clef"], vm.Files);
	}

	public void Dispose()
	{
		if (Directory.Exists(_dataDir.Root))
		{
			Directory.Delete(_dataDir.Root, recursive: true);
		}
	}

	private static List<string> Messages(LogsViewModel vm) => [.. vm.Entries.Select(e => e.Message)];

	private void Write(string file, params (string Level, string Message)[] entries)
	{
		Directory.CreateDirectory(_dataDir.Logs);
		File.WriteAllText(Path.Combine(_dataDir.Logs, file), string.Empty);
		Append(file, entries);
	}

	private void Append(string file, params (string Level, string Message)[] entries)
	{
		var lines = entries.Select(e => $$"""{"@t":"2026-10-02T10:00:00Z","@l":"{{e.Level}}","@m":"{{e.Message}}"}""" + "\n");
		File.AppendAllText(Path.Combine(_dataDir.Logs, file), string.Concat(lines));
	}
}
