using AiChromeProxy.Client.Shell;
using AiChromeProxy.Domain.Projects;

namespace AiChromeProxy.Client.Projects;

/// <summary>What the projects menu shows and how its keyboard moves.</summary>
public static class ProjectLine
{
	/// <summary>"this folder · Claude is working · online · 5 min ago": the parts that apply, the chat's last activity last.</summary>
	public static string Of(RepoInfo repo, string? localRepo, DateTimeOffset now)
	{
		var local = string.Equals(repo.Name, localRepo, StringComparison.OrdinalIgnoreCase);
		var parts = new List<string>();
		if (local)
		{
			parts.Add("this folder");
		}

		if (repo.Running)
		{
			parts.Add("Claude is working");
		}

		if (repo.Open && !local)
		{
			parts.Add("online");
		}

		parts.Add(repo.LastActivity is { } at ? Format.Ago(at, now) : "no chat yet");
		return string.Join(" · ", parts);
	}

	/// <summary>The item focused after <paramref name="key"/> (arrows wrap around; -1: none focused yet).</summary>
	public static int Move(string key, int index, int count)
	{
		if (count == 0)
		{
			return -1;
		}

		return key switch
		{
			"ArrowDown" => (index + 1) % count,
			"ArrowUp" => index <= 0 ? count - 1 : index - 1,
			"Home" => 0,
			"End" => count - 1,
			_ => index,
		};
	}
}
