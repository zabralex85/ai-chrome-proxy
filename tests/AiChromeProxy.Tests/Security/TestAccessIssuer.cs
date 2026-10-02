using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Tests.Security;

/// <summary>Plays the Cloudflare Access side in tests: RSA key, JWKS document, signed tokens.</summary>
public sealed class TestAccessIssuer
{
	public const string TeamDomain = "test-team.cloudflareaccess.com";
	public const string Audience = "test-aud";

	private readonly RsaSecurityKey _key;

	public TestAccessIssuer(string keyId = "kid-1")
	{
		_key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = keyId };
	}

	public string Jwks()
	{
		var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
		return JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
	}

	public string Token(
		string issuer = "https://" + TeamDomain,
		string audience = Audience,
		DateTime? expires = null,
		DateTime? notBefore = null)
	{
		var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
		return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
		{
			Issuer = issuer,
			Audience = audience,
			NotBefore = notBefore ?? exp.AddMinutes(-20),
			IssuedAt = notBefore ?? exp.AddMinutes(-20),
			Expires = exp,
			Claims = new Dictionary<string, object> { ["email"] = "user@example.com" },
			SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
		});
	}

	/// <summary>HttpMessageHandler serving this issuer's JWKS; counts requests.</summary>
	public JwksHandler Handler() => new(this);

	public sealed class JwksHandler(TestAccessIssuer issuer) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			Assert.Equal($"https://{TeamDomain}/cdn-cgi/access/certs", request.RequestUri!.ToString());
			return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(issuer.Jwks()) });
		}
	}
}
