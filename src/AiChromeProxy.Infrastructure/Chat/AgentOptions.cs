namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>Section <c>Agent</c>: which agent CLI runs and how.</summary>
public sealed class AgentOptions
{
	public const string Section = "Agent";

	/// <summary>The command; a bare name is looked up on <c>PATH</c> as <c>.exe</c>, then <c>.cmd</c>; an absolute path is used as is.</summary>
	public string Command { get; set; } = "claude";

	/// <summary>Extra arguments, appended after the built ones.</summary>
	public string[] Args { get; set; } = [];

	/// <summary>Environment variables merged into the process environment.</summary>
	public Dictionary<string, string> Env { get; set; } = [];

	/// <summary>A run with no output for this long is killed (enforced by the chat service).</summary>
	public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);

	/// <summary>Gets a value indicating whether <see cref="IdleTimeout"/> is positive and fits a timer (or is <see cref="Timeout.InfiniteTimeSpan"/>).</summary>
	public bool HasValidIdleTimeout => IdleTimeout == Timeout.InfiniteTimeSpan || (IdleTimeout > TimeSpan.Zero && IdleTimeout.TotalMilliseconds <= uint.MaxValue - 1);
}
