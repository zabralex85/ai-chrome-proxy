namespace AiChromeProxy.Domain.Chat;

/// <summary>
/// <c>chat.attach</c>: part of a file for the next message. <paramref name="Id"/> is 32 lowercase hex characters chosen by the client;
/// <paramref name="Data"/> is base64url without padding, at most <see cref="ChatLimits.AttachmentChunkSize"/> bytes raw. Chunks come in order
/// (offset 0 starts the upload over) and only the <paramref name="Last"/> one is answered (<c>chat.attached</c>, or the upload's first error).
/// </summary>
public sealed record ChatAttachPayload(string Repo, string Id, string Name, long Size, long Offset, string Data, bool Last);
