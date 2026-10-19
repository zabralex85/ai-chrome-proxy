namespace AiChromeProxy.Client.Chat;

/// <summary>Where a file of the chat box stands.</summary>
public enum AttachmentState
{
	/// <summary>Being sent to the server.</summary>
	Uploading,

	/// <summary>On the server; the message can name <see cref="PendingAttachment.Id"/>.</summary>
	Ready,

	/// <summary>The upload failed (<see cref="PendingAttachment.Error"/>); remove it and attach it again.</summary>
	Failed,
}

/// <summary>A file waiting in the chat box.</summary>
public sealed record PendingAttachment(AttachmentFile File, AttachmentState State, long Sent = 0, string? Id = null, string? Error = null);
