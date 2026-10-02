using System.Text;

namespace AiChromeProxy.Tray.Clef;

/// <summary>Reads a CLEF file incrementally while the Server keeps writing it (shared read; a trailing partial line waits for its newline).</summary>
public sealed class ClefTail(string path)
{
	private long _position;

	public string Path { get; } = path;

	/// <returns>Entries completed since the previous call (all of them on the first call).</returns>
	public IReadOnlyList<LogEntry> ReadNew()
	{
		using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
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
