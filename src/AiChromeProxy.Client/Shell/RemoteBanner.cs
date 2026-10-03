using AiChromeProxy.Client.Sync;

namespace AiChromeProxy.Client.Shell;

/// <summary>The Explorer banner for server changes that wait: its text and the button that moves them on.</summary>
/// <param name="Text">"N server changes — " (before the button) or "N server changes waiting".</param>
/// <param name="Button">"Allow writing" or "Apply all".</param>
public sealed record RemoteBanner(string Text, string Button)
{
	public const string AllowWriting = "Allow writing";
	public const string ApplyAll = "Apply all";

	/// <summary>Null when nothing waits, or when the next cycle writes it anyway (write access and automatic apply).</summary>
	public static RemoteBanner? Of(SyncEngine engine)
	{
		var waiting = engine.Remote.Count(r => r.Status == RemoteStatus.Waiting);
		if (waiting == 0)
		{
			return null;
		}

		if (!engine.CanWrite)
		{
			return new RemoteBanner(Format.ServerChanges(waiting) + " — ", AllowWriting);
		}

		return engine.Settings.ApplyServerChangesOrDefault ? null : new RemoteBanner(Format.ServerChanges(waiting) + " waiting", ApplyAll);
	}
}
