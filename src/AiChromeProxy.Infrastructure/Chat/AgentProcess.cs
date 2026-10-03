using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>
/// The real agent child process: in the host's kill-on-close Job object, the prompt written to stdin (then closed), stdout read as lines,
/// stderr kept (its tail) and forwarded line by line.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Process and Win32 glue; the arguments and decisions are in ClaudeArguments and ClaudeRunner, the process itself runs against the fake agent.")]
public sealed class AgentProcess : IAgentProcess
{
	private const int StderrTailChars = 4000;

	private readonly Process _process;
	private readonly StringBuilder _stderr = new();
	private readonly Task _stderrDone;

	private AgentProcess(Process process, Action<string> onStderr)
	{
		_process = process;
		_stderrDone = Task.Run(() => PumpStderrAsync(onStderr));
		Exited = WaitAsync();
	}

	public IAsyncEnumerable<string> Lines => ReadLinesAsync();

	public Task<int> Exited { get; }

	public string Stderr
	{
		get
		{
			lock (_stderr)
			{
				return _stderr.ToString().Trim();
			}
		}
	}

	/// <summary>Starts <paramref name="file"/> (with <paramref name="args"/>, or with the ready-made <paramref name="rawArguments"/> line) in <paramref name="workingDirectory"/> and writes <paramref name="prompt"/> to its stdin.</summary>
	/// <exception cref="Win32Exception">The file could not be started, or the job object failed (then the process is killed).</exception>
	public static AgentProcess Start(string file, IReadOnlyList<string> args, string workingDirectory, IReadOnlyDictionary<string, string> env, string prompt, Action<string> onStderr, string? rawArguments = null)
	{
		KillOnCloseJob.Ensure();
		var info = new ProcessStartInfo(file)
		{
			WorkingDirectory = workingDirectory,
			UseShellExecute = false,
			CreateNoWindow = true,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardInputEncoding = new UTF8Encoding(false),
			StandardOutputEncoding = new UTF8Encoding(false),
			StandardErrorEncoding = new UTF8Encoding(false),
		};
		if (rawArguments is not null)
		{
			info.Arguments = rawArguments;
		}
		else
		{
			foreach (var arg in args)
			{
				info.ArgumentList.Add(arg);
			}
		}

		foreach (var (key, value) in env)
		{
			info.Environment[key] = value;
		}

		var process = new Process { StartInfo = info };
		process.Start();
		var started = new AgentProcess(process, onStderr);
		try
		{
			KillOnCloseJob.Assign(process);
		}
		catch (Win32Exception)
		{
			started.Kill();
			started.Dispose();
			throw;
		}

		_ = started.WritePromptAsync(prompt);
		return started;
	}

	public void Kill()
	{
		try
		{
			_process.Kill(entireProcessTree: true);
		}
		catch (InvalidOperationException)
		{
			// Already exited.
		}
		catch (Exception ex) when (ex is Win32Exception or AggregateException)
		{
			// Already terminating or access denied: the kill-on-close job still ends it with the host.
		}
	}

	public void Dispose() => _process.Dispose();

	private async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct = default)
	{
		while (await _process.StandardOutput.ReadLineAsync(ct) is { } line)
		{
			yield return line;
		}
	}

	private async Task WritePromptAsync(string prompt)
	{
		try
		{
			await _process.StandardInput.WriteAsync(prompt);
			_process.StandardInput.Close();
		}
		catch (IOException)
		{
			// The process ended before reading its stdin; Exited and Stderr tell why.
		}
	}

	private async Task PumpStderrAsync(Action<string> onStderr)
	{
		while (await _process.StandardError.ReadLineAsync() is { } line)
		{
			lock (_stderr)
			{
				_stderr.AppendLine(line);
				if (_stderr.Length > StderrTailChars)
				{
					_stderr.Remove(0, _stderr.Length - StderrTailChars);
				}
			}

			onStderr(line);
		}
	}

	private async Task<int> WaitAsync()
	{
		await _process.WaitForExitAsync();
		await _stderrDone;
		return _process.ExitCode;
	}
}
