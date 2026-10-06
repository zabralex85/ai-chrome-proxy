using System.Text.RegularExpressions;
using AiChromeProxy.Domain.Chat;

namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Reads <c>claude mcp list</c>: one <c>&lt;name&gt;: &lt;command or url&gt; - &lt;mark&gt; &lt;text&gt;</c> line per server; other lines (progress,
/// warnings, blanks) are skipped. The name ends at the first <c>": "</c> (names may hold <c>:</c> and spaces, as in <c>plugin:design:chat</c>);
/// the mark is the last <c>" - &lt;symbol&gt; "</c> (a command may hold <c>" - "</c>; an unknown mark, even outside the BMP, is <c>unknown</c>).
/// At most <see cref="ClaudeToolEntries.MaxReported"/> servers; a name longer than <see cref="ClaudeToolEntries.MaxLength"/> is skipped and a reason cut
/// to <see cref="ClaudeToolEntries.MaxReasonLength"/>.
/// </summary>
public static partial class McpListParser
{
	/// <summary>The servers in the order listed.</summary>
	public static IReadOnlyList<ClaudeMcpServer> Parse(string output)
	{
		var servers = new List<ClaudeMcpServer>();
		foreach (var line in output.Split('\n'))
		{
			var m = Line().Match(line.TrimEnd('\r'));
			if (!m.Success || m.Groups["name"].Value.StartsWith('[') || m.Groups["name"].Length > ClaudeToolEntries.MaxLength)
			{
				continue;
			}

			var name = m.Groups["name"].Value;
			var text = m.Groups["text"].Value;
			var status = m.Groups["mark"].Value switch
			{
				"✔" => ClaudeToolStatuses.Connected,
				"✘" or "✗" => ClaudeToolStatuses.Failed,
				"!" => ClaudeToolStatuses.NeedsAuth,
				"-" => ClaudeToolStatuses.NotConfigured,
				"⏸" => ClaudeToolStatuses.Pending,
				_ => ClaudeToolStatuses.Unknown,
			};
			var dash = text.IndexOf('—', StringComparison.Ordinal);
			var reason = status == ClaudeToolStatuses.Failed && dash >= 0 ? text[(dash + 1)..].Trim() : null;
			var source = name.StartsWith("plugin:", StringComparison.Ordinal) ? "plugin" : name.StartsWith("claude.ai ", StringComparison.Ordinal) ? "claudeai" : null;
			servers.Add(new ClaudeMcpServer(name, source, status, reason is { Length: > 0 } ? reason[..Math.Min(reason.Length, ClaudeToolEntries.MaxReasonLength)] : null));
			if (servers.Count == ClaudeToolEntries.MaxReported)
			{
				break;
			}
		}

		return servers;
	}

	[GeneratedRegex(@"^(?<name>.+?): (?<target>.*) - (?<mark>\p{Cs}{2}|[^\w\s])️? (?<text>.+)$")]
	private static partial Regex Line();
}
