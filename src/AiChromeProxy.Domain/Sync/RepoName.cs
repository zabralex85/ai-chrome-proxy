namespace AiChromeProxy.Domain.Sync;

/// <summary>The mirror folder name for a picked folder: only <c>[A-Za-z0-9._-]</c>, at most 64 characters.</summary>
public static class RepoName
{
	public const int MaxLength = 64;

	/// <returns>The sanitized name, or null when nothing usable is left (empty input).</returns>
	public static string? Sanitize(string? folderName)
	{
		if (string.IsNullOrWhiteSpace(folderName))
		{
			return null;
		}

		var chars = folderName.Trim().Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_').Take(MaxLength).ToArray();
		var name = new string(chars);

		// Trailing dots vanish on Windows ("." and ".." would escape the root); a device name would open the device.
		var trimmed = name.TrimEnd('.');
		name = trimmed + new string('_', name.Length - trimmed.Length);
		return SyncPath.IsValid(name) ? name : "_" + name.Replace('.', '_')[..Math.Min(name.Length, MaxLength - 1)];
	}

	public static bool IsValid(string? repo) => repo is not null && Sanitize(repo) == repo;
}
