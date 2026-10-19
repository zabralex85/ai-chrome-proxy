using System.Text.Json.Serialization;

namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.cancel</c>: stops a run; echoed back. A client that does not know the run id sends <paramref name="Repo"/> instead: that folder's run is stopped.</summary>
public sealed record ChatCancelPayload(string? RunId, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Repo = null);
