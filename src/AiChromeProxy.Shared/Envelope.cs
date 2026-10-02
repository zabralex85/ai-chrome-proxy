using System.Text.Json;

namespace AiChromeProxy.Shared;

/// <summary>Single wire message for every feature; routed by <see cref="Type"/>.</summary>
public sealed record Envelope(string Type, JsonElement Payload, string? CorrelationId = null)
{
	public static Envelope Create<T>(string type, T payload, string? correlationId = null) =>
		new(type, JsonSerializer.SerializeToElement(payload, JsonSerializerOptions.Web), correlationId);
}
