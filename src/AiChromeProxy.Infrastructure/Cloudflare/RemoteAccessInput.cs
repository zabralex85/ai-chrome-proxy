using AiChromeProxy.Infrastructure.Hosting;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>Checks the wizard's details before anything is created in Cloudflare.</summary>
public static class RemoteAccessInput
{
	/// <summary>One DNS label: <c>a-z</c>, <c>0-9</c> and <c>-</c>, 1–63 characters, no leading or trailing <c>-</c>.</summary>
	public static bool IsValidSubdomain(string? value) =>
		value is { Length: >= 1 and <= 63 }
		&& value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
		&& value[0] != '-' && value[^1] != '-';

	/// <summary><c>local@domain</c>: exactly one <c>@</c>, no spaces, a dot inside the domain.</summary>
	public static bool IsValidEmail(string value)
	{
		var at = value.IndexOf('@', StringComparison.Ordinal);
		if (at <= 0 || at != value.LastIndexOf('@') || value.Any(char.IsWhiteSpace))
		{
			return false;
		}

		var domain = value[(at + 1)..];
		var dot = domain.IndexOf('.', StringComparison.Ordinal);
		return dot > 0 && !domain.EndsWith('.');
	}

	/// <summary>Comma- or newline-separated addresses, trimmed, empty entries dropped, duplicates (any case) removed.</summary>
	public static IReadOnlyList<string> ParseEmails(string? text) =>
		(text ?? string.Empty)
			.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

	/// <returns>One message per problem; empty when the details can be provisioned.</returns>
	public static IReadOnlyList<string> Validate(string subdomain, string? zoneName, IReadOnlyList<string> emails)
	{
		var errors = new List<string>();
		if (zoneName is null)
		{
			errors.Add("Choose a zone.");
		}

		if (!IsValidSubdomain(subdomain))
		{
			errors.Add("The subdomain must be one label of a-z, 0-9 and '-', 1 to 63 characters, not starting or ending with '-'.");
		}
		else if (zoneName is not null && !HostName.IsValid($"{subdomain}.{zoneName}"))
		{
			errors.Add($"{subdomain}.{zoneName} is not a valid host name.");
		}

		if (emails.Count == 0)
		{
			errors.Add("Enter at least one email address.");
		}

		errors.AddRange(emails.Where(e => !IsValidEmail(e)).Select(e => $"'{e}' is not an email address."));
		return errors;
	}
}
