using AiChromeProxy.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;

namespace AiChromeProxy.Server.Security;

/// <summary>Rejects every request (static files, hub, WebSocket upgrade) without a valid Access JWT.</summary>
public sealed class CloudflareAccessMiddleware(
	RequestDelegate next,
	IOptions<CloudflareAccessOptions> options,
	CloudflareAccessTokenValidator validator)
{
	public const string HeaderName = "Cf-Access-Jwt-Assertion";
	public const string CookieName = "CF_Authorization";

	/// <summary><see cref="HttpContext.Items"/> key holding the validated token's <c>email</c> claim (absent when Access is disabled).</summary>
	public const string EmailItem = "aicp.access.email";

	public async Task InvokeAsync(HttpContext context)
	{
		if (!options.Value.Enabled)
		{
			await next(context);
			return;
		}

		string? token = context.Request.Headers[HeaderName];
		if (string.IsNullOrEmpty(token))
		{
			token = context.Request.Cookies[CookieName];
		}

		if (string.IsNullOrEmpty(token) || !await validator.ValidateAsync(token, context.RequestAborted))
		{
			context.Response.StatusCode = StatusCodes.Status401Unauthorized;
			return;
		}

		// Validated above: reading a claim needs no second check.
		context.Items[EmailItem] = new JsonWebToken(token).TryGetPayloadValue<string>("email", out var email) ? email : null;
		await next(context);
	}
}
