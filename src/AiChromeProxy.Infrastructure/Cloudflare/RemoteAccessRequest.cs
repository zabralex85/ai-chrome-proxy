namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>What the wizard provisions: <c>https://&lt;Subdomain&gt;.&lt;Zone&gt;</c> for <paramref name="Emails"/>, tunnelled to <c>127.0.0.1:&lt;Port&gt;</c>.</summary>
/// <param name="MachineName">Names the tunnel (<c>ai-chrome-proxy-&lt;machine&gt;</c>), so each home server has its own.</param>
public sealed record RemoteAccessRequest(CloudflareZone Zone, string Subdomain, IReadOnlyList<string> Emails, int Port, string MachineName)
{
	public string PublicHost => $"{Subdomain}.{Zone.Name}";
}
