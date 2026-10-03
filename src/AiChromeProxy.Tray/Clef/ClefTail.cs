using System.Text;

namespace AiChromeProxy.Tray.Clef;

/// <summary>Reads a CLEF file incrementally while the Server keeps writing it (shared read; a trailing partial line waits for its newline).</summary>
public sealed class ClefTail(string path, long maxInitialBytes = ClefTail.InitialReadWindow)
{
	/// <summary>ponytail: a file is first read from its last 16 MB only; older lines are not shown. Page backwards if that ever matters.</summary>
	public const long InitialReadWindow = 16L * 1024 * 1024;

	private long _position;
	private bool _started;

	public string Path { get; } = path;

	/// <returns>Entries completed since the previous call (all of them on the first call).</returns>
	public IReadOnlyList<LogEntry> ReadNew()
	{
		using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
		if (!_started)
		{
			_started = true;
			_position = Math.Max(0, stream.Length - maxInitialBytes);
		}
		else if (stream.Length < _position)
		{
			_position = 0;
		}

		if (stream.Length <= _position)
		{
			return [];
		}

		// ponytail: reads the whole unread tail into memory; fine for daily files kept 14 days, stream it if files grow to hundreds of MB.
		var bytes = new byte[stream.Length - _position];
		stream.Position = _position;
		stream.ReadExactly(bytes);

		var end = Array.LastIndexOf(bytes, (byte)'\n');
		if (end < 0)
		{
			return [];
		}

		_position += end + 1;
		return Encoding.UTF8.GetString(bytes, 0, end + 1)
			.Split('\n')
			.Select(line => ClefParser.Parse(line.TrimEnd('\r')))
			.OfType<LogEntry>()
			.ToList();
	}
}
