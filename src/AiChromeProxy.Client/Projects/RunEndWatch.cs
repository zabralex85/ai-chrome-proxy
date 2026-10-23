namespace AiChromeProxy.Client.Projects;

/// <summary>Spots the end of a run of the viewed project: its edits are on the server then, so its files are listed again.</summary>
public sealed class RunEndWatch
{
	private bool _wasRunning;

	/// <summary>
	/// True once when the viewed project's run ends. Only a chat showing that project counts (<paramref name="chatRepo"/>): while it still shows
	/// the folder's own repo, or nothing is viewed, the watch starts over.
	/// </summary>
	public bool Ended(string? viewing, string? chatRepo, bool running)
	{
		if (viewing is null || !string.Equals(viewing, chatRepo, StringComparison.OrdinalIgnoreCase))
		{
			_wasRunning = false;
			return false;
		}

		// Updated before the caller refreshes: a re-entrant call from that refresh does not fire again.
		var ended = _wasRunning && !running;
		_wasRunning = running;
		return ended;
	}
}
