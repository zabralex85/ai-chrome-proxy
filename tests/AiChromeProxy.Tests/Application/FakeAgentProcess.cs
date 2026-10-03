using System.Threading.Channels;
using AiChromeProxy.Application.Chat;

namespace AiChromeProxy.Tests.Application;

/// <summary>A scripted agent process: the test writes stdout lines and ends it; <see cref="Kill"/> ends it with -1.</summary>
public sealed class FakeAgentProcess : IAgentProcess
{
	private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
	private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

	public FakeAgentProcess(AgentRun run)
	{
		Run = run;
	}

	/// <summary>What the runner was asked to start.</summary>
	public AgentRun Run { get; }

	public IAsyncEnumerable<string> Lines => _lines.Reader.ReadAllAsync();

	public Task<int> Exited => _exited.Task;

	public string Stderr { get; set; } = string.Empty;

	public bool Killed { get; private set; }

	public bool Disposed { get; private set; }

	/// <summary>Writes stdout lines.</summary>
	public void Write(params string[] lines)
	{
		foreach (var line in lines)
		{
			_lines.Writer.TryWrite(line);
		}
	}

	/// <summary>Closes stdout and exits with <paramref name="code"/>.</summary>
	public void Exit(int code = 0)
	{
		_lines.Writer.TryComplete();
		_exited.TrySetResult(code);
	}

	public void Kill()
	{
		Killed = true;
		Exit(-1);
	}

	public void Dispose() => Disposed = true;
}
