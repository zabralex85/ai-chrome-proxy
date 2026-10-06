using System.ComponentModel;
using System.Globalization;
using System.Text;
using AiChromeProxy.Application.Chat;
using AiChromeProxy.Domain.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>
/// <b>Check now</b>: runs <c>&lt;Agent:Command&gt; mcp list</c> (stdout and stderr parsed together: it writes some lines to stderr) and
/// <c>&lt;Agent:Command&gt; plugin list --json</c> side by side in the repo's mirror, with the environment and process handling of a chat run
/// (<see cref="ClaudeRunner.Start"/>), each killed after <see cref="Timeout"/>.
/// </summary>
public sealed class ClaudeToolsProbe(IOptions<AgentOptions> options, ILogger<ClaudeToolsProbe> logger) : IClaudeToolsProbe
{
	/// <summary>Gets how long each command may run (60 s; shorter in tests).</summary>
	public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(60);

	public async Task<(ClaudeToolsSnapshot? Snapshot, string? Error)> CheckAsync(string folder)
	{
		string file;
		try
		{
			file = ClaudeRunner.Resolve(options.Value);
		}
		catch (FileNotFoundException ex)
		{
			return (null, "Check failed: " + ex.Message);
		}

		var mcp = RunAsync(file, ["mcp", "list"], folder);
		var plugins = RunAsync(file, ["plugin", "list", "--json"], folder);
		await Task.WhenAll(mcp, plugins);
		var (mcpOut, mcpErr, mcpError) = await mcp;
		var (pluginsOut, _, pluginsError) = await plugins;
		if ((mcpError ?? pluginsError) is { } error)
		{
			return (null, error);
		}

		try
		{
			return (new ClaudeToolsSnapshot(McpListParser.Parse(mcpOut + "\n" + mcpErr), PluginListParser.Parse(pluginsOut), default, ClaudeToolsSnapshot.FromCheck), null);
		}
		catch (FormatException ex)
		{
			return (null, "Check failed: " + ex.Message);
		}
	}

	private static string Shorten(string text) => text.Length <= 300 ? text : text[..300] + "…";

	/// <summary>Runs one command to its end: its stdout and stderr, or the error text (start failure, timeout, non-zero exit).</summary>
	private async Task<(string Stdout, string Stderr, string? Error)> RunAsync(string file, string[] args, string folder)
	{
		var command = $"`{Path.GetFileNameWithoutExtension(options.Value.Command)} {string.Join(' ', args)}`";
		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		try
		{
			// Exited completes only once stderr was read to its end, so the builder is complete (and no longer written) when read below.
			using (var process = ClaudeRunner.Start(file, args, folder, options.Value.Env, string.Empty, line => stderr.AppendLine(line)))
			{
				using (var timeout = new CancellationTokenSource(Timeout))
				{
					int code;
					try
					{
						await foreach (var line in process.Lines.WithCancellation(timeout.Token))
						{
							stdout.AppendLine(line);
						}

						code = await process.Exited.WaitAsync(timeout.Token);
					}
					catch (OperationCanceledException)
					{
						process.Kill();
						logger.LogWarning("Claude tools check: {Command} timed out in {Folder}", command, folder);
						return (string.Empty, string.Empty, string.Create(CultureInfo.InvariantCulture, $"Check timed out after {Timeout.TotalSeconds:0.##} s."));
					}

					if (code != 0)
					{
						var reason = process.Stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
						return (string.Empty, string.Empty, $"Check failed: {command} exited with code {code}{(reason is null ? "." : ": " + Shorten(reason))}");
					}

					return (stdout.ToString(), stderr.ToString(), null);
				}
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
		{
			return (string.Empty, string.Empty, $"Check failed: {command} could not start: {ex.Message}");
		}
	}
}
