using System.Net;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class CloudflareApiTests : IDisposable
{
	private const string Token = "secret-api-token-123";

	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly List<TimeSpan> _waits = [];
	private readonly CloudflareApi _api;

	public CloudflareApiTests()
	{
		_http = new HttpClient(_handler);
		_api = new CloudflareApi(_http, Token, (wait, _) =>
		{
			_waits.Add(wait);
			return Task.CompletedTask;
		});
	}

	[Fact]
	public async Task VerifyToken_Active_SendsBearerToken()
	{
		_handler.On("GET", "user/tokens/verify", """{"id":"t1","status":"active"}""");

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		var request = Assert.Single(_handler.Requests);
		Assert.Equal($"Bearer {Token}", request.Authorization);
		Assert.Null(request.Body);
	}

	[Fact]
	public async Task VerifyToken_NotActive_Throws()
	{
		_handler.On("GET", "user/tokens/verify", """{"id":"t1","status":"disabled"}""");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("The API token is disabled, not active.", ex.Message);
	}

	[Fact]
	public async Task ListZones_ActiveOnly_WithAccounts()
	{
		_handler.On("GET", "zones?status=active&page=1&per_page=50", """[{"id":"z1","name":"example.com","status":"active","account":{"id":"a1","name":"Jane's account"}}]""", totalPages: 1);

		var zones = await _api.ListZonesAsync(TestContext.Current.CancellationToken);

		Assert.Equal([new CloudflareZone("z1", "example.com", new CloudflareAccount("a1", "Jane's account"))], zones);
	}

	[Fact]
	public async Task List_FollowsTotalPages()
	{
		_handler
			.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"p1"}]""", totalPages: 3)
			.On("GET", "accounts/a1/access/apps?page=2&per_page=50", """[{"id":"p2"}]""", totalPages: 3)
			.On("GET", "accounts/a1/access/apps?page=3&per_page=50", """[{"id":"p3"}]""", totalPages: 3);

		var items = await _api.ListAsync<Item>("accounts/a1/access/apps", TestContext.Current.CancellationToken);

		Assert.Equal(["p1", "p2", "p3"], items.Select(i => i.Id));
		Assert.Equal(3, _handler.Requests.Count);
	}

	[Fact]
	public async Task List_NoResultInfo_SinglePage()
	{
		_handler.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"p1"}]""");

		var items = await _api.ListAsync<Item>("accounts/a1/access/apps", TestContext.Current.CancellationToken);

		Assert.Equal("p1", Assert.Single(items).Id);
	}

	[Fact]
	public async Task Send_BodyAsSnakeCaseJson()
	{
		_handler.On("POST", "accounts/a1/cfd_tunnel", """{"id":"t1","name":"n"}""");

		var item = await _api.SendAsync<Item>(HttpMethod.Post, "accounts/a1/cfd_tunnel", new { Name = "n", ConfigSrc = "cloudflare" }, TestContext.Current.CancellationToken);

		Assert.Equal("t1", item.Id);
		Assert.Equal("""{"name":"n","config_src":"cloudflare"}""", _handler.Body("POST", "accounts/a1/cfd_tunnel")!.ToJsonString());
	}

	[Fact]
	public async Task SuccessFalse_ThrowsFirstError_WithoutToken()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.ErrorEnvelope(1000, "Invalid API Token")));

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API error 1000 on GET user/tokens/verify: Invalid API Token", ex.Message);
		Assert.Equal(1000, ex.Code);
		Assert.Equal(HttpStatusCode.OK, ex.StatusCode);
		Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Non2xx_WithEnvelope_ThrowsFirstError_PathWithoutQuery()
	{
		_handler.OnError("GET", "zones/z1/dns_records?name=code.example.com&page=1&per_page=50", HttpStatusCode.Forbidden, 10000, "Authentication error");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.ListAsync<Item>("zones/z1/dns_records?name=code.example.com", TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API error 10000 on GET zones/z1/dns_records: Authentication error", ex.Message);
		Assert.Equal(HttpStatusCode.Forbidden, ex.StatusCode);
		Assert.DoesNotContain(Token, ex.ToString(), StringComparison.Ordinal);
	}

	[Fact]
	public async Task Non2xx_NotJson_ThrowsStatus()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html>502</html>") });

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API returned HTTP 502 on GET user/tokens/verify.", ex.Message);
		Assert.Equal(0, ex.Code);
	}

	[Fact]
	public async Task Success_NotJson_ThrowsUnreadable()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>ok</html>") });

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.StartsWith("Cloudflare API returned an unreadable response on GET user/tokens/verify", ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task Success_NullResult_Throws()
	{
		_handler.On("GET", "user/tokens/verify", "null");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal("Cloudflare API returned no result on GET user/tokens/verify.", ex.Message);
	}

	[Fact]
	public async Task TooManyRequests_WaitsRetryAfterOnce_ThenSucceeds()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => RateLimited(TimeSpan.FromSeconds(7)), Active);

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		Assert.Equal([TimeSpan.FromSeconds(7)], _waits);
		Assert.Equal(2, _handler.Requests.Count);
	}

	[Fact]
	public async Task TooManyRequests_RetryAfterCappedAt60s_SecondOneFails()
	{
		_handler.OnResponse("GET", "user/tokens/verify", () => RateLimited(TimeSpan.FromMinutes(10)));

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => _api.VerifyTokenAsync(TestContext.Current.CancellationToken));

		Assert.Equal([TimeSpan.FromSeconds(60)], _waits);
		Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
		Assert.Equal(2, _handler.Requests.Count);
	}

	[Fact]
	public async Task TooManyRequests_NoRetryAfter_Waits60s()
	{
		_handler.OnResponse(
			"GET",
			"user/tokens/verify",
			() => FakeCloudflareHandler.Json(HttpStatusCode.TooManyRequests, FakeCloudflareHandler.ErrorEnvelope(971, "Please wait")),
			Active);

		await _api.VerifyTokenAsync(TestContext.Current.CancellationToken);

		Assert.Equal([CloudflareApi.MaxRetryAfter], _waits);
	}

	public void Dispose() => _http.Dispose();

	private static HttpResponseMessage Active() => FakeCloudflareHandler.Json(HttpStatusCode.OK, FakeCloudflareHandler.Envelope("""{"status":"active"}"""));

	private static HttpResponseMessage RateLimited(TimeSpan retryAfter)
	{
		var response = FakeCloudflareHandler.Json(HttpStatusCode.TooManyRequests, FakeCloudflareHandler.ErrorEnvelope(971, "Please wait and consider throttling your request speed"));
		response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(retryAfter);
		return response;
	}

	private sealed record Item(string Id);
}
