namespace AiChromeProxy.Domain.Chat;

/// <summary><c>chat.attached</c>: the attachment is stored; <c>chat.send</c> may name <paramref name="Id"/>.</summary>
public sealed record ChatAttachedPayload(string Repo, string Id);
