namespace AiChromeProxy.Server.Security;

public sealed class CloudflareAccessOptions
{
	public const string Section = "CloudflareAccess";

	public bool Enabled { get; set; } = true;

	public string TeamDomain { get; set; } = string.Empty;

	public string Audience { get; set; } = string.Empty;

	/// <summary>Fail closed: outside Development the check must be on and fully configured.</summary>
	public void Validate(IHostEnvironment env)
	{
		if (!Enabled)
		{
			if (!env.IsDevelopment())
			{
				throw new InvalidOperationException("CloudflareAccess:Enabled=false is allowed only in Development.");
			}

			return;
		}

		if (string.IsNullOrWhiteSpace(TeamDomain) || string.IsNullOrWhiteSpace(Audience))
		{
			throw new InvalidOperationException("CloudflareAccess:TeamDomain and CloudflareAccess:Audience must be set.");
		}
	}
}
