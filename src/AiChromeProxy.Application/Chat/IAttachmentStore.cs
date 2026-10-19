namespace AiChromeProxy.Application.Chat;

/// <summary>
/// Where chat attachments live: <c>&lt;root&gt;\&lt;repo&gt;\&lt;id&gt;\&lt;name&gt;</c>, a file being uploaded as <c>&lt;name&gt;.part</c>.
/// Callers pass sanitized repo names, checked ids and sanitized names; a path that would still leave the root throws <see cref="ArgumentException"/>.
/// </summary>
public interface IAttachmentStore
{
	/// <summary>The repo's attachments folder (absolute; it may not exist yet).</summary>
	string RepoFolder(string repo);

	/// <summary>Empties the attachment's folder and creates an empty <c>&lt;name&gt;.part</c> in it.</summary>
	void Begin(string repo, string id, string name);

	/// <summary>Appends to <c>&lt;name&gt;.part</c>; returns its new length.</summary>
	long Append(string repo, string id, string name, byte[] data);

	/// <summary>Renames <c>&lt;name&gt;.part</c> to <c>&lt;name&gt;</c>.</summary>
	void Commit(string repo, string id, string name);

	/// <summary>The attachment's finished file, or null.</summary>
	StoredAttachment? Find(string repo, string id);

	/// <summary>Deletes the attachment's folder; no error when it is gone.</summary>
	void Delete(string repo, string id);

	/// <summary>
	/// Deletes attachment folders whose newest file was written before <paramref name="olderThan"/>, or before <paramref name="partsOlderThan"/>
	/// when the folder holds an unfinished <c>.part</c>; returns how many folders were deleted. A folder that cannot be deleted now is skipped.
	/// </summary>
	int Sweep(DateTimeOffset olderThan, DateTimeOffset partsOlderThan);
}
