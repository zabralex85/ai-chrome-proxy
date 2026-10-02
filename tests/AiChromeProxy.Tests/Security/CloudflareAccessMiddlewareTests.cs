using System.Net;
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

		Assert.Equal(StatusCodes.Status204NoContent, status);
	}

	[Fact]
	public async Task ValidCookieToken_Passes()
	{
		var status = await RunAsync(ctx => ctx.Request.Headers.Cookie = $"{CloudflareAccessMiddleware.CookieName}={_issuer.Token()}");

		Assert.Equal(StatusCodes.Status204NoContent, status);
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
	public async Task JwksEndpointFailing_FetchedAtMostOncePerMinute()
	{
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var stub = new StubHandler(HttpStatusCode.InternalServerError, "boom");
		var (validator, _) = Build(stub, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		await Assert.ThrowsAnyAsync<HttpRequestException>(() => validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(1, stub.Requests);

		time.Now += TimeSpan.FromMinutes(2);
		await Assert.ThrowsAnyAsync<HttpRequestException>(() => validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(2, stub.Requests);
	}

	[Fact]
	public async Task EmptyJwks_FetchedAtMostOncePerMinute()
	{
		var time = new FixedTimeProvider(DateTimeOffset.UtcNow);
		var stub = new StubHandler(HttpStatusCode.OK, "{\"keys\":[]}");
		var (validator, _) = Build(stub, time, enabled: true);
		var ct = TestContext.Current.CancellationToken;

		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.False(await validator.ValidateAsync(_issuer.Token(), ct));
		Assert.Equal(1, stub.Requests);
	}

	[Fact]
	public async Task CallerCancelledDuringColdFetch_DoesNotPoisonKeyCache()
	{
		var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
		var handler = new GatedHandler(_issuer.Jwks(), gate.Task, entered);
		var (validator, _) = Build(handler, TimeProvider.System, enabled: true);
		using var cts = new CancellationTokenSource();

		var first = validator.ValidateAsync(_issuer.Token(), cts.Token);
		await entered.Task;
		await cts.CancelAsync();
		gate.SetResult();
		try
		{
			await first;
		}
		catch (OperationCanceledException)
		{
		}

		Assert.True(await validator.ValidateAsync(_issuer.Token(), TestContext.Current.CancellationToken));
		Assert.Equal(1, handler.Requests);
	}

	[Fact]
	public async Task Disabled_PassesWithoutToken()
	{
		var (validator, options) = Build(_issuer.Handler(), TimeProvider.System, enabled: false);
		var ctx = new DefaultHttpContext();
		var middleware = new CloudflareAccessMiddleware(Ok, options, validator);

		await middleware.InvokeAsync(ctx);

		Assert.Equal(StatusCodes.Status204NoContent, ctx.Response.StatusCode);
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
		context.Response.StatusCode = StatusCodes.Status204NoContent;
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

	private sealed class GatedHandler(string body, Task gate, TaskCompletionSource entered) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			entered.TrySetResult();
			await gate.WaitAsync(cancellationToken);
			return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
		}
	}

	private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
	{
		public int Requests { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requests++;
			return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
		}
	}
}
