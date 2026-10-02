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

	private readonly JsonWebTokenHandler _handler = new();
	private readonly SemaphoreSlim _refreshLock = new(1, 1);
	private IList<SecurityKey> _keys = [];
	private DateTimeOffset _lastRefresh = DateTimeOffset.MinValue;

	public async Task<bool> ValidateAsync(string token, CancellationToken ct)
	{
		if (_keys.Count == 0)
		{
			await RefreshKeysAsync(force: true, ct);
		}

		var result = await _handler.ValidateTokenAsync(token, Parameters());
		if (!result.IsValid && result.Exception is SecurityTokenSignatureKeyNotFoundException && await RefreshKeysAsync(force: false, ct))
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
	private async Task<bool> RefreshKeysAsync(bool force, CancellationToken ct)
	{
		await _refreshLock.WaitAsync(ct);
		try
		{
			var now = time.GetUtcNow();
			if (!force && now - _lastRefresh < MinRefreshInterval)
			{
				return false;
			}

			var url = $"https://{options.Value.TeamDomain}/cdn-cgi/access/certs";
			var json = await httpFactory.CreateClient(JwksHttpClient).GetStringAsync(url, ct);
			_keys = new JsonWebKeySet(json).GetSigningKeys();
			_lastRefresh = now;
			return true;
		}
		finally
		{
			_refreshLock.Release();
		}
	}
}
