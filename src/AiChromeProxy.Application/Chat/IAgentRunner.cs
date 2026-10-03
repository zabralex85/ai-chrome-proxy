namespace AiChromeProxy.Application.Chat;

/// <summary>Starts agent processes (Claude Code in production, a fake in tests).</summary>
public interface IAgentRunner
{
	/// <summary>Starts the agent for <paramref name="run"/>, its prompt already written to stdin.</summary>
	/// <exception cref="FileNotFoundException">The agent command was not found; the message says what to configure.</exception>
	/// <exception cref="InvalidOperationException">The run's arguments cannot be passed safely to the resolved command.</exception>
	Task<IAgentProcess> StartAsync(AgentRun run, CancellationToken ct);
}
