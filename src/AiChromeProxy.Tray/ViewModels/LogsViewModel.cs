using System.Collections.ObjectModel;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Clef;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Logs window: CLEF files in <c>&lt;DataDir&gt;\logs</c> (newest first), level/text filters and Follow (tail the current file).</summary>
public sealed partial class LogsViewModel : ObservableObject
{
	/// <summary>ponytail: at most 50,000 entries stay in memory (oldest dropped); a virtualised/on-disk view is the upgrade if more history is needed.</summary>
	public const int MaxEntries = 50_000;

	private readonly string _logsDir;
	private readonly int _maxEntries;
	private readonly long _readWindow;
	private readonly List<LogEntry> _all = [];
	private ClefTail? _tail;

	public LogsViewModel(DataDirectory dataDir, int maxEntries = MaxEntries, long readWindowBytes = ClefTail.InitialReadWindow)
	{
		_logsDir = dataDir.Logs;
		_maxEntries = maxEntries;
		_readWindow = readWindowBytes;
		var listed = RefreshFiles();
		SelectedFile = Files.FirstOrDefault();
		if (listed && SelectedFile is null)
		{
			Error = $"No log files yet in {_logsDir}.";
		}
	}

	/// <summary>File names, newest first.</summary>
	public ObservableCollection<string> Files { get; } = [];

	public IReadOnlyList<ClefLevel> Levels { get; } = Enum.GetValues<ClefLevel>();

	/// <summary>Entries of the selected file that pass the filters.</summary>
	[ObservableProperty]
	public partial ObservableCollection<LogEntry> Entries { get; private set; } = [];

	[ObservableProperty]
	public partial string? SelectedFile { get; set; }

	[ObservableProperty]
	public partial ClefLevel MinimumLevel { get; set; } = ClefLevel.Information;

	[ObservableProperty]
	public partial string FilterText { get; set; } = string.Empty;

	/// <summary>While on, <see cref="Poll"/> appends new entries and jumps to a newer file (daily roll-over).</summary>
	[ObservableProperty]
	public partial bool Follow { get; set; } = true;

	[ObservableProperty]
	public partial string? Error { get; private set; }

	/// <summary>Called by the window once a second.</summary>
	public void Poll()
	{
		if (!Follow)
		{
			return;
		}

		if (!RefreshFiles())
		{
			return;
		}

		if (Files.FirstOrDefault() is { } newest && newest != SelectedFile)
		{
			SelectedFile = newest;
			return;
		}

		Append(ReadNew());
	}

	/// <summary>Picking an older file turns Follow off; turning it back on jumps to the newest file.</summary>
	partial void OnFollowChanged(bool value)
	{
		if (value)
		{
			Poll();
		}
	}

	partial void OnSelectedFileChanged(string? value)
	{
		_all.Clear();
		Entries = [];
		Error = null;
		if (value != Files.FirstOrDefault())
		{
			Follow = false;
		}

		_tail = value is null ? null : new ClefTail(Path.Combine(_logsDir, value), _readWindow);
		Append(ReadNew());
	}

	partial void OnMinimumLevelChanged(ClefLevel value) => Entries = [.. _all.Where(Visible)];

	partial void OnFilterTextChanged(string value) => Entries = [.. _all.Where(Visible)];

	private bool Visible(LogEntry entry) => entry.Matches(MinimumLevel, FilterText);

	private IReadOnlyList<LogEntry> ReadNew()
	{
		try
		{
			var read = _tail?.ReadNew() ?? [];
			if (_tail is not null)
			{
				Error = null;
			}

			return read;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Error = ex.Message;
			return [];
		}
	}

	private void Append(IReadOnlyList<LogEntry> entries)
	{
		_all.AddRange(entries);
		if (_all.Count > _maxEntries)
		{
			_all.RemoveRange(0, _all.Count - _maxEntries);
			Entries = [.. _all.Where(Visible)];
			return;
		}

		foreach (var entry in entries.Where(Visible))
		{
			Entries.Add(entry);
		}
	}

	/// <summary>Syncs <see cref="Files"/> in place (no reset), so the bound selection survives a refresh.</summary>
	private bool RefreshFiles()
	{
		List<string> names;
		try
		{
			names = Directory.Exists(_logsDir)
				? Directory.GetFiles(_logsDir, "*.clef").Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.OrdinalIgnoreCase).ToList()
				: [];
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			Error = ex.Message;
			return false;
		}

		foreach (var gone in Files.Except(names).ToList())
		{
			Files.Remove(gone);
		}

		for (var i = 0; i < names.Count; i++)
		{
			if (i >= Files.Count || Files[i] != names[i])
			{
				Files.Insert(i, names[i]);
			}
		}

		return true;
	}
}
