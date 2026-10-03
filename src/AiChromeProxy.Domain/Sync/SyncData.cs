using System.Buffers.Text;

namespace AiChromeProxy.Domain.Sync;

/// <summary>
/// Encoding of <see cref="SyncChunkPayload.Data"/>: base64url (RFC 4648 §5, <c>-</c> and <c>_</c>) without padding.
/// Plain base64 would not do: JSON encoders escape <c>+</c> as <c>\u002B</c>, so a chunk of adversarial bytes could grow sixfold past SignalR's 32 KB limit.
/// </summary>
public static class SyncData
{
	public static string Encode(ReadOnlySpan<byte> data) => Base64Url.EncodeToString(data);

	/// <exception cref="FormatException"><paramref name="data"/> is not base64url (for example plain base64 with <c>+</c> or <c>/</c>).</exception>
	public static byte[] Decode(string data) => Base64Url.DecodeFromChars(data);
}
