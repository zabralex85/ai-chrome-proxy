using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using AiChromeProxy.Infrastructure.Security;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AiChromeProxy.Benchmarks;

/// <summary>Valid RS256 token, JWKS already cached: in-process RSA key and a stub JWKS handler, no network.</summary>
[MemoryDiagnoser]
public class TokenValidationBenchmarks
{
	private const string TeamDomain = "bench-team.cloudflareaccess.com";
	private const string Audience = "bench-aud";

	private CloudflareAccessTokenValidator _validator = null!;
	private string _token = string.Empty;

	[GlobalSetup]
	public void Setup()
	{
		var key = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "bench-kid" };
		var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(key);
		var jwks = JsonSerializer.Serialize(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });

		var services = new ServiceCollection();
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => new JwksHandler(jwks));
		var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
		var options = Options.Create(new CloudflareAccessOptions { TeamDomain = TeamDomain, Audience = Audience });
		_validator = new CloudflareAccessTokenValidator(factory, options, TimeProvider.System);

		_token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
		{
			Issuer = $"https://{TeamDomain}",
			Audience = Audience,
			Expires = DateTime.UtcNow.AddHours(1),
			SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
		});

		// Warms the key cache, so the benchmark measures validation only.
		if (!_validator.ValidateAsync(_token, CancellationToken.None).GetAwaiter().GetResult())
		{
			throw new InvalidOperationException("Benchmark token must be valid.");
		}
	}

	[Benchmark]
	public Task<bool> ValidateAsync() => _validator.ValidateAsync(_token, CancellationToken.None);

	private sealed class JwksHandler(string jwks) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
			Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(jwks) });
	}
}
