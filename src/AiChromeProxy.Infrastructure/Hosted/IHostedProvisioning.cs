using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Infrastructure.Hosted;

/// <summary>The hosted provisioning service as the tray wizard uses it (<see cref="HostedProvisioningClient"/>; tests use a fake).</summary>
public interface IHostedProvisioning
{
	/// <summary>The domain subdomains are created under for this invite.</summary>
	Task<string> GetZoneAsync(InviteLink link, CancellationToken cancellationToken);

	/// <summary>Creates the tunnel, DNS record and Access application and returns the Server settings.</summary>
	Task<RemoteAccessResult> RedeemAsync(InviteLink link, string subdomain, string email, string machineName, int port, CancellationToken cancellationToken);
}
