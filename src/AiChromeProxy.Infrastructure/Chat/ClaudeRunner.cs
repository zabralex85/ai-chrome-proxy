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

	public Task<IAgentProcess> StartAsync(AgentRun run, CancellationToken ct)
	{
		ct.ThrowIfCancellationRequested();
		var pathDirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator);
		var file = ClaudeArguments.ResolveCommand(_options.Command, pathDirs, File.Exists)
			?? throw new FileNotFoundException($"`{_options.Command}` was not found on PATH for the service account; set Agent:Command.");
		var args = ClaudeArguments.Build(run, _options);

		// A .cmd (npm shim) can only run through cmd.exe, which re-parses the line: refuse arguments it would interpret.
		string? rawArguments = null;
		if (file.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
		{
			if (ClaudeArguments.UnsafeForCmd(args))
			{
				throw new InvalidOperationException($"`{file}` runs through cmd.exe, which cannot take these arguments safely (one of & | < > ^ % ! or a line break); install claude.exe or set Agent:Command to it.");
			}

			rawArguments = "/d /s /c " + ClaudeArguments.CmdLine(file, args);
			file = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
		}

		_logger.LogInformation("Starting the agent {Command} in {Folder}", file, run.RepoFolder);
		return Task.FromResult<IAgentProcess>(AgentProcess.Start(file, args, run.RepoFolder, _options.Env, run.Prompt, line => _logger.LogWarning("agent stderr: {Line}", line), rawArguments));
	}
}
