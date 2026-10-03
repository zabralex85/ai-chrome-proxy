namespace AiChromeProxy.Application.Sync;

/// <summary>Detects edits made directly in the mirror folder (by hand, or by Claude).</summary>
public interface IMirrorWatcher
{
	/// <summary>
	/// Watches &lt;root&gt;\&lt;repo&gt; until disposed; calls <paramref name="changed"/> with the coalesced protocol paths (500 ms after the last event),
	/// or with null when events were lost (buffer overflow) and every file must be checked. Temp files are left out.
	/// A path may be a folder (renamed or deleted); the callback never throws into the watching thread.
	/// </summary>
	IDisposable Watch(string repo, Func<IReadOnlyCollection<string>?, Task> changed);
}
