using System.Reflection;
using System.Text.Json;

namespace AiChromeProxy.Domain;

/// <summary>
/// The version a release stamps on every assembly (<c>-p:Version</c>). The Server reports its own in the <c>pong</c> payload
/// (optional <see cref="PongField"/>), so a client left running from an older (or newer) release can offer a reload.
/// </summary>
public static class ProductVersion
{
	public const string PongField = "serverVersion";

	/// <summary>The informational version without build metadata (<c>+sha</c>); empty when the assembly has none.</summary>
	public static string Of(Assembly assembly) =>
		(assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty).Split('+')[0];

	/// <summary>Whether a pong names a server version other than <paramref name="clientVersion"/>; a server that names none (older) never differs.</summary>
	public static bool ServerDiffers(JsonElement pong, string clientVersion) =>
		pong.ValueKind == JsonValueKind.Object
		&& pong.TryGetProperty(PongField, out var server)
		&& server.ValueKind == JsonValueKind.String
		&& server.GetString() is { Length: > 0 } version
		&& version != clientVersion;
}
