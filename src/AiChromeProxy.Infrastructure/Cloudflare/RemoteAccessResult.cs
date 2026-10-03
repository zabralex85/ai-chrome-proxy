namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>The Server settings provisioning produced: <c>CloudflareAccess:TeamDomain</c>, <c>CloudflareAccess:Audience</c>, <c>Server:PublicHost</c>, <c>Tunnel:Token</c> (secret).</summary>
public sealed record RemoteAccessResult(string TeamDomain, string Audience, string PublicHost, string TunnelToken)
{
	/// <summary>Never prints the tunnel token.</summary>
	public override string ToString() => $"RemoteAccessResult {{ TeamDomain = {TeamDomain}, Audience = {Audience}, PublicHost = {PublicHost} }}";
}
