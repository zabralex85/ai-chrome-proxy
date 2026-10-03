using AiChromeProxy.Application.Chat;
using AiChromeProxy.Infrastructure.Chat;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class ClaudeRunnerTests : IDisposable
{
	private static readonly string FakeAgent = Path.Combine(AppContext.BaseDirectory, "AiChromeProxy.FakeAgent.exe");

	private readonly string _folder = Path.Combine(TempRootCleanup.Root, Guid.NewGuid().ToString("N"));

	public ClaudeRunnerTests() => Directory.CreateDirectory(_folder);

	public void Dispose() => Directory.Delete(_folder, recursive: true);

	[Fact]
	public async Task Start_RunsTheCommand_PromptOnStdin_LinesFromStdout()
	{
		var script = Path.Combine(_folder, "script.jsonl");
		var stdin = Path.Combine(_folder, "stdin.txt");
		await File.WriteAllLinesAsync(script, ["{\"type\":\"a\"}", "{\"type\":\"b\"}"], TestContext.Current.CancellationToken);
		var runner = Runner(new AgentOptions { Env = { ["FAKE_AGENT_SCRIPT"] = script, ["FAKE_AGENT_STDIN_FILE"] = stdin, ["FAKE_AGENT_DELAY_MS"] = "5" } });

		using (var process = await runner.StartAsync(new AgentRun(_folder, "héllo\nworld"), TestContext.Current.CancellationToken))
		{
			var lines = new List<string>();
			await foreach (var line in process.Lines.WithCancellation(TestContext.Current.CancellationToken))
			{
				lines.Add(line);
			}

			Assert.Equal(["{\"type\":\"a\"}", "{\"type\":\"b\"}"], lines);
			Assert.Equal(0, await process.Exited);
			Assert.Equal(string.Empty, process.Stderr);
		}

		Assert.Equal("héllo\nworld", await File.ReadAllTextAsync(stdin, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Start_NonZeroExit_ReportsExitCodeAndStderr()
	{
		var runner = Runner(new AgentOptions { Env = { ["FAKE_AGENT_EXIT_CODE"] = "3", ["FAKE_AGENT_STDERR"] = "not logged in" } });

		using (var process = await runner.StartAsync(new AgentRun(_folder, "hi"), TestContext.Current.CancellationToken))
		{
			Assert.Equal(3, await process.Exited);
			Assert.Equal("not logged in", process.Stderr);
		}
	}

	[Fact]
	public async Task Kill_StopsALongRun()
	{
		var script = Path.Combine(_folder, "slow.jsonl");
		await File.WriteAllLinesAsync(script, Enumerable.Repeat("x", 100), TestContext.Current.CancellationToken);
		var runner = Runner(new AgentOptions { Env = { ["FAKE_AGENT_SCRIPT"] = script, ["FAKE_AGENT_DELAY_MS"] = "500" } });

		using (var process = await runner.StartAsync(new AgentRun(_folder, "hi"), TestContext.Current.CancellationToken))
		{
			process.Kill();

			await process.Exited.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
			process.Kill();
		}
	}

	[Fact]
	public async Task Start_UnknownCommand_ThrowsWithAdvice()
	{
		var runner = Runner(new AgentOptions { Command = "no-such-agent-command-xyz" });

		var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => runner.StartAsync(new AgentRun(_folder, "hi"), TestContext.Current.CancellationToken));

		Assert.Contains("Agent:Command", ex.Message);
	}

	[Fact]
	public async Task Start_RootedCommandThatDoesNotExist_ThrowsWithAdvice()
	{
		var runner = Runner(new AgentOptions { Command = Path.Combine(_folder, "missing", "claude.exe") });

		var ex = await Assert.ThrowsAsync<FileNotFoundException>(() => runner.StartAsync(new AgentRun(_folder, "hi"), TestContext.Current.CancellationToken));

		Assert.Contains("Agent:Command", ex.Message);
	}

	[Fact]
	public async Task Start_CmdShim_RefusesOtherArgumentsCmdWouldInterpret()
	{
		var shim = Path.Combine(_folder, "claude.cmd");
		await File.WriteAllTextAsync(shim, "@exit /b 0", TestContext.Current.CancellationToken);
		var runner = Runner(new AgentOptions { Command = shim });

		await Assert.ThrowsAsync<InvalidOperationException>(() => runner.StartAsync(new AgentRun(_folder, "hi", Model: "a&b"), TestContext.Current.CancellationToken));
		await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(new AgentOptions { Command = shim, Args = ["x|y"] }).StartAsync(new AgentRun(_folder, "hi"), TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Start_CmdShim_DropsUnsafeAllowRules_PassesTheRestEndToEnd()
	{
		var shim = Path.Combine(_folder, "my shim", "claude.cmd");
		Directory.CreateDirectory(Path.GetDirectoryName(shim)!);
		await File.WriteAllTextAsync(shim, "@echo off" + Environment.NewLine + "\"" + FakeAgent + "\" %*" + Environment.NewLine, TestContext.Current.CancellationToken);
		var received = Path.Combine(_folder, "args.txt");
		var options = new AgentOptions { Command = shim, Env = { ["FAKE_AGENT_ARGS_FILE"] = received } };
		var run = new AgentRun(_folder, "hi", Permissions: "ask", ApprovalUrl: "http://127.0.0.1:1/mcp/approve", ApprovalToken: "t", AllowedTools: ["Bash(a && b)", "Read", "Bash(date +%Y)"]);

		using (var process = await Runner(options).StartAsync(run, TestContext.Current.CancellationToken))
		{
			Assert.Equal(0, await process.Exited);
		}

		var expected = ClaudeArguments.Build(run with { AllowedTools = ["Read"] }, options);
		Assert.Equal(expected, await File.ReadAllLinesAsync(received, TestContext.Current.CancellationToken));
		Assert.Equal("Read", expected[expected.ToList().IndexOf("--allowedTools") + 1]);
	}

	[Fact]
	public void IdleTimeout_FromOptions()
	{
		Assert.Equal(TimeSpan.FromMinutes(10), Runner(new AgentOptions()).IdleTimeout);
		Assert.Equal(TimeSpan.FromSeconds(5), Runner(new AgentOptions { IdleTimeout = TimeSpan.FromSeconds(5) }).IdleTimeout);
	}

	private static ClaudeRunner Runner(AgentOptions options)
	{
		if (options.Command == "claude")
		{
			options.Command = FakeAgent;
		}

		return new ClaudeRunner(Options.Create(options), NullLogger<ClaudeRunner>.Instance);
	}
}
