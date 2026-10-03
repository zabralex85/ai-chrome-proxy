using System.Text.Json;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>
/// Creates or reuses everything remote access needs: Cloudflare Tunnel (remotely managed) with its ingress, the proxied DNS CNAME,
/// the Access policy and the Access application. Every step finds before it creates, so a re-run converges instead of duplicating.
/// </summary>
public sealed class RemoteAccessProvisioner(CloudflareApi api)
{
	public const string ApplicationName = "AI Chrome Proxy";
	public const string NotEnabledMessage = "Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry.";

	public static string TunnelName(string machineName) => "ai-chrome-proxy-" + machineName.ToLowerInvariant();

	public static string PolicyName(string publicHost) => $"AI Chrome Proxy — {publicHost}";

	/// <summary>Runs the six steps in order, reporting one line per finished step.</summary>
	/// <exception cref="CloudflareApiException">A step failed (Access not enabled, a foreign DNS record, or an API error); later steps did not run.</exception>
	public async Task<RemoteAccessResult> ProvisionAsync(RemoteAccessRequest request, IProgress<string> progress, CancellationToken ct)
	{
		var account = request.Zone.Account.Id;
		var host = request.PublicHost;

		var teamDomain = await GetTeamDomainAsync(account, ct);
		progress.Report($"Zero Trust team domain: {teamDomain}");

		var (tunnel, tunnelCreated) = await FindOrCreateTunnelAsync(account, TunnelName(request.MachineName), ct);
		var tunnelToken = await api.SendAsync<string>(HttpMethod.Get, $"accounts/{account}/cfd_tunnel/{tunnel.Id}/token", null, ct);
		progress.Report($"Tunnel {tunnel.Name}: {(tunnelCreated ? "created" : "reused")}");

		var service = $"http://127.0.0.1:{request.Port}";
		var ingress = new { Config = new { Ingress = new object[] { new { Hostname = host, Service = service }, new { Service = "http_status:404" } } } };
		await api.SendAsync<JsonElement>(HttpMethod.Put, $"accounts/{account}/cfd_tunnel/{tunnel.Id}/configurations", ingress, ct);
		progress.Report($"Tunnel route: {host} → {service}");

		progress.Report($"DNS record {host}: {await EnsureCnameAsync(request.Zone.Id, host, tunnel.Id, ct)}");

		var (policy, policyCreated) = await UpsertPolicyAsync(account, PolicyName(host), request.Emails, ct);
		progress.Report($"Access policy {policy.Name}: {(policyCreated ? "created" : "updated")} ({string.Join(", ", request.Emails)})");

		var (app, appCreated) = await UpsertApplicationAsync(account, host, policy.Id, ct);
		progress.Report($"Access application {host}: {(appCreated ? "created" : "updated")}");

		return new RemoteAccessResult(teamDomain, app.Aud, host, tunnelToken);
	}

	private async Task<string> GetTeamDomainAsync(string account, CancellationToken ct)
	{
		Organization organization;
		try
		{
			organization = await api.SendAsync<Organization>(HttpMethod.Get, $"accounts/{account}/access/organizations", null, ct);
		}
		catch (CloudflareApiException ex) when ((int?)ex.StatusCode is >= 400 and < 500 and not 429)
		{
			throw new CloudflareApiException($"Cloudflare Access is not enabled for this account. {NotEnabledMessage} ({ex.Message})", ex.Code, ex.StatusCode);
		}

		return string.IsNullOrWhiteSpace(organization.AuthDomain)
			? throw new CloudflareApiException($"Cloudflare Access is not enabled for this account. {NotEnabledMessage}")
			: organization.AuthDomain;
	}

	private async Task<(Tunnel Tunnel, bool Created)> FindOrCreateTunnelAsync(string account, string name, CancellationToken ct)
	{
		var tunnels = await api.ListAsync<Tunnel>($"accounts/{account}/cfd_tunnel?name={Uri.EscapeDataString(name)}&is_deleted=false", ct);
		if (tunnels.FirstOrDefault(t => t.Name == name) is { } existing)
		{
			return (existing, false);
		}

		var created = await api.SendAsync<Tunnel>(HttpMethod.Post, $"accounts/{account}/cfd_tunnel", new { Name = name, ConfigSrc = "cloudflare" }, ct);
		return (created, true);
	}

	/// <returns>What happened, for the progress line.</returns>
	private async Task<string> EnsureCnameAsync(string zone, string host, string tunnelId, CancellationToken ct)
	{
		var target = $"{tunnelId}.cfargotunnel.com";
		var records = await api.ListAsync<DnsRecord>($"zones/{zone}/dns_records?name={Uri.EscapeDataString(host)}", ct);
		bool IsOurs(DnsRecord r) => r.Type == "CNAME" && string.Equals(r.Content, target, StringComparison.OrdinalIgnoreCase);

		if (records.Any(r => !IsOurs(r)))
		{
			throw new CloudflareApiException($"{host} already has a DNS record; choose another subdomain or delete it.");
		}

		if (records.FirstOrDefault() is not { } ours)
		{
			var record = new { Type = "CNAME", Name = host, Content = target, Proxied = true, Ttl = 1 };
			await api.SendAsync<JsonElement>(HttpMethod.Post, $"zones/{zone}/dns_records", record, ct);
			return "CNAME created";
		}

		if (ours.Proxied)
		{
			return "CNAME kept";
		}

		await api.SendAsync<JsonElement>(HttpMethod.Patch, $"zones/{zone}/dns_records/{ours.Id}", new { Proxied = true }, ct);
		return "CNAME switched to proxied";
	}

	private async Task<(Policy Policy, bool Created)> UpsertPolicyAsync(string account, string name, IReadOnlyList<string> emails, CancellationToken ct)
	{
		var body = new { Name = name, Decision = "allow", Include = emails.Select(e => new { Email = new { Email = e } }).ToList() };
		var policies = await api.ListAsync<Policy>($"accounts/{account}/access/policies", ct);
		if (policies.FirstOrDefault(p => p.Name == name) is { } existing)
		{
			return (await api.SendAsync<Policy>(HttpMethod.Put, $"accounts/{account}/access/policies/{existing.Id}", body, ct), false);
		}

		return (await api.SendAsync<Policy>(HttpMethod.Post, $"accounts/{account}/access/policies", body, ct), true);
	}

	private async Task<(Application Application, bool Created)> UpsertApplicationAsync(string account, string host, string policyId, CancellationToken ct)
	{
		var body = new
		{
			Name = ApplicationName,
			Type = "self_hosted",
			Domain = host,
			SessionDuration = "24h",
			Policies = new[] { new { Id = policyId, Precedence = 1 } },
		};
		var apps = await api.ListAsync<Application>($"accounts/{account}/access/apps", ct);
		if (apps.FirstOrDefault(a => string.Equals(a.Domain, host, StringComparison.OrdinalIgnoreCase)) is { } existing)
		{
			return (await api.SendAsync<Application>(HttpMethod.Put, $"accounts/{account}/access/apps/{existing.Id}", body, ct), false);
		}

		return (await api.SendAsync<Application>(HttpMethod.Post, $"accounts/{account}/access/apps", body, ct), true);
	}

	private sealed record Organization(string? AuthDomain);

	private sealed record Tunnel(string Id, string Name);

	private sealed record DnsRecord(string Id, string Type, string Content, bool Proxied);

	private sealed record Policy(string Id, string Name);

	private sealed record Application(string Id, string? Domain, string Aud);
}
