using AiChromeProxy.Server.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiChromeProxy.Tests.Security;

public sealed class CloudflareAccessMiddlewareTests
{
	private readonly TestAccessIssuer _issuer = new();

	[Fact]
	public async Task ValidHeaderToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token());

		Assert.Equal(StatusCodes.Status200OK, status);
	}

	[Fact]
	public async Task ValidCookieToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers.Cookie = $"{CloudflareAccessMiddleware.CookieName}={_issuer.Token()}");

		Assert.Equal(StatusCodes.Status200OK, status);
	}

	[Fact]
	public async Task MissingToken_401()
	{
		Assert.Equal(StatusCodes.Status401Unauthorized, await RunAsync(_ => { }));
	}

	[Fact]
	public async Task WrongAudience_401()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token(audience: "other"));

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task WrongIssuer_401()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = _issuer.Token(issuer: "https://evil.cloudflareaccess.com"));

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task Expired_401()
	{
		var token = _issuer.Token(expires: DateTime.UtcNow.AddMinutes(-5));

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = token);

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task ForeignKey_401()
	{
		var foreign = new TestAccessIssuer("kid-1");

		var status = await RunAsync(ctx => ctx.Request.Headers[CloudflareAccessMiddleware.HeaderName] = foreign.Token());

		Assert.Equal(StatusCodes.Status401Unauthorized, status);
	}

	[Fact]
	public async Task UnknownKid_RefetchesJwksAtMostOncePerMinute()
	{
		var stranger = new TestAccessIssuer("kid-unknown");
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var jwks = _issuer.Handler();
		var (validator, _) = Build(jwks, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.True(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.Equal(1, jwks.Requests);

		time.Now += TimeSpan.FromMinutes(2);
		Assert.False(await validator.ValidateAsync(stranger.Token(), ct));
		Assert.Equal(2, jwks.Requests);
	}

	[Fact]
	public async Task Disabled_PassesWithoutToken()
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: false);
		var ctx = new DefaultHttpContext();
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
	}

	private static (CloudflareAccessTokenValidator Validator, IOptions<CloudflareAccessOptions> Options) Build(
		HttpMessageHandler jwks, TimeProvider time, bool enabled)
	{
		var services = new ServiceCollection();
		services.AddHttpClient(CloudflareAccessTokenValidator.JwksHttpClient).ConfigurePrimaryHttpMessageHandler(() => jwks);
		var factory = services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
		var options = Options.Create(new CloudflareAccessOptions
		{
			Enabled = enabled,
			TeamDomain = TestAccessIssuer.TeamDomain,
			Audience = TestAccessIssuer.Audience,
		});
		return (new CloudflareAccessTokenValidator(factory, options, time), options);
	}

	private static Task Ok(HttpContext context)
	{
		context.Response.StatusCode = StatusCodes.Status200OK;
		return Task.CompletedTask;
	}

	private async Task<int> RunAsync(Action<HttpContext> arrange)
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: true);
		var ctx = new DefaultHttpContext();
		arrange(ctx);
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		return ctx.Response.StatusCode;
	}
}
