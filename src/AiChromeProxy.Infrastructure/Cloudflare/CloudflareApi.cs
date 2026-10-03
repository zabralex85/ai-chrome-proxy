using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiChromeProxy.Infrastructure.Cloudflare;

/// <summary>
/// Cloudflare REST API v4 with a user API token: the <c>{ success, errors, result, result_info }</c> envelope, error mapping,
/// one wait on HTTP 429 and pagination. The token goes only into the <c>Authorization</c> header of requests to <see cref="BaseUrl"/>.
/// </summary>
/// <param name="delay">Waits before the 429 retry; tests pass a recorder. Default <see cref="Task.Delay(TimeSpan, CancellationToken)"/>.</param>
public sealed class CloudflareApi(HttpClient http, string apiToken, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
	public const string BaseUrl = "https://api.cloudflare.com/client/v4/";
	public const int PageSize = 50;

	/// <summary>Longest <c>Retry-After</c> honoured; also the wait when the header is missing.</summary>
	public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

	private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

	private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

	/// <summary><c>GET user/tokens/verify</c>: the token exists and is active.</summary>
	public async Task VerifyTokenAsync(CancellationToken ct)
	{
		var token = await SendAsync<TokenStatus>(HttpMethod.Get, "user/tokens/verify", null, ct);
		if (token.Status != "active")
		{
			throw new CloudflareApiException($"The API token is {token.Status}, not active.");
		}
	}

	/// <summary>Active zones the token can read; each carries its account (no account permission needed).</summary>
	public Task<IReadOnlyList<CloudflareZone>> ListZonesAsync(CancellationToken ct) => ListAsync<CloudflareZone>("zones?status=active", ct);

	/// <summary>One call; <paramref name="body"/> (if any) is sent as JSON with snake_case names.</summary>
	/// <returns>The envelope's <c>result</c>.</returns>
	/// <exception cref="CloudflareApiException">Non-2xx status, <c>success: false</c>, a second 429, no result or an unreadable response.</exception>
	public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		var envelope = await SendEnvelopeAsync<T>(method, path, body, ct);
		return envelope.Result ?? throw new CloudflareApiException($"Cloudflare API returned no result on {method} {PathOnly(path)}.");
	}

	/// <summary>All pages of a list (<c>page</c> and <c>per_page</c> are appended to <paramref name="path"/>), following <c>result_info.total_pages</c>.</summary>
	public async Task<IReadOnlyList<T>> ListAsync<T>(string path, CancellationToken ct)
	{
		var items = new List<T>();
		var separator = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
		for (var page = 1; ; page++)
		{
			var envelope = await SendEnvelopeAsync<List<T>>(HttpMethod.Get, $"{path}{separator}page={page}&per_page={PageSize}", null, ct);
			items.AddRange(envelope.Result ?? []);
			if (page >= (envelope.ResultInfo?.TotalPages ?? 1))
			{
				return items;
			}
		}
	}

	/// <summary>Error messages name the endpoint without its query (an email address may be in it).</summary>
	private static string PathOnly(string path) => path.Split('?')[0];

	private static CloudflareApiException Error(HttpMethod method, string path, HttpStatusCode status, ApiError? first) =>
		first is null
			? new CloudflareApiException($"Cloudflare API returned HTTP {(int)status} on {method} {PathOnly(path)}.", 0, status)
			: new CloudflareApiException($"Cloudflare API error {first.Code} on {method} {PathOnly(path)}: {first.Message}", first.Code, status);

	private async Task<Envelope<T>> SendEnvelopeAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
	{
		var json = body is null ? null : JsonSerializer.Serialize(body, Json);
		for (var attempt = 1; ; attempt++)
		{
			using var request = new HttpRequestMessage(method, BaseUrl + path);
			request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiToken);
			if (json is not null)
			{
				request.Content = new StringContent(json, Encoding.UTF8, "application/json");
			}

			using var response = await http.SendAsync(request, ct);
			if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
			{
				var wait = response.Headers.RetryAfter?.Delta ?? MaxRetryAfter;
				await _delay(wait < MaxRetryAfter ? wait : MaxRetryAfter, ct);
				continue;
			}

			Envelope<T>? envelope;
			try
			{
				envelope = await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct);
			}
			catch (JsonException) when (!response.IsSuccessStatusCode)
			{
				// Not an API envelope (e.g. an HTML error page from a proxy): report the status.
				envelope = null;
			}
			catch (JsonException ex)
			{
				throw new CloudflareApiException($"Cloudflare API returned an unreadable response on {method} {PathOnly(path)}: {ex.Message}", 0, response.StatusCode);
			}

			if (!response.IsSuccessStatusCode || envelope is not { Success: true })
			{
				throw Error(method, path, response.StatusCode, envelope?.Errors?.FirstOrDefault());
			}

			return envelope;
		}
	}

	private sealed record Envelope<T>(bool Success, IReadOnlyList<ApiError>? Errors, T? Result, ResultInfo? ResultInfo);

	private sealed record ApiError(int Code, string Message);

	private sealed record ResultInfo(int TotalPages);

	private sealed record TokenStatus(string Status);
}
