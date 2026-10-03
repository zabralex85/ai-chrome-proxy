namespace AiChromeProxy.Application.Chat;

/// <summary>A running agent: its stdout lines, its exit and a way to stop it.</summary>
public interface IAgentProcess : IDisposable
{
	/// <summary>The process's stdout, one line at a time, until it closes. Read once.</summary>
	IAsyncEnumerable<string> Lines { get; }

	/// <summary>Completes with the exit code once the process has ended and its stderr was read.</summary>
	Task<int> Exited { get; }

	/// <summary>The tail of what the process wrote to stderr so far (empty when nothing); the error text of a run that ended without a result line.</summary>
	string Stderr { get; }

	/// <summary>Kills the process and its children; no-op when it already ended.</summary>
	void Kill();
}
