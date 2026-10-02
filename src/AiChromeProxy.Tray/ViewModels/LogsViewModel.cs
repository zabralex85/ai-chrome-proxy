using System.Collections.ObjectModel;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Tray.Clef;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AiChromeProxy.Tray.ViewModels;

/// <summary>Logs window: CLEF files in <c>&lt;DataDir&gt;\logs</c> (newest first), level/text filters and Follow (tail the current file).</summary>
public sealed partial class LogsViewModel : ObservableObject
{
	private readonly string _logsDir;
	private readonly List<LogEntry> _all = [];
	private ClefTail? _tail;

	public LogsViewModel(DataDirectory dataDir)
	{
		_logsDir = dataDir.Logs;
		RefreshFiles();
		SelectedFile = Files.FirstOrDefault();
		if (SelectedFile is null)
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

		RefreshFiles();
		if (Files.FirstOrDefault() is { } newest && newest != SelectedFile)
		{
			SelectedFile = newest;
			return;
		}

		Append(ReadNew());
	}

	partial void OnSelectedFileChanged(string? value)
	{
		_all.Clear();
		Entries = [];
		Error = null;
		_tail = value is null ? null : new ClefTail(Path.Combine(_logsDir, value));
		Append(ReadNew());
	}

	partial void OnMinimumLevelChanged(ClefLevel value) => Entries = [.. _all.Where(Visible)];

	partial void OnFilterTextChanged(string value) => Entries = [.. _all.Where(Visible)];

	private bool Visible(LogEntry entry) => entry.Matches(MinimumLevel, FilterText);

	private IReadOnlyList<LogEntry> ReadNew()
	{
		try
		{
			return _tail?.ReadNew() ?? [];
		}
		catch (IOException ex)
		{
			Error = ex.Message;
			return [];
		}
	}

	private void Append(IReadOnlyList<LogEntry> entries)
	{
		_all.AddRange(entries);
		foreach (var entry in entries.Where(Visible))
		{
			Entries.Add(entry);
		}
	}

	/// <summary>Syncs <see cref="Files"/> in place (no reset), so the bound selection survives a refresh.</summary>
	private void RefreshFiles()
	{
		var names = Directory.Exists(_logsDir)
			? Directory.GetFiles(_logsDir, "*.clef").Select(Path.GetFileName).OfType<string>().OrderDescending(StringComparer.OrdinalIgnoreCase).ToList()
			: [];
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
	}
}
