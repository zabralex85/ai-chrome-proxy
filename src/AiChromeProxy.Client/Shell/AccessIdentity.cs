using System.Text.Json;

namespace AiChromeProxy.Client.Shell;

/// <summary>The signed-in user as Cloudflare Access reports it (same origin, answered by Cloudflare before the request reaches the server).</summary>
public static class AccessIdentity
{
	/// <summary>Shown when there is no Access identity (Development, a direct LAN connection, any failure).</summary>
	public const string Fallback = "local";

	/// <summary>The <c>email</c> of <c>GET /cdn-cgi/access/get-identity</c>, or <see cref="Fallback"/>. Never throws except on cancellation.</summary>
	public static async Task<string> EmailAsync(HttpClient http, CancellationToken ct = default)
	{
		try
		{
			using (var response = await http.GetAsync("/cdn-cgi/access/get-identity", ct))
			{
				if (!response.IsSuccessStatusCode)
				{
					return Fallback;
				}

				// Without Access the server's SPA fallback answers with index.html: not JSON, so the parse fails.
				using (var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)))
				{
					return json.RootElement.ValueKind == JsonValueKind.Object
						&& json.RootElement.TryGetProperty("email", out var email)
						&& email.ValueKind == JsonValueKind.String
						&& !string.IsNullOrWhiteSpace(email.GetString())
						? email.GetString()!
						: Fallback;
				}
			}
		}
		catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
		{
			return Fallback;
		}
	}
}
