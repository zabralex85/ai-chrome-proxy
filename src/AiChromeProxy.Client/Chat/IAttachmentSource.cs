namespace AiChromeProxy.Client.Chat;

/// <summary>Files pasted, dropped or picked into the chat box, kept by the browser under a handle (<c>Scripts/chat.ts</c>).</summary>
public interface IAttachmentSource
{
	/// <summary>Up to <paramref name="length"/> bytes of the file from <paramref name="offset"/>; fewer when the file is shorter, none when it is forgotten.</summary>
	Task<byte[]> ReadChunkAsync(string handle, long offset, int length);

	/// <summary>Lets the browser drop the file and its preview.</summary>
	Task ForgetAsync(string handle);
}
