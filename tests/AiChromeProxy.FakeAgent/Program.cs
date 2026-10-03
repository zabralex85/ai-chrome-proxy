// A stand-in for the agent CLI in tests: ignores its arguments, reads stdin to the end, then prints the lines of a fixture file.
//   FAKE_AGENT_SCRIPT       path of the file whose lines are printed to stdout
//   FAKE_AGENT_DELAY_MS     pause before each line (default 0)
//   FAKE_AGENT_STDIN_FILE   when set, the stdin text is written to this file
//   FAKE_AGENT_STDERR       when set, this text is written to stderr first
//   FAKE_AGENT_EXIT_CODE    exit code (default 0)
string stdin;
using (var reader = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false)))
{
	stdin = await reader.ReadToEndAsync();
}

var stdinFile = Environment.GetEnvironmentVariable("FAKE_AGENT_STDIN_FILE");
if (!string.IsNullOrEmpty(stdinFile))
{
	await File.WriteAllTextAsync(stdinFile, stdin);
}

var stderr = Environment.GetEnvironmentVariable("FAKE_AGENT_STDERR");
if (!string.IsNullOrEmpty(stderr))
{
	await Console.Error.WriteLineAsync(stderr);
}

var script = Environment.GetEnvironmentVariable("FAKE_AGENT_SCRIPT");
var delay = int.TryParse(Environment.GetEnvironmentVariable("FAKE_AGENT_DELAY_MS"), out var ms) ? ms : 0;
if (!string.IsNullOrEmpty(script))
{
	foreach (var line in await File.ReadAllLinesAsync(script))
	{
		if (delay > 0)
		{
			await Task.Delay(delay);
		}

		Console.Out.WriteLine(line);
		Console.Out.Flush();
	}
}

return int.TryParse(Environment.GetEnvironmentVariable("FAKE_AGENT_EXIT_CODE"), out var code) ? code : 0;
