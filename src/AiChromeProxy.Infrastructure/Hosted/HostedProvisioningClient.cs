using System.Net.Http.Json;
using System.Text.Json;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Infrastructure.Hosted;

/// <summary>Client of the hosted provisioning protocol (version 1). Never logs bodies: the redeem answer carries the tunnel token.</summary>
public sealed class HostedProvisioningClient(HttpClient http) : IHostedProvisioning
{
	private static readonly TimeSpan _checkTimeout = TimeSpan.FromSeconds(15);
	// Longer than the service may hold the invite (120 s): giving up earlier could leave a redeemed invite whose answer nobody read.
	private static readonly TimeSpan _redeemTimeout = TimeSpan.FromSeconds(130);

	/// <summary>The domain subdomains are created under for this invite.</summary>
	public async Task<string> GetZoneAsync(InviteLink link, CancellationToken cancellationToken)
	{
		var answer = await SendAsync<ZoneAnswer>(link, HttpMethod.Get, $"v1/invites/{link.Code}", null, _checkTimeout, cancellationToken);
		return string.IsNullOrWhiteSpace(answer?.Zone) ? throw new HostedProvisioningException("The service answered without a zone.") : answer.Zone;
	}

	/// <summary>Creates the tunnel, DNS record and Access application and returns the Server settings.</summary>
	public async Task<RemoteAccessResult> RedeemAsync(InviteLink link, string subdomain, string email, string machineName, int port, CancellationToken cancellationToken)
	{
		var body = new { subdomain, email, machineName, port };
		var answer = await SendAsync<RemoteAccessResult>(link, HttpMethod.Post, $"v1/invites/{link.Code}/redeem", body, _redeemTimeout, cancellationToken);
		if (answer is null || string.IsNullOrWhiteSpace(answer.TeamDomain) || string.IsNullOrWhiteSpace(answer.Audience)
			|| string.IsNullOrWhiteSpace(answer.PublicHost) || string.IsNullOrWhiteSpace(answer.TunnelToken))
		{
			throw new HostedProvisioningException("The service answered with an incomplete result.");
		}

		return answer;
	}

	private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
	{
		try
		{
			var error = await response.Content.ReadFromJsonAsync<ErrorAnswer>(JsonSerializerOptions.Web, cancellationToken);
			return string.IsNullOrWhiteSpace(error?.Error) ? null : error.Error;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	private async Task<T?> SendAsync<T>(InviteLink link, HttpMethod method, string path, object? body, TimeSpan timeout, CancellationToken cancellationToken)
	{
		using (var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
		{
			timeoutSource.CancelAfter(timeout);
			try
			{
				using (var request = new HttpRequestMessage(method, new Uri(link.Origin, path)))
				{
					if (body is not null)
					{
						// A string body carries Content-Length; the service refuses chunked bodies.
						request.Content = new StringContent(JsonSerializer.Serialize(body, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json");
					}

					using (var response = await http.SendAsync(request, timeoutSource.Token))
					{
						if (!response.IsSuccessStatusCode)
						{
							throw new HostedProvisioningException(await ReadErrorAsync(response, timeoutSource.Token) ?? $"The service answered {(int)response.StatusCode}.", response.StatusCode);
						}

						return await response.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, timeoutSource.Token);
					}
				}
			}
			catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
			{
				throw new HostedProvisioningException($"Could not reach {link.Origin.Host}: timed out.");
			}
			catch (HttpRequestException ex)
			{
				throw new HostedProvisioningException($"Could not reach {link.Origin.Host}: {ex.Message}");
			}
			catch (JsonException)
			{
				throw new HostedProvisioningException("The service answered with something unexpected.");
			}
		}
	}

	private sealed record ZoneAnswer(string? Zone);

	private sealed record ErrorAnswer(string? Error);
}
