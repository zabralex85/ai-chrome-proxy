using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using AiChromeProxy.Infrastructure.Hosting;
using AiChromeProxy.Infrastructure.Security;
using AiChromeProxy.Server.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace AiChromeProxy.Tests.Server;

/// <summary>The supervision loop against a fake process and a fake clock: no real <c>cloudflared</c> is ever started.</summary>
public sealed class CloudflaredSupervisorTests
{
	private const string Token = "tunnel-token-secret";
	private const string ConfiguredPath = @"C:\tools\cloudflared.exe";

	private readonly CountingTimeProvider _time = new();
	private readonly ListLogger<CloudflaredSupervisor> _logger = new();
	private readonly ConcurrentQueue<ProcessStartInfo> _starts = new();
	private readonly ConcurrentQueue<FakeProcess> _processes = new();
	private Action<string>? _output;

	private Exception? FailStart { get; set; }

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	public async Task NoToken_NeverStarts_LogsOnce(string token)
	{
		await Create(token).RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		Assert.Empty(_starts);
		Assert.Equal(["Cloudflare Tunnel not configured (Tunnel:Token is empty); cloudflared is not started."], _logger.Messages);
	}

	[Fact]
	public async Task Start_TokenOnlyInChildEnvironment_OutputLogged()
	{
		// Inherited by the child unless removed: cloudflared would log the .NET form in clear, and read any TUNNEL_* as its own setting.
		// Unique names, so tests running in parallel never see a variable they would act on.
		var suffix = Guid.NewGuid().ToString("N");
		string[] inherited = [$"Tunnel__AicpTest{suffix}", $"TUNNEL_AICPTEST{suffix}"];
		ProcessStartInfo info;
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			Task run;
			try
			{
				foreach (var name in inherited)
				{
					Environment.SetEnvironmentVariable(name, "inherited");
				}

				run = Create().RunAsync(cts.Token);
				info = Assert.Single(_starts);
			}
			finally
			{
				foreach (var name in inherited)
				{
					Environment.SetEnvironmentVariable(name, null);
				}
			}

			Assert.Equal(ConfiguredPath, info.FileName);
			Assert.Equal(["tunnel", "--no-autoupdate", "run"], info.ArgumentList);
			Assert.Equal(string.Empty, info.Arguments);
			Assert.Equal(Token, info.Environment[CloudflaredSupervisor.TokenVariable]);
			Assert.Equal([CloudflaredSupervisor.TokenVariable], info.Environment.Keys.Where(k => k.StartsWith("TUNNEL_", StringComparison.OrdinalIgnoreCase)));
			Assert.Contains("PATH", info.Environment.Keys, StringComparer.OrdinalIgnoreCase);

			_output!("INF Registered tunnel connection");
			Assert.Contains("cloudflared: INF Registered tunnel connection", _logger.Messages);
			Assert.DoesNotContain(_logger.Messages, m => m.Contains(Token, StringComparison.Ordinal));

			await cts.CancelAsync();
			await run;
		}
	}

	[Fact]
	public async Task Exits_RestartedWithDoublingBackoff_CappedAt60s()
	{
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);

			foreach (var seconds in new[] { 1, 2, 4, 8, 16, 32, 60, 60 })
			{
				await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(seconds));
			}

			Assert.Contains(_logger.Entries, e => e is (LogLevel.Warning, "cloudflared exited with code 1."));
			Assert.Equal(
				["00:00:01", "00:00:02", "00:00:04", "00:00:08", "00:00:16", "00:00:32", "00:01:00", "00:01:00"],
				_logger.Messages.Where(m => m.StartsWith("Restarting cloudflared in ", StringComparison.Ordinal)).Select(m => m["Restarting cloudflared in ".Length..^1]));
			Assert.All(_processes.SkipLast(1), p => Assert.True(p.Disposed));
			await cts.CancelAsync();
			await run;
		}
	}

	[Fact]
	public async Task RunOfFiveMinutes_ResetsBackoff()
	{
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);
			await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(1));
			await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(2));

			_time.Advance(CloudflaredSupervisor.StableRun);

			await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(1));
			await ExitAndExpectRestartAfterAsync(TimeSpan.FromSeconds(2));
			await cts.CancelAsync();
			await run;
		}
	}

	[Fact]
	public async Task StartFailure_LoggedAsError_RetriedWithBackoff()
	{
		FailStart = new Win32Exception(2, "The system cannot find the file specified");
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);

			await WaitUntilAsync(() => _time.Timers == 1);
			_time.Advance(TimeSpan.FromSeconds(1));
			await WaitUntilAsync(() => _time.Timers == 2);
			FailStart = null;
			_time.Advance(TimeSpan.FromSeconds(2));
			await WaitUntilAsync(() => _processes.Count == 1);

			Assert.Equal(3, _starts.Count);
			Assert.Equal(2, _logger.Entries.Count(e => e is (LogLevel.Error, $"cloudflared could not be started ({ConfiguredPath}).")));
			await cts.CancelAsync();
			await run;
		}
	}

	[Fact]
	public async Task Shutdown_KillsAndDisposesTheProcess_NoRestart()
	{
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);

			await cts.CancelAsync();
			await run;

			var process = Assert.Single(_processes);
			Assert.True(process.Killed);
			Assert.True(process.Disposed);
			Assert.Single(_starts);
		}
	}

	[Fact]
	public async Task Shutdown_DuringBackoff_Returns_NoRestart()
	{
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);
			_processes.Last().Exit(0);
			await WaitUntilAsync(() => _time.Timers == 1);

			await cts.CancelAsync();
			await run;

			Assert.Single(_starts);
			Assert.False(_processes.Last().Killed);
			Assert.True(_processes.Last().Disposed);
		}
	}

	[Fact]
	public async Task AccessCheckDisabled_NeverStarts_LogsOnce()
	{
		await Create(accessEnabled: false).RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

		Assert.Empty(_starts);
		Assert.Equal(["Cloudflare Access check is disabled; cloudflared is not started (never expose a Server without it)."], _logger.Messages);
	}

	[Fact]
	public async Task WaitFailure_LoggedAsError_ProcessKilledAndDisposed_RestartedWithBackoff()
	{
		using (var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
		{
			var run = Create().RunAsync(cts.Token);
			var failed = _processes.Last();

			failed.Fail(new InvalidOperationException("No process is associated with this object."));
			await WaitUntilAsync(() => _time.Timers == 1);
			_time.Advance(TimeSpan.FromSeconds(1));
			await WaitUntilAsync(() => _processes.Count == 2);

			Assert.True(failed.Killed);
			Assert.True(failed.Disposed);
			Assert.Contains(_logger.Entries, e => e is (LogLevel.Error, "Waiting for cloudflared failed; it is stopped and restarted."));
			await cts.CancelAsync();
			await run;
		}
	}

	[Fact]
	public async Task HostedService_StartAndStop_KillsTheProcess()
	{
		using (var supervisor = Create())
		{
			await supervisor.StartAsync(TestContext.Current.CancellationToken);
			await WaitUntilAsync(() => _processes.Count == 1);
			await supervisor.StopAsync(TestContext.Current.CancellationToken);

			Assert.True(_processes.Last().Killed);
		}
	}

	[Fact]
	public void ResolveExecutable_ConfiguredThenBundledThenPath()
	{
		var dir = Path.Combine(Path.GetTempPath(), "aicp-tests", Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(dir);
		try
		{
			Assert.Equal(ConfiguredPath, CloudflaredSupervisor.ResolveExecutable(ConfiguredPath, dir));
			Assert.Equal("cloudflared", CloudflaredSupervisor.ResolveExecutable(" ", dir));

			var bundled = Path.Combine(dir, CloudflaredSupervisor.BundledFileName);
			File.WriteAllText(bundled, string.Empty);

			Assert.Equal(bundled, CloudflaredSupervisor.ResolveExecutable(null, dir));
		}
		finally
		{
			Directory.Delete(dir, recursive: true);
		}
	}

	private static async Task WaitUntilAsync(Func<bool> condition)
	{
		var deadline = DateTime.UtcNow.AddSeconds(10);
		while (!condition())
		{
			Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the supervisor");
			await Task.Delay(5, TestContext.Current.CancellationToken);
		}
	}

	private CloudflaredSupervisor Create(string token = Token, bool accessEnabled = true) =>
		new(new TunnelOptions { Token = token, CloudflaredPath = ConfiguredPath }, new CloudflareAccessOptions { Enabled = accessEnabled }, _logger, _time, Start);

	private ICloudflaredProcess Start(ProcessStartInfo info, Action<string> output)
	{
		_starts.Enqueue(info);
		_output = output;
		if (FailStart is { } failure)
		{
			throw failure;
		}

		var process = new FakeProcess();
		_processes.Enqueue(process);
		return process;
	}

	/// <summary>Exits the running process, waits until the supervisor armed its backoff timer, then checks the restart comes exactly after <paramref name="backoff"/>.</summary>
	private async Task ExitAndExpectRestartAfterAsync(TimeSpan backoff)
	{
		var timers = _time.Timers;
		var started = _processes.Count;
		_processes.Last().Exit(1);
		await WaitUntilAsync(() => _time.Timers == timers + 1);

		_time.Advance(backoff - TimeSpan.FromMilliseconds(1));
		Assert.Equal(started, _processes.Count);
		_time.Advance(TimeSpan.FromMilliseconds(1));
		await WaitUntilAsync(() => _processes.Count == started + 1);
	}

	/// <summary>Counts timers (each backoff wait creates one), so a test advances the clock only once the wait is armed.</summary>
	private sealed class CountingTimeProvider : FakeTimeProvider
	{
		private int _timers;

		public int Timers => Volatile.Read(ref _timers);

		public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
		{
			var timer = base.CreateTimer(callback, state, dueTime, period);
			Interlocked.Increment(ref _timers);
			return timer;
		}
	}

	private sealed class FakeProcess : ICloudflaredProcess
	{
		private readonly TaskCompletionSource<int> _exit = new();

		public bool Killed { get; private set; }

		public bool Disposed { get; private set; }

		public void Exit(int exitCode) => _exit.SetResult(exitCode);

		public void Fail(Exception error) => _exit.SetException(error);

		public Task<int> WaitForExitAsync(CancellationToken ct) => _exit.Task.WaitAsync(ct);

		public void Kill()
		{
			Killed = true;
			_exit.TrySetResult(-1);
		}

		public void Dispose() => Disposed = true;
	}
}
