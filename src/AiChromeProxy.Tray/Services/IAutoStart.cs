namespace AiChromeProxy.Tray.Services;

/// <summary>"Start with Windows" for the tray (per user, at login).</summary>
public interface IAutoStart
{
	bool IsEnabled { get; set; }
}
