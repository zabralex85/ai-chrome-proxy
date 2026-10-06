namespace AiChromeProxy.Domain.Chat;

/// <summary>Status values of <see cref="ClaudeMcpServerRow"/> and <see cref="ClaudePluginRow"/>.</summary>
public static class ClaudeToolStatuses
{
	public const string Connected = "connected";
	public const string Failed = "failed";
	public const string NeedsAuth = "needs-auth";
	public const string NotConfigured = "not-configured";
	public const string Pending = "pending";

	/// <summary>Off in this project: the run did not load it.</summary>
	public const string Off = "off";

	/// <summary>Off in this project and no longer installed.</summary>
	public const string Missing = "missing";

	public const string Unknown = "unknown";

	/// <summary>The value when it is one of these statuses, else <see cref="Unknown"/>.</summary>
	public static string Normalize(string? status) =>
		status is Connected or Failed or NeedsAuth or NotConfigured or Pending or Off or Missing ? status : Unknown;
}
