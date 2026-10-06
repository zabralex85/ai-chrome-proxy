using AiChromeProxy.Application.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Infrastructure.Chat;

/// <summary>Starts the configured agent command (Claude Code by default) for one turn.</summary>
public sealed class ClaudeRunner : IAgentRunner
{
	private readonly AgentOptions _options;
	private readonly ILogger<ClaudeRunner> _logger;

	public ClaudeRunner(IOptions<AgentOptions> options, ILogger<ClaudeRunner> logger)
	{
		_options = options.Value;
		_logger = logger;
	}

	public TimeSpan IdleTimeout => _options.IdleTimeout;

	public Task<IAgentProcess> StartAsync(AgentRun run, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var file = Resolve(_options);
		if (ViaCmd(file))
		{
			// Allow-always rules such as Bash(a | b) and odd server or plugin names cannot go through cmd.exe: drop just those (the command is
			// asked about again, the server or plugin stays on) and keep the run going.
			run = run with
			{
				AllowedTools = SafeForCmd(run.AllowedTools, "allowed-tools rule", file),
				DisabledMcpServers = SafeForCmd(run.DisabledMcpServers, "MCP server switch", file),
				DisabledPlugins = SafeForCmd(run.DisabledPlugins, "plugin switch", file),
				ApprovedMcpServers = SafeForCmd(run.ApprovedMcpServers, "MCP server approval", file),
			};
		}

		_logger.LogInformation("Starting the agent {Command} in {Folder}", file, run.RepoFolder);
		return Task.FromResult<IAgentProcess>(Start(file, ClaudeArguments.Build(run, _options), run.RepoFolder, _options.Env, run.Prompt, line => _logger.LogWarning("agent stderr: {Line}", line)));
	}

	/// <summary>The file to start for <see cref="AgentOptions.Command"/> (see <see cref="ClaudeArguments.ResolveCommand"/>).</summary>
	/// <exception cref="FileNotFoundException">Not found; the message says what to configure.</exception>
	internal static string Resolve(AgentOptions options)
	{
		var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
		return ClaudeArguments.ResolveCommand(options.Command, pathDirs, File.Exists)
			?? throw new FileNotFoundException($"`{options.Command}` was not found on PATH for the service account; set Agent:Command.");
	}

	/// <summary>Starts <paramref name="file"/>; a <c>.cmd</c> or <c>.bat</c> (npm shim) runs through <c>cmd.exe</c>.</summary>
	/// <exception cref="InvalidOperationException">The file runs through cmd.exe, which cannot take <paramref name="args"/> safely.</exception>
	/// <exception cref="System.ComponentModel.Win32Exception">The process could not be started.</exception>
	internal static AgentProcess Start(string file, IReadOnlyList<string> args, string folder, IReadOnlyDictionary<string, string> env, string prompt, Action<string> onStderr)
	{
		string? rawArguments = null;

		// A .cmd (npm shim) can only run through cmd.exe, which re-parses the line: refuse any other argument it would interpret.
		if (ViaCmd(file))
		{
			if (ClaudeArguments.UnsafeForCmd(args))
			{
				throw new InvalidOperationException($"`{file}` runs through cmd.exe, which cannot take these arguments safely (one of & | < > ^ % ! or a line break); install claude.exe or set Agent:Command to it.");
			}

			rawArguments = "/d /s /c " + ClaudeArguments.CmdLine(file, args);
			file = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
		}

		return AgentProcess.Start(file, args, folder, env, prompt, onStderr, rawArguments);
	}

	private static bool ViaCmd(string file) =>
		file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

	/// <summary>The entries cmd.exe can take (null stays null); each dropped one is logged.</summary>
	private IReadOnlyList<string>? SafeForCmd(IReadOnlyList<string>? entries, string what, string file)
	{
		if (entries is null)
		{
			return null;
		}

		var safe = new List<string>();
		foreach (var entry in entries)
		{
			// JSON escapes & < > (as & ...) in --settings, but not | ^ % !: checking the raw entry covers both.
			if (ClaudeArguments.UnsafeForCmd([entry]))
			{
				_logger.LogWarning("Dropped the {What} {Entry}: cmd.exe cannot pass it safely to {File}; install claude.exe to use it", what, entry, file);
			}
			else
			{
				safe.Add(entry);
			}
		}

		return safe;
	}
}
