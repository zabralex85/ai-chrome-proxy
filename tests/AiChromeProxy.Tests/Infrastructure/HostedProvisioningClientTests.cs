using System.Net;
using System.Text;
using System.Text.Json;
using AiChromeProxy.Infrastructure.Cloudflare;
using AiChromeProxy.Infrastructure.Hosted;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class HostedProvisioningClientTests : IDisposable
{
	private readonly StubHandler _handler = new();
	private readonly HttpClient _http;
	private readonly HostedProvisioningClient _client;
	private readonly InviteLink _link;

	public HostedProvisioningClientTests()
	{
		_http = new HttpClient(_handler, disposeHandler: false);
		_client = new HostedProvisioningClient(_http);
		Assert.True(InviteLink.TryParse("https://svc.example.com/invite/abcdefghij_KLMN-0123", out var link));
		_link = link!;
	}

	public void Dispose() => _http.Dispose();

	[Fact]
	public async Task GetZone_Ok()
	{
		_handler.Reply = _ => Json(HttpStatusCode.OK, """{"zone":"example.com"}""");

		var zone = await _client.GetZoneAsync(_link, TestContext.Current.CancellationToken);

		Assert.Equal("example.com", zone);
		Assert.Equal("GET https://svc.example.com/v1/invites/abcdefghij_KLMN-0123", _handler.Seen);
	}

	[Fact]
	public async Task GetZone_EmptyZone_Throws()
	{
		_handler.Reply = _ => Json(HttpStatusCode.OK, """{"zone":""}""");

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(() => _client.GetZoneAsync(_link, TestContext.Current.CancellationToken));

		Assert.Equal("The service answered without a zone.", ex.Message);
	}

	[Fact]
	public async Task GetZone_NotFoundWithError_UsesErrorText()
	{
		_handler.Reply = _ => Json(HttpStatusCode.NotFound, """{"error":"This invite was already used."}""");

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(() => _client.GetZoneAsync(_link, TestContext.Current.CancellationToken));

		Assert.Equal("This invite was already used.", ex.Message);
		Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
	}

	[Theory]
	[InlineData("")]
	[InlineData("<html>oops</html>")]
	[InlineData("""{"error":""}""")]
	public async Task GetZone_ServerErrorWithoutText_UsesStatus(string body)
	{
		_handler.Reply = _ => Json(HttpStatusCode.InternalServerError, body);

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(() => _client.GetZoneAsync(_link, TestContext.Current.CancellationToken));

		Assert.Equal("The service answered 500.", ex.Message);
	}

	[Fact]
	public async Task GetZone_BadJson_Throws()
	{
		_handler.Reply = _ => Json(HttpStatusCode.OK, "nope");

		await Assert.ThrowsAsync<HostedProvisioningException>(() => _client.GetZoneAsync(_link, TestContext.Current.CancellationToken));
	}

	[Fact]
	public async Task Redeem_Ok_SendsCamelCaseBody()
	{
		_handler.Reply = _ => Json(HttpStatusCode.OK, """{"teamDomain":"team.example.org","audience":"aud","publicHost":"alice.example.com","tunnelToken":"tok"}""");

		var result = await Redeem();

		Assert.Equal(new RemoteAccessResult("team.example.org", "aud", "alice.example.com", "tok"), result);
		Assert.Equal("POST https://svc.example.com/v1/invites/abcdefghij_KLMN-0123/redeem", _handler.Seen);
		using (var doc = JsonDocument.Parse(_handler.Body!))
		{
			var root = doc.RootElement;
			Assert.Equal("alice", root.GetProperty("subdomain").GetString());
			Assert.Equal("alice@example.org", root.GetProperty("email").GetString());
			Assert.Equal("HOMEPC", root.GetProperty("machineName").GetString());
			Assert.Equal(5180, root.GetProperty("port").GetInt32());
		}
	}

	[Fact]
	public async Task Redeem_Incomplete_Throws()
	{
		_handler.Reply = _ => Json(HttpStatusCode.OK, """{"teamDomain":"t","audience":"a","publicHost":"h","tunnelToken":""}""");

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(Redeem);

		Assert.Equal("The service answered with an incomplete result.", ex.Message);
	}

	[Fact]
	public async Task Redeem_ConflictWithError_UsesErrorText()
	{
		_handler.Reply = _ => Json(HttpStatusCode.Conflict, """{"error":"That subdomain is taken."}""");

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(Redeem);

		Assert.Equal("That subdomain is taken.", ex.Message);
	}

	[Fact]
	public async Task NetworkFailure_NamesHost()
	{
		_handler.Reply = _ => throw new HttpRequestException("connection refused");

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(Redeem);

		Assert.Equal("Could not reach svc.example.com: connection refused", ex.Message);
		Assert.Null(ex.StatusCode);
	}

	[Fact]
	public async Task Timeout_Message()
	{
		// A cancellation the caller did not ask for is the client's own timeout.
		_handler.Reply = _ => throw new OperationCanceledException();

		var ex = await Assert.ThrowsAsync<HostedProvisioningException>(() => _client.GetZoneAsync(_link, TestContext.Current.CancellationToken));

		Assert.Equal("Could not reach svc.example.com: timed out.", ex.Message);
	}

	[Fact]
	public async Task CallerCancellation_Propagates()
	{
		using (var cts = new CancellationTokenSource())
		{
			await cts.CancelAsync();
			_handler.Reply = _ => throw new OperationCanceledException();

			await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _client.GetZoneAsync(_link, cts.Token));
		}
	}

	private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
		new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private Task<RemoteAccessResult> Redeem() =>
		_client.RedeemAsync(_link, "alice", "alice@example.org", "HOMEPC", 5180, TestContext.Current.CancellationToken);

	private sealed class StubHandler : HttpMessageHandler
	{
		public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.OK);

		public string? Seen { get; private set; }

		public string? Body { get; private set; }

		protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Seen = $"{request.Method} {request.RequestUri}";
			Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
			return Reply(request);
		}
	}
}
