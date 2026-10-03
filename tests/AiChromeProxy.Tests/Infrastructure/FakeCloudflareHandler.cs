using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using AiChromeProxy.Infrastructure.Cloudflare;

namespace AiChromeProxy.Tests.Infrastructure;

/// <summary>
/// In-memory Cloudflare API: canned v4 envelopes per <c>"METHOD path?query"</c> (relative to <see cref="CloudflareApi.BaseUrl"/>),
/// every request recorded. Registering a route again replaces it; a route given several responses plays them in order and then repeats
/// the last; an unknown route answers 404.
/// </summary>
public sealed class FakeCloudflareHandler : HttpMessageHandler
{
	private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _routes = [];

	public List<Request> Requests { get; } = [];

	/// <summary>"METHOD path?query" of each request, in order.</summary>
	public IEnumerable<string> Calls => Requests.Select(r => $"{r.Method} {r.Path}");

	/// <summary><c>{"success":true,"errors":[],"result":<paramref name="resultJson"/>}</c>, plus <c>result_info</c> when <paramref name="totalPages"/> is given.</summary>
	public static string Envelope(string resultJson, int? totalPages = null)
	{
		var resultInfo = totalPages is null ? string.Empty : $$""","result_info":{"page":1,"per_page":50,"total_pages":{{totalPages}}}""";
		return $$"""{"success":true,"errors":[],"messages":[],"result":{{resultJson}}{{resultInfo}}}""";
	}

	public static string ErrorEnvelope(int code, string message) =>
		$$"""{"success":false,"errors":[{"code":{{code}},"message":"{{message}}"}],"messages":[],"result":null}""";

	public static HttpResponseMessage Json(HttpStatusCode status, string json) =>
		new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

	/// <summary>A successful call returning <paramref name="resultJson"/>.</summary>
	public FakeCloudflareHandler On(string method, string path, string resultJson, int? totalPages = null) =>
		OnResponse(method, path, () => Json(HttpStatusCode.OK, Envelope(resultJson, totalPages)));

	/// <summary>A failed call: <paramref name="status"/> with one API error.</summary>
	public FakeCloudflareHandler OnError(string method, string path, HttpStatusCode status, int code, string message) =>
		OnResponse(method, path, () => Json(status, ErrorEnvelope(code, message)));

	public FakeCloudflareHandler OnResponse(string method, string path, params Func<HttpResponseMessage>[] responses)
	{
		_routes[$"{method} {path}"] = new Queue<Func<HttpResponseMessage>>(responses);
		return this;
	}

	/// <summary>The JSON body of the only request to "METHOD path".</summary>
	public JsonNode? Body(string method, string path) => Assert.Single(Requests, r => r.Method == method && r.Path == path).Body;

	protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
	{
		var url = request.RequestUri!.AbsoluteUri;
		Assert.StartsWith(CloudflareApi.BaseUrl, url, StringComparison.Ordinal);
		var path = url[CloudflareApi.BaseUrl.Length..];
		var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
		Requests.Add(new Request(request.Method.Method, path, request.Headers.Authorization?.ToString(), body));

		if (!_routes.TryGetValue($"{request.Method.Method} {path}", out var queue))
		{
			return Json(HttpStatusCode.NotFound, ErrorEnvelope(7003, $"No route for {request.Method.Method} {path}"));
		}

		return (queue.Count > 1 ? queue.Dequeue() : queue.Peek())();
	}

	public sealed record Request(string Method, string Path, string? Authorization, JsonNode? Body);
}
