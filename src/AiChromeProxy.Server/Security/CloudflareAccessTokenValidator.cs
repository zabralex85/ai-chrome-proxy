using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Server.Security;

/// <summary>Validates Cloudflare Access JWTs against the team JWKS.</summary>
public sealed class CloudflareAccessTokenValidator(
	IHttpClientFactory httpFactory,
	IOptions<CloudflareAccessOptions> options,
	TimeProvider time)
{
	public const string JwksHttpClient = "cf-access-jwks";

	private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);
	private static readonly TimeSpan JwksTimeout = TimeSpan.FromSeconds(10);

	private readonly JsonWebTokenHandler _handler = new();
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private IList<SecurityKey> _keys = [];
	private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

	public async Task<bool> ValidateAsync(string token, CancellationToken ct)
	{
		if (_keys.Count == 0)
		{
			await RefreshKeysAsync(ct);
		}

		var result = await _handler.ValidateTokenAsync(token, Parameters());
		if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && await RefreshKeysAsync(ct))
		{
			result = await _handler.ValidateTokenAsync(token, Parameters());
		}

		return result.IsValid;
	}

	private TokenValidationParameters Parameters()
	{
		var o = options.Value;
		return new TokenValidationParameters
		{
			ValidIssuer = $"https://{o.TeamDomain}",
			ValidAudience = o.Audience,
			IssuerSigningKeys = _keys,
			ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
			ClockSkew = TimeSpan.FromMinutes(1),
			LifetimeValidator = (notBefore, expires, _, p) =>
			{
				var now = time.GetUtcNow().UtcDateTime;
				return (notBefore is null || notBefore.Value - p.ClockSkew <= now)
					&& expires is not null && now <= expires.Value + p.ClockSkew;
			},
		};
	}

	/// <returns>True when keys were (re)loaded.</returns>
	private async Task<bool> RefreshKeysAsync(CancellationToken ct)
	{
		await _refreshLock.WaitAsync(ct);
		try
		{
			var now = time.GetUtcNow();
			if (now - _lastRefresh < MinRefreshInterval)
			{
				return false;
			}

			_lastRefresh = now;
			var url = $"https://{options.Value.TeamDomain}/cdn-cgi/access/certs";
			var client = httpFactory.CreateClient(JwksHttpClient);
			client.Timeout = JwksTimeout;
			// Not the caller's token: _lastRefresh is already set, so a cancelled fetch would leave keys empty for a minute.
			var json = await client.GetStringAsync(url, CancellationToken.None);
			_keys = new JsonWebKeySet(json).GetSigningKeys();
			return true;
		}
		finally
		{
			_refreshLock.Release();
		}
	}
}
