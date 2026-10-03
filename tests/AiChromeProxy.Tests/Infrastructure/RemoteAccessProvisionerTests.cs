using System.Net;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

public sealed class RemoteAccessProvisionerTests : IDisposable
{
	public const string Host = "code.example.com";
	public const string TunnelId = "c1744f8b-faa1-48a4-9e5c-02ac921467fa";
	public const string TunnelToken = "eyJhIjoidHVubmVsLXRva2VuIn0";
	public const string Policy = "AI Chrome Proxy — code.example.com";

	public static readonly CloudflareZone Zone = new("z1", "example.com", new CloudflareAccount("a1", "Jane's account"));

	private readonly FakeCloudflareHandler _handler = new();
	private readonly HttpClient _http;
	private readonly List<string> _progress = [];

	public RemoteAccessProvisionerTests() => _http = new HttpClient(_handler);

	/// <summary>Cloudflare as a fresh account sees it: Access enabled, nothing of ours exists yet; every create succeeds.</summary>
	public static FakeCloudflareHandler FreshAccount(FakeCloudflareHandler handler) => handler
		.On("GET", "accounts/a1/access/organizations", """{"name":"Jane","auth_domain":"jane.cloudflareaccess.com"}""")
		.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", "[]", totalPages: 0)
		.On("POST", "accounts/a1/cfd_tunnel", $$"""{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}""")
		.On("GET", $"accounts/a1/cfd_tunnel/{TunnelId}/token", $"\"{TunnelToken}\"")
		.On("PUT", $"accounts/a1/cfd_tunnel/{TunnelId}/configurations", """{"tunnel_id":"t","version":1}""")
		.On("GET", "zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", "[]", totalPages: 0)
		.On("POST", "zones/z1/dns_records", """{"id":"d1"}""")
		.On("GET", "accounts/a1/access/policies?page=1&per_page=50", """[{"id":"other","name":"Somebody else's"}]""", totalPages: 1)
		.On("POST", "accounts/a1/access/policies", $$"""{"id":"p1","name":"{{Policy}}"}""")
		.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"x","domain":"blog.example.com","aud":"other"}]""", totalPages: 1)
		.On("POST", "accounts/a1/access/apps", """{"id":"app1","domain":"code.example.com","aud":"aud-123"}""");

	[Fact]
	public async Task FreshAccount_CreatesEverything_InOrder_WithExactBodies()
	{
		FreshAccount(_handler);

		var result = await ProvisionAsync();

		Assert.Equal(new RemoteAccessResult("jane.cloudflareaccess.com", "aud-123", Host, TunnelToken), result);
		Assert.Equal(
			[
				"GET accounts/a1/access/organizations",
				"GET accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50",
				"GET zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50",
				"POST accounts/a1/cfd_tunnel",
				$"GET accounts/a1/cfd_tunnel/{TunnelId}/token",
				$"PUT accounts/a1/cfd_tunnel/{TunnelId}/configurations",
				"POST zones/z1/dns_records",
				"GET accounts/a1/access/policies?page=1&per_page=50",
				"POST accounts/a1/access/policies",
				"GET accounts/a1/access/apps?page=1&per_page=50",
				"POST accounts/a1/access/apps",
			],
			_handler.Calls);
		AssertBody("POST", "accounts/a1/cfd_tunnel", """{"name":"ai-chrome-proxy-homepc","config_src":"cloudflare"}""");
		AssertBody(
			"PUT",
			$"accounts/a1/cfd_tunnel/{TunnelId}/configurations",
			"""{"config":{"ingress":[{"hostname":"code.example.com","service":"http://127.0.0.1:5180"},{"service":"http_status:404"}]}}""");
		AssertBody(
			"POST",
			"zones/z1/dns_records",
			$$"""{"type":"CNAME","name":"code.example.com","content":"{{TunnelId}}.cfargotunnel.com","proxied":true,"ttl":1}""");
		AssertBody(
			"POST",
			"accounts/a1/access/policies",
			$$$"""{"name":"{{{Policy}}}","decision":"allow","include":[{"email":{"email":"jane@example.com"}},{"email":{"email":"joe@example.com"}}]}""");
		AssertBody(
			"POST",
			"accounts/a1/access/apps",
			"""{"name":"AI Chrome Proxy","type":"self_hosted","domain":"code.example.com","session_duration":"24h","policies":[{"id":"p1","precedence":1}]}""");
		Assert.Equal(
			[
				"Zero Trust team domain: jane.cloudflareaccess.com",
				"Tunnel ai-chrome-proxy-homepc: created",
				"Tunnel route: code.example.com → http://127.0.0.1:5180",
				"DNS record code.example.com: CNAME created",
				$"Access policy {Policy}: created (jane@example.com, joe@example.com)",
				"Access application code.example.com: created",
			],
			_progress);
	}

	[Fact]
	public async Task SecondRun_ReusesEverything_UpdatesPolicyAndApp_NoDuplicates()
	{
		_handler
			.On("GET", "accounts/a1/access/organizations", """{"auth_domain":"jane.cloudflareaccess.com"}""")
			.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", $$"""[{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}]""", totalPages: 1)
			.On("GET", $"accounts/a1/cfd_tunnel/{TunnelId}/token", $"\"{TunnelToken}\"")
			.On("PUT", $"accounts/a1/cfd_tunnel/{TunnelId}/configurations", "{}")
			.On("GET", "zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", $$"""[{"id":"d1","type":"CNAME","content":"{{TunnelId}}.CFARGOTUNNEL.com","proxied":true}]""", totalPages: 1)
			.On("GET", "accounts/a1/access/policies?page=1&per_page=50", "[]", totalPages: 2)
			.On("GET", "accounts/a1/access/policies?page=2&per_page=50", $$"""[{"id":"p1","name":"{{Policy}}"}]""", totalPages: 2)
			.On("PUT", "accounts/a1/access/policies/p1", $$"""{"id":"p1","name":"{{Policy}}"}""")
			.On("GET", "accounts/a1/access/apps?page=1&per_page=50", """[{"id":"app1","domain":"CODE.example.com","aud":"aud-old"}]""", totalPages: 1)
			.On("PUT", "accounts/a1/access/apps/app1", """{"id":"app1","domain":"code.example.com","aud":"aud-123"}""");

		var result = await ProvisionAsync("ann@example.org");

		Assert.Equal("aud-123", result.Audience);
		Assert.DoesNotContain(_handler.Requests, r => r.Method is "POST" or "PATCH");
		AssertBody("PUT", "accounts/a1/access/policies/p1", $$$"""{"name":"{{{Policy}}}","decision":"allow","include":[{"email":{"email":"ann@example.org"}}]}""");
		AssertBody(
			"PUT",
			"accounts/a1/access/apps/app1",
			"""{"name":"AI Chrome Proxy","type":"self_hosted","domain":"code.example.com","session_duration":"24h","policies":[{"id":"p1","precedence":1}]}""");
		Assert.Equal(
			[
				"Zero Trust team domain: jane.cloudflareaccess.com",
				"Tunnel ai-chrome-proxy-homepc: reused",
				"Tunnel route: code.example.com → http://127.0.0.1:5180",
				"DNS record code.example.com: CNAME kept",
				$"Access policy {Policy}: updated (ann@example.org)",
				"Access application code.example.com: updated",
			],
			_progress);
	}

	[Fact]
	public async Task OurCnameUnproxied_PatchedToProxied()
	{
		FreshAccount(_handler)
			.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", $$"""[{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}]""", totalPages: 1)
			.On("GET", "zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", $$"""[{"id":"d1","type":"CNAME","content":"{{TunnelId}}.cfargotunnel.com","proxied":false}]""", totalPages: 1)
			.On("PATCH", "zones/z1/dns_records/d1", """{"id":"d1"}""");

		await ProvisionAsync();

		AssertBody("PATCH", "zones/z1/dns_records/d1", """{"proxied":true}""");
		Assert.DoesNotContain("POST zones/z1/dns_records", _handler.Calls);
		Assert.Contains("DNS record code.example.com: CNAME switched to proxied", _progress);
	}

	[Theory]
	[InlineData("A", "203.0.113.10")]
	[InlineData("CNAME", "other.example.net")]
	public async Task ForeignDnsRecord_Refused_BeforeAnyChange(string type, string content)
	{
		FreshAccount(_handler)
			.On("GET", "zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", $$"""[{"id":"d9","type":"{{type}}","content":"{{content}}","proxied":true}]""", totalPages: 1);

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("code.example.com already has a DNS record; choose another subdomain or delete it.", ex.Message);
		Assert.Equal("GET zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50", _handler.Calls.Last());
		Assert.All(_handler.Requests, r => Assert.Equal("GET", r.Method));
		Assert.Equal(["Zero Trust team domain: jane.cloudflareaccess.com"], _progress);
	}

	[Fact]
	public async Task OurCnameAndAForeignRecord_Refused_BeforeAnyChange()
	{
		FreshAccount(_handler)
			.On("GET", "accounts/a1/cfd_tunnel?name=ai-chrome-proxy-homepc&is_deleted=false&page=1&per_page=50", $$"""[{"id":"{{TunnelId}}","name":"ai-chrome-proxy-homepc"}]""", totalPages: 1)
			.On(
				"GET",
				"zones/z1/dns_records?name.exact=code.example.com&page=1&per_page=50",
				$$"""[{"id":"d1","type":"CNAME","content":"{{TunnelId}}.cfargotunnel.com","proxied":true},{"id":"d9","type":"TXT","content":"v=spf1 -all","proxied":false}]""",
				totalPages: 1);

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("code.example.com already has a DNS record; choose another subdomain or delete it.", ex.Message);
		Assert.All(_handler.Requests, r => Assert.Equal("GET", r.Method));
	}

	[Fact]
	public async Task AccessNotEnabled_ClearError_NothingCreated()
	{
		FreshAccount(_handler)
			.OnError("GET", "accounts/a1/access/organizations", HttpStatusCode.NotFound, 12130, "access.api.error.not_enabled");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.StartsWith(
			"Cloudflare Access is not enabled for this account. Enable Zero Trust once at https://one.dash.cloudflare.com (free plan), then retry.",
			ex.Message,
			StringComparison.Ordinal);
		Assert.Contains("access.api.error.not_enabled", ex.Message, StringComparison.Ordinal);
		Assert.Equal(12130, ex.Code);
		Assert.Equal(HttpStatusCode.NotFound, ex.StatusCode);
		Assert.Single(_handler.Requests);
		Assert.Empty(_progress);
	}

	[Fact]
	public async Task OrganizationWithoutAuthDomain_SameClearError()
	{
		FreshAccount(_handler).On("GET", "accounts/a1/access/organizations", "{}");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Contains(RemoteAccessProvisioner.NotEnabledMessage, ex.Message, StringComparison.Ordinal);
	}

	[Fact]
	public async Task OrganizationServerError_NotReportedAsNotEnabled()
	{
		FreshAccount(_handler).OnError("GET", "accounts/a1/access/organizations", HttpStatusCode.InternalServerError, 10001, "Internal error");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("Cloudflare API error 10001 on GET accounts/a1/access/organizations: Internal error", ex.Message);
	}

	[Fact]
	public async Task OrganizationRateLimited_NotReportedAsNotEnabled()
	{
		FreshAccount(_handler).OnError("GET", "accounts/a1/access/organizations", HttpStatusCode.TooManyRequests, 10000, "Rate limited");

		var ex = await Assert.ThrowsAsync<CloudflareApiException>(() => ProvisionAsync());

		Assert.Equal("Cloudflare API error 10000 on GET accounts/a1/access/organizations: Rate limited", ex.Message);
		Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
		Assert.Equal(2, _handler.Requests.Count);
	}

	[Fact]
	public void Names_FromMachineAndHost()
	{
		Assert.Equal("ai-chrome-proxy-homepc", RemoteAccessProvisioner.TunnelName("HOMEPC"));
		Assert.Equal(Policy, RemoteAccessProvisioner.PolicyName(Host));
		Assert.Equal(Host, new RemoteAccessRequest(Zone, "code", [], 5180, "x").PublicHost);
	}

	[Fact]
	public void Result_ToString_HidesTunnelToken()
	{
		var text = new RemoteAccessResult("t.cloudflareaccess.com", "aud", Host, TunnelToken).ToString();

		Assert.DoesNotContain(TunnelToken, text, StringComparison.Ordinal);
		Assert.Contains(Host, text, StringComparison.Ordinal);
	}

	public void Dispose() => _http.Dispose();

	private Task<RemoteAccessResult> ProvisionAsync(params string[] emails) =>
		new RemoteAccessProvisioner(new CloudflareApi(_http, "api-token", (_, _) => Task.CompletedTask)).ProvisionAsync(
			new RemoteAccessRequest(Zone, "code", emails.Length == 0 ? ["jane@example.com", "joe@example.com"] : emails, 5180, "HOMEPC"),
			new ListProgress(_progress),
			TestContext.Current.CancellationToken);

	private void AssertBody(string method, string path, string expectedJson)
	{
		var actual = _handler.Body(method, path);
		Assert.True(JsonNode.DeepEquals(JsonNode.Parse(expectedJson), actual), actual?.ToJsonString());
	}

	/// <summary>Synchronous <see cref="IProgress{T}"/> (the BCL <c>Progress</c> posts to the thread pool).</summary>
	private sealed class ListProgress(List<string> lines) : IProgress<string>
	{
		public void Report(string value) => lines.Add(value);
	}
}
