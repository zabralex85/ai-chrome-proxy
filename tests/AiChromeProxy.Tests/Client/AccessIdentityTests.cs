using System.Net;
using System.Text;
using AiChromeProxy.Client.Shell;

namespace AiChromeProxy.Tests.Client;

public sealed class AccessIdentityTests
{
	private static CancellationToken Ct => TestContext.Current.CancellationToken;

	[Fact]
	public async Task Email_FromIdentityJson_RequestedAtTheOriginRoot()
	{
		var handler = new Handler(_ => Json("""{"email":"dev@example.com","name":"Dev"}"""));
		using (var http = new HttpClient(handler) { BaseAddress = new Uri("https://app.example.com/sub/") })
		{
			Assert.Equal("dev@example.com", await AccessIdentity.EmailAsync(http, Ct));
		}

		Assert.Equal("https://app.example.com/cdn-cgi/access/get-identity", handler.Requested?.ToString());
	}

	[Theory]
	[InlineData("<!DOCTYPE html><html></html>")]
	[InlineData("""{"name":"Dev"}""")]
	[InlineData("""{"email":""}""")]
	[InlineData("""{"email":42}""")]
	[InlineData("[]")]
	public async Task Email_NotAnIdentity_Local(string body)
	{
		using (var http = new HttpClient(new Handler(_ => Json(body))) { BaseAddress = new Uri("https://app.example.com/") })
		{
			Assert.Equal(AccessIdentity.Fallback, await AccessIdentity.EmailAsync(http, Ct));
		}
	}

	[Fact]
	public async Task Email_ErrorStatusOrFailure_Local()
	{
		using (var notFound = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new Uri("https://app.example.com/") })
		using (var failing = new HttpClient(new Handler(_ => throw new HttpRequestException("offline"))) { BaseAddress = new Uri("https://app.example.com/") })
		{
			Assert.Equal("local", await AccessIdentity.EmailAsync(notFound, Ct));
			Assert.Equal("local", await AccessIdentity.EmailAsync(failing, Ct));
		}
	}

	private static HttpResponseMessage Json(string body) =>
		new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

	private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
	{
		public Uri? Requested { get; private set; }

		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
		{
			Requested = request.RequestUri;
			return Task.FromResult(reply(request));
		}
	}
}
